use crate::{AudioPacketEncoder, Equalizer10Band, FrameAssembler, RtpCipherMode, RtpPacketBuilder, RtxRingBuffer};
use airplay_core::{AudioFormat, Result};
use crossbeam_channel::Receiver;
use parking_lot::Mutex;
use std::collections::{HashMap, VecDeque};
use std::net::{IpAddr, Ipv4Addr, SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicI64, AtomicU32, AtomicU64, AtomicU8, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const NTP_EPOCH_OFFSET: u64 = 2_208_988_800;

// ---------------------------------------------------------------------------
// Windows Real-Time Audio Threading & High-Resolution Waitable Timer
// (exact match to decompiled streamer/platform.c 0x140023020 & sender.c 0x1400191a0)
// ---------------------------------------------------------------------------
#[cfg(windows)]
mod win_rt {
    use std::ffi::c_void;
    use std::time::Instant;

    const CREATE_WAITABLE_TIMER_HIGH_RESOLUTION: u32 = 2;
    const TIMER_ALL_ACCESS: u32 = 0x001F_0003;
    const INFINITE: u32 = 0xFFFF_FFFF;
    const AVRT_PRIORITY_CRITICAL: i32 = 2;
    const THREAD_PRIORITY_HIGHEST: i32 = 2;

    #[link(name = "avrt")]
    extern "system" {
        fn AvSetMmThreadCharacteristicsW(task_name: *const u16, task_index: *mut u32) -> isize;
        fn AvSetMmThreadPriority(avrt_handle: isize, priority: i32) -> i32;
        fn AvRevertMmThreadCharacteristics(avrt_handle: isize) -> i32;
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn GetCurrentThread() -> isize;
        fn SetThreadPriority(thread: isize, priority: i32) -> i32;
        fn CreateWaitableTimerExW(
            attrs: *const c_void,
            name: *const u16,
            flags: u32,
            desired_access: u32,
        ) -> isize;
        fn SetWaitableTimerEx(
            timer: isize,
            due_time: *const i64,
            period: i32,
            completion_routine: *const c_void,
            arg: *const c_void,
            wake_context: *const c_void,
            tolerable_delay: u32,
        ) -> i32;
        fn WaitForSingleObject(handle: isize, ms: u32) -> u32;
        fn CloseHandle(handle: isize) -> i32;
        fn Sleep(ms: u32);
    }

    #[link(name = "winmm")]
    extern "system" {
        fn timeBeginPeriod(period: u32) -> u32;
        fn timeEndPeriod(period: u32) -> u32;
    }

    pub struct MmcssGuard {
        avrt_handle: isize,
    }

    impl MmcssGuard {
        pub fn enter_pro_audio() -> Self {
            let task_utf16: Vec<u16> = "Pro Audio\0".encode_utf16().collect();
            let mut task_index: u32 = 0;
            let handle = unsafe { AvSetMmThreadCharacteristicsW(task_utf16.as_ptr(), &mut task_index) };
            if handle != 0 {
                unsafe {
                    AvSetMmThreadPriority(handle, AVRT_PRIORITY_CRITICAL);
                }
            } else {
                unsafe {
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
                }
            }
            Self { avrt_handle: handle }
        }
    }

    impl Drop for MmcssGuard {
        fn drop(&mut self) {
            if self.avrt_handle != 0 {
                unsafe {
                    AvRevertMmThreadCharacteristics(self.avrt_handle);
                }
            }
        }
    }

    pub struct HighResPacer {
        timer: isize,
    }

    impl HighResPacer {
        pub fn new() -> Self {
            let timer = unsafe {
                CreateWaitableTimerExW(
                    std::ptr::null(),
                    std::ptr::null(),
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                    TIMER_ALL_ACCESS,
                )
            };
            if timer == 0 {
                unsafe {
                    timeBeginPeriod(1);
                }
            }
            Self { timer }
        }

        /// Sleep until `target` using high-res waitable timer (wakes 250 us early when > 400 us)
        /// followed by exact spin-wait, matching decompiled `sender.c:L604-655`.
        pub fn sleep_until(&self, target: Instant) {
            let now = Instant::now();
            if target <= now {
                return;
            }
            let remaining_ns = target.saturating_duration_since(now).as_nanos() as u64;
            if self.timer != 0 {
                if remaining_ns > 400_000 {
                    let due_100ns: i64 = -(((remaining_ns - 250_000) / 100) as i64);
                    let ok = unsafe {
                        SetWaitableTimerEx(
                            self.timer,
                            &due_100ns,
                            0,
                            std::ptr::null(),
                            std::ptr::null(),
                            std::ptr::null(),
                            0,
                        )
                    };
                    if ok != 0 {
                        unsafe {
                            WaitForSingleObject(self.timer, INFINITE);
                        }
                    }
                }
            } else if remaining_ns > 2_000_000 {
                let sleep_ms = ((remaining_ns - 1_000_000) / 1_000_000) as u32;
                if sleep_ms > 0 {
                    unsafe {
                        Sleep(sleep_ms);
                    }
                }
            }
            while Instant::now() < target {
                std::hint::spin_loop();
            }
        }
    }

    impl Drop for HighResPacer {
        fn drop(&mut self) {
            if self.timer != 0 {
                unsafe {
                    CloseHandle(self.timer);
                }
            } else {
                unsafe {
                    timeEndPeriod(1);
                }
            }
        }
    }
}

#[cfg(not(windows))]
mod win_rt {
    use std::time::{Duration, Instant};
    pub struct MmcssGuard;
    impl MmcssGuard {
        pub fn enter_pro_audio() -> Self { Self }
    }
    pub struct HighResPacer;
    impl HighResPacer {
        pub fn new() -> Self { Self }
        pub fn sleep_until(&self, target: Instant) {
            let now = Instant::now();
            if target > now {
                let rem = target - now;
                if rem > Duration::from_millis(2) {
                    std::thread::sleep(rem - Duration::from_millis(1));
                }
                while Instant::now() < target {
                    std::hint::spin_loop();
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// PTP Timeline Servo (exact match to decompiled timeline_servo.c 0x140006e60)
// ---------------------------------------------------------------------------
#[derive(Debug, Default)]
pub struct TimelineServo {
    locked: bool,
    cal_samples: Vec<i64>,
    baseline_ns: i64,
    filtered_ns: f64,
    engaged: bool,
    pub last_ppb: i64,
}

impl TimelineServo {
    pub fn reset(&mut self) {
        self.locked = false;
        self.cal_samples.clear();
        self.baseline_ns = 0;
        self.filtered_ns = 0.0;
        self.engaged = false;
        self.last_ppb = 0;
    }

    /// Feed a measured sender lateness sample (ns) and return clock-rate adjustment in ppb (`±150_000`).
    pub fn update(&mut self, lateness_ns: i64) -> i64 {
        if !self.locked {
            self.cal_samples.push(lateness_ns);
            if self.cal_samples.len() >= 5 {
                self.cal_samples.sort_unstable();
                self.baseline_ns = self.cal_samples[self.cal_samples.len() / 2];
                self.cal_samples.clear();
                self.filtered_ns = 0.0;
                self.engaged = false;
                self.locked = true;
            }
            self.last_ppb = 0;
            return 0;
        }

        let err_ns = (lateness_ns - self.baseline_ns) as f64;
        if err_ns.abs() > 250_000_000.0 {
            self.reset();
            return self.update(lateness_ns);
        }

        self.filtered_ns += 0.2 * (err_ns - self.filtered_ns);
        let abs_f = self.filtered_ns.abs();
        if !self.engaged && abs_f > 5_000_000.0 {
            self.engaged = true;
        } else if self.engaged && abs_f < 1_000_000.0 {
            self.engaged = false;
        }

        let ppb = if self.engaged {
            ((self.filtered_ns * 1_000_000_000.0) / 20_000_000_000.0)
                .round()
                .clamp(-150_000.0, 150_000.0) as i64
        } else {
            0
        };
        self.last_ppb = ppb;
        ppb
    }
}

#[derive(Debug, Clone)]
pub struct RtpTarget {
    pub id: u32,
    pub data_addr: SocketAddr,
    pub control_addr: Option<SocketAddr>,
    pub cipher: RtpCipherMode,
    pub ssrc: u32,
}

#[derive(Debug, Default)]
pub struct RtxStats {
    pub requests: AtomicU64,
    pub requested_packets: AtomicU64,
    pub resent_packets: AtomicU64,
    pub missing_packets: AtomicU64,
}

#[derive(Debug, Clone, Default)]
pub struct StreamerHealthSnapshot {
    pub packets: u64,
    pub rtx_requests: u64,
    pub rtx_requested: u64,
    pub rtx_resent: u64,
    pub rtx_missing: u64,
    pub underruns: u64,
    pub input_drops: u64,
    pub decoded_drops: u64,
    pub deadline_resets: u64,
    pub send_errors: u64,
    pub source_silence_ms: u64,
    pub in_standby: bool,
    pub lead_samples: u32,
    pub servo_ppb: i64,
    pub queue_depth: usize,
}

#[derive(Debug, Default)]
struct StreamerCounters {
    packets: AtomicU64,
    underruns: AtomicU64,
    input_drops: AtomicU64,
    decoded_drops: AtomicU64,
    deadline_resets: AtomicU64,
    send_errors: AtomicU64,
    queue_depth: AtomicU64,
    servo_ppb: AtomicI64,
}

#[derive(Clone)]
pub enum TimingAnchor {
    /// Classic NTP timing (20-byte `0xD4` sync packets).
    Ntp,
    /// AirPlay 2 PTP timing (28-byte `0xD7` sync packets locked to the shared `PtpClock`).
    Ptp {
        clock_id: Arc<dyn Fn() -> u64 + Send + Sync>,
        now_nanos: Arc<dyn Fn() -> u64 + Send + Sync>,
    },
}

/// Multi-target paced RTP audio sender matching decompiled `airstereo2-backend.exe`:
/// - MMCSS "Pro Audio" (`AVRT_PRIORITY_CRITICAL`) + `CreateWaitableTimerExW(HIGH_RESOLUTION)` (250 us early wake + spin)
/// - Clock-rate ppb scaling (`±200_000 ppb`) + `TimelineServo` (`±150_000 ppb`) + overrun re-anchor
/// - 1 Hz `0xD7` PTP sync (`0x90` X-bit on first sync / re-anchor, `0x80` after) with smooth `±3 ms/sync` playout lead slew
/// - Silence-wake re-anchor (`RMS < 25` x 8 frames / 200 ms arms; resume after >= 1200 ms re-anchors)
/// - True Standby (`RMS < 50` for `timeout_secs` pauses RTP without disconnecting receivers)
/// - 10-band dynamic sample-rate EQ + ALAC / L16 PCM encoding (44.1 kHz & 48 kHz)
/// - Per-target RTX retransmit cache + UDP control listener (`0x55` -> `0xD6`)
pub struct AudioStreamer {
    stop: Arc<AtomicBool>,
    targets: Arc<Mutex<Vec<RtpTarget>>>,
    eq: Arc<Mutex<Equalizer10Band>>,
    latency_samples: Arc<AtomicU32>,
    slew_target_samples: Arc<AtomicU32>,
    slew_step_samples: Arc<AtomicU32>,
    slew_active: Arc<AtomicBool>,
    vernier_offset_samples: Arc<AtomicI32>,
    base_drift_ppb: Arc<AtomicI64>,
    calibration_tone: Arc<AtomicU8>,
    silent_ms: Arc<AtomicU64>,
    standby_enabled: Arc<AtomicBool>,
    standby_timeout_secs: Arc<AtomicU64>,
    in_standby: Arc<AtomicBool>,
    force_reanchor: Arc<AtomicBool>,
    counters: Arc<StreamerCounters>,
    pub local_control_port: u16,
    pub rtx_stats: Arc<RtxStats>,
    sender_handle: Option<std::thread::JoinHandle<()>>,
    rtx_handle: Option<std::thread::JoinHandle<()>>,
}

impl AudioStreamer {
    pub fn bind_control_socket() -> Result<(UdpSocket, u16)> {
        let sock = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0))?;
        let port = sock.local_addr()?.port();
        let _ = sock.set_read_timeout(Some(Duration::from_millis(50)));
        Ok((sock, port))
    }

    #[allow(clippy::too_many_arguments)]
    pub fn start(
        format: AudioFormat,
        pcm_rx: Receiver<Vec<f32>>,
        initial_targets: Vec<RtpTarget>,
        control_sock: UdpSocket,
        start_seq: u16,
        start_rtptime: u32,
        initial_latency_samples: u32,
        timing: TimingAnchor,
    ) -> Result<Self> {
        let local_control_port = control_sock.local_addr()?.port();
        let stop = Arc::new(AtomicBool::new(false));
        let targets = Arc::new(Mutex::new(initial_targets));
        let eq = Arc::new(Mutex::new(Equalizer10Band::new(format.sample_rate)));
        let latency_samples = Arc::new(AtomicU32::new(initial_latency_samples));
        let slew_target_samples = Arc::new(AtomicU32::new(initial_latency_samples));
        let slew_step_samples = Arc::new(AtomicU32::new(0));
        let slew_active = Arc::new(AtomicBool::new(false));
        let vernier_offset_samples = Arc::new(AtomicI32::new(0));
        let base_drift_ppb = Arc::new(AtomicI64::new(0));
        let calibration_tone = Arc::new(AtomicU8::new(0)); // 0=off, 1=playing, 2=paused
        let silent_ms = Arc::new(AtomicU64::new(0));
        let standby_enabled = Arc::new(AtomicBool::new(false));
        let standby_timeout_secs = Arc::new(AtomicU64::new(300));
        let in_standby = Arc::new(AtomicBool::new(false));
        let force_reanchor = Arc::new(AtomicBool::new(false));
        let counters = Arc::new(StreamerCounters::default());
        let rtx_cache: Arc<Mutex<HashMap<IpAddr, RtxRingBuffer>>> = Arc::new(Mutex::new(HashMap::new()));
        let rtx_stats = Arc::new(RtxStats::default());

        let data_sock = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0))?;
        let _ = data_sock.set_nonblocking(true);
        let sync_sock = control_sock.try_clone()?;

        // RTX retransmit listener thread on control UDP socket
        let rtx_handle = {
            let stop_c = stop.clone();
            let cache_c = rtx_cache.clone();
            let stats_c = rtx_stats.clone();
            std::thread::Builder::new()
                .name("airplay-rtx-control".into())
                .spawn(move || {
                    tracing::info!("RTX control listener started on local UDP port {local_control_port}");
                    let mut buf = [0u8; 512];
                    let mut unexpected_logs = 0u32;
                    let mut err_logs = 0u32;
                    while !stop_c.load(Ordering::Relaxed) {
                        let (n, src) = match control_sock.recv_from(&mut buf) {
                            Ok(v) => v,
                            Err(e) => {
                                if !matches!(
                                    e.kind(),
                                    std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
                                ) && err_logs < 5
                                {
                                    err_logs += 1;
                                    tracing::warn!("RTX control_sock recv_from error: {e}");
                                }
                                continue;
                            }
                        };
                        if n < 8 {
                            continue;
                        }
                        let ptype = buf[1] & 0x7F;
                        if ptype != 0x55 {
                            if unexpected_logs < 5 {
                                unexpected_logs += 1;
                                tracing::info!(
                                    "RTX control_sock non-0x55 packet: len={n} b0=0x{:02x} b1=0x{:02x} from={src}",
                                    buf[0],
                                    buf[1]
                                );
                            }
                            continue;
                        }
                        // Exact match to decompiled `session/control.c:L386-399`:
                        // 8-byte format uses bytes 4..8 (`first_seq`, `count`);
                        // 12+-byte format (with SSRC) uses bytes 8..12.
                        let (first_seq, count) = if n == 8 {
                            (
                                u16::from_be_bytes([buf[4], buf[5]]),
                                u16::from_be_bytes([buf[6], buf[7]]).min(64),
                            )
                        } else if n >= 12 {
                            (
                                u16::from_be_bytes([buf[8], buf[9]]),
                                u16::from_be_bytes([buf[10], buf[11]]).min(64),
                            )
                        } else {
                            continue;
                        };
                        stats_c.requests.fetch_add(1, Ordering::Relaxed);
                        stats_c.requested_packets.fetch_add(count as u64, Ordering::Relaxed);

                        let cache = cache_c.lock();
                        let mut resent: u16 = 0;
                        let mut miss: u16 = 0;
                        if let Some(ring) = cache.get(&src.ip()) {
                            for offset in 0..count {
                                let seq = first_seq.wrapping_add(offset);
                                if let Some(orig_pkt) = ring.get(seq) {
                                    let seq_be = seq.to_be_bytes();
                                    let mut rtx_pkt = Vec::with_capacity(4 + orig_pkt.len());
                                    rtx_pkt.extend_from_slice(&[0x80, 0xD6, seq_be[0], seq_be[1]]);
                                    rtx_pkt.extend_from_slice(orig_pkt);
                                    let _ = control_sock.send_to(&rtx_pkt, src);
                                    stats_c.resent_packets.fetch_add(1, Ordering::Relaxed);
                                    resent += 1;
                                } else {
                                    stats_c.missing_packets.fetch_add(1, Ordering::Relaxed);
                                    miss += 1;
                                }
                            }
                        } else {
                            miss = count;
                            stats_c.missing_packets.fetch_add(count as u64, Ordering::Relaxed);
                        }
                        tracing::info!(
                            "[RTX] requested={} available={} resent={} miss={} first_seq={} from={}",
                            count,
                            resent,
                            resent,
                            miss,
                            first_seq,
                            src.ip()
                        );
                    }
                })?
        };

        // Main paced RTP audio sender thread
        let sender_handle = {
            let stop_c = stop.clone();
            let targets_c = targets.clone();
            let eq_c = eq.clone();
            let cache_c = rtx_cache.clone();
            let lat_c = latency_samples.clone();
            let slew_tgt_c = slew_target_samples.clone();
            let slew_step_c = slew_step_samples.clone();
            let slew_act_c = slew_active.clone();
            let vernier_c = vernier_offset_samples.clone();
            let drift_c = base_drift_ppb.clone();
            let tone_c = calibration_tone.clone();
            let silent_c = silent_ms.clone();
            let stby_en_c = standby_enabled.clone();
            let stby_to_c = standby_timeout_secs.clone();
            let in_stby_c = in_standby.clone();
            let reanchor_c = force_reanchor.clone();
            let cnt_c = counters.clone();

            std::thread::Builder::new()
                .name("airplay-rtp-sender".into())
                .spawn(move || {
                    let _mmcss = win_rt::MmcssGuard::enter_pro_audio();
                    let pacer = win_rt::HighResPacer::new();

                    let mut assembler = FrameAssembler::new(format);
                    let mut encoder = AudioPacketEncoder::new(format);
                    let mut pending_packets: VecDeque<Vec<i16>> = VecDeque::with_capacity(16);
                    let silence_packet =
                        vec![0i16; (format.frames_per_packet as usize) * (format.channels as usize)];

                    let sr_hz = format.sample_rate.hz() as u64;
                    let sr_f64 = sr_hz as f64;
                    let nominal_pkt_ns: u64 =
                        (format.frames_per_packet as u64 * 1_000_000_000) / sr_hz.max(1);

                    let mut seq = start_seq;
                    let mut rtptime = start_rtptime;
                    let mut pkt_count: u64 = 0;
                    let mut tone_sample_idx: u64 = 0;

                    // Pre-roll: wait up to 75 ms in 10 ms slices for initial captured frames (sender.c L1278-1338)
                    let preroll_deadline = Instant::now() + Duration::from_millis(75);
                    while pending_packets.len() < 2 && Instant::now() < preroll_deadline && !stop_c.load(Ordering::Relaxed) {
                        let rem = preroll_deadline.saturating_duration_since(Instant::now());
                        if let Ok(mut samples) = pcm_rx.recv_timeout(rem.min(Duration::from_millis(10))) {
                            eq_c.lock().process_interleaved_in_place(&mut samples);
                            for pkt in assembler.push_interleaved_f32(&samples) {
                                pending_packets.push_back(pkt);
                            }
                        }
                    }

                    // Timing & sync state (sender.c 0x140018e50, timeline_servo.c 0x140006e60)
                    let mut next_deadline = Instant::now();
                    let mut first_sync_sent = false;
                    let mut last_sync_at = Instant::now() - Duration::from_secs(2);
                    let mut last_ptp_clock_id: Option<u64> = None;
                    let mut ptp_anchor: Option<(u64, u32, u64)> = None; // (anchor_ptp_ns, last_rtp_ts, total_samples)
                    let mut servo = TimelineServo::default();

                    // Silence-wake & standby state (runner.rs 0x140350f85..0x1403535f4)
                    let mut silent_frames: u32 = 0;
                    let mut audible_frames: u32 = 0;
                    let mut silence_started_at: Option<Instant> = None;
                    let mut silence_wake_armed = true;
                    let mut standby_silence_since: Option<Instant> = None;
                    let mut was_in_standby = false;

                    while !stop_c.load(Ordering::Relaxed) {
                        // Drain captured WASAPI audio into 352-frame packets; 14-pkt cap matches original 8+6 queue
                        // so normal WASAPI jitter never triggers spurious decoded_drops (which would block Adaptive PTP).
                        while let Ok(mut samples) = pcm_rx.try_recv() {
                            eq_c.lock().process_interleaved_in_place(&mut samples);
                            for pkt in assembler.push_interleaved_f32(&samples) {
                                while pending_packets.len() >= 14 {
                                    pending_packets.pop_front();
                                    cnt_c.decoded_drops.fetch_add(1, Ordering::Relaxed);
                                }
                                pending_packets.push_back(pkt);
                            }
                        }
                        cnt_c.queue_depth.store(pending_packets.len() as u64, Ordering::Relaxed);

                        // Compute ppb-adjusted packet interval (sender.c L696-719)
                        let ppb_total = (drift_c.load(Ordering::Relaxed) + servo.last_ppb)
                            .clamp(-200_000, 200_000);
                        let denom = (1_000_000_000_i128 + ppb_total as i128) as u128;
                        let adjusted_ns = (((nominal_pkt_ns as u128 * 1_000_000_000 + denom / 2)
                            / denom) as u64)
                            .max(1);

                        // High-resolution pacing wait
                        let now = Instant::now();
                        if next_deadline > now {
                            pacer.sleep_until(next_deadline);
                        } else {
                            let behind_ns = now.saturating_duration_since(next_deadline).as_nanos() as u64;
                            // Overrun re-anchor (sender.c L910-964): budget = adjusted_ns * 4
                            if behind_ns > adjusted_ns.saturating_mul(4) {
                                next_deadline = now;
                                cnt_c.deadline_resets.fetch_add(1, Ordering::Relaxed);
                            }
                        }

                        // Drain one more time right at slot boundary
                        while let Ok(mut samples) = pcm_rx.try_recv() {
                            eq_c.lock().process_interleaved_in_place(&mut samples);
                            for pkt in assembler.push_interleaved_f32(&samples) {
                                while pending_packets.len() >= 14 {
                                    pending_packets.pop_front();
                                    cnt_c.decoded_drops.fetch_add(1, Ordering::Relaxed);
                                }
                                pending_packets.push_back(pkt);
                            }
                        }

                        let (mut pcm_i16, is_concealed) = match pending_packets.pop_front() {
                            Some(p) => (p, false),
                            None => (silence_packet.clone(), true),
                        };

                        // Per-frame 16-bit PCM RMS (runner.rs 0x140350f50)
                        let sum_sq: f64 = pcm_i16.iter().map(|&s| (s as f64) * (s as f64)).sum();
                        let rms = if pcm_i16.is_empty() {
                            0.0
                        } else {
                            (sum_sq / pcm_i16.len() as f64).sqrt()
                        };

                        let slot_now = Instant::now();
                        // Silence-wake & re-anchor detector (runner.rs 0x140350f85..0x140353098)
                        let audible = rms >= 25.0 || tone_c.load(Ordering::Relaxed) == 1;
                        if !audible {
                            audible_frames = 0;
                            silent_frames = silent_frames.saturating_add(1);
                            let started = *silence_started_at.get_or_insert(slot_now);
                            let idle_ms = slot_now.saturating_duration_since(started).as_millis() as u64;
                            silent_c.store(idle_ms, Ordering::Relaxed);
                            if !silence_wake_armed && silent_frames >= 8 && idle_ms >= 200 {
                                silence_wake_armed = true;
                                tracing::debug!(
                                    "Audio source idle for {}ms; re-armed silence wake detector",
                                    idle_ms
                                );
                            }
                        } else {
                            let prev_idle_ms = silence_started_at
                                .map(|t| slot_now.saturating_duration_since(t).as_millis() as u64)
                                .unwrap_or(0);
                            silent_frames = 0;
                            audible_frames = audible_frames.saturating_add(1);
                            silent_c.store(0, Ordering::Relaxed);
                            if silence_wake_armed && audible_frames >= 3 {
                                silence_wake_armed = false;
                                silence_started_at = None;
                                if prev_idle_ms >= 1200 {
                                    tracing::info!(
                                        "Audio source resumed after {}ms idle (RMS={:.1}); re-anchor requested=true",
                                        prev_idle_ms,
                                        rms
                                    );
                                    first_sync_sent = false;
                                    ptp_anchor = None;
                                    servo.reset();
                                }
                            } else if audible_frames >= 3 {
                                silence_started_at = None;
                            }
                        }

                        // Only count underrun when audio is NOT silent/idle
                        if is_concealed && audible && !was_in_standby {
                            cnt_c.underruns.fetch_add(1, Ordering::Relaxed);
                        }

                        // Standby state machine (runner.rs 0x140353254..0x1403535f4; threshold RMS = 50.0)
                        let stby_on = stby_en_c.load(Ordering::Relaxed);
                        if stby_on && tone_c.load(Ordering::Relaxed) != 1 {
                            if rms < 50.0 {
                                let since = *standby_silence_since.get_or_insert(slot_now);
                                let timeout_secs = stby_to_c.load(Ordering::Relaxed);
                                if !was_in_standby && slot_now.saturating_duration_since(since).as_secs() >= timeout_secs {
                                    was_in_standby = true;
                                    in_stby_c.store(true, Ordering::Relaxed);
                                    tracing::info!("Silence detected for {}s — entering standby", timeout_secs);
                                }
                            } else {
                                standby_silence_since = None;
                                if was_in_standby {
                                    was_in_standby = false;
                                    in_stby_c.store(false, Ordering::Relaxed);
                                    first_sync_sent = false;
                                    ptp_anchor = None;
                                    servo.reset();
                                    tracing::info!("Audio activity detected (RMS={:.1}) — waking from standby", rms);
                                }
                            }
                        } else {
                            standby_silence_since = None;
                            if was_in_standby {
                                was_in_standby = false;
                                in_stby_c.store(false, Ordering::Relaxed);
                                first_sync_sent = false;
                                ptp_anchor = None;
                                servo.reset();
                            }
                        }

                        // While in standby, pause RTP transmission while keeping sockets/session alive
                        if was_in_standby {
                            next_deadline = slot_now + Duration::from_nanos(adjusted_ns);
                            continue;
                        }

                        // Optional 1 kHz 40 ms calibration pulse once per second when CALIBRATION_TONE START is active
                        if tone_c.load(Ordering::Relaxed) == 1 {
                            let pulse_len = sr_hz / 25; // 40 ms
                            for frame in pcm_i16.chunks_exact_mut(2) {
                                let phase_in_sec = tone_sample_idx % sr_hz;
                                if phase_in_sec < pulse_len {
                                    let t = tone_sample_idx as f64 / sr_f64;
                                    let env = (std::f64::consts::PI * phase_in_sec as f64 / pulse_len as f64).sin();
                                    let v = ((2.0 * std::f64::consts::PI * 1000.0 * t).sin() * env * 12000.0) as i16;
                                    frame[0] = v;
                                    frame[1] = v;
                                } else {
                                    frame[0] = 0;
                                    frame[1] = 0;
                                }
                                tone_sample_idx = tone_sample_idx.wrapping_add(1);
                            }
                        }

                        if reanchor_c.swap(false, Ordering::Relaxed) {
                            first_sync_sent = false;
                            ptp_anchor = None;
                            servo.reset();
                        }

                        let encoded = encoder.encode_packet(&pcm_i16);
                        let tgts = targets_c.lock().clone();

                        // 1 Hz natural sync + immediate first sync / re-anchor (rtp.c L1665-1719, runner.rs 0x140356528)
                        let need_sync = !first_sync_sent
                            || slot_now.saturating_duration_since(last_sync_at) >= Duration::from_secs(1);
                        if need_sync {
                            // Step active playout lead slew by ±step_samples on each 1 Hz sync (3 ms/sync)
                            if first_sync_sent && slew_act_c.load(Ordering::Relaxed) {
                                let cur = lat_c.load(Ordering::Relaxed);
                                let tgt = slew_tgt_c.load(Ordering::Relaxed);
                                let step = slew_step_c.load(Ordering::Relaxed).max(1);
                                let next_lat = if cur < tgt {
                                    cur.saturating_add(step).min(tgt)
                                } else if cur > tgt {
                                    cur.saturating_sub(step).max(tgt)
                                } else {
                                    cur
                                };
                                lat_c.store(next_lat, Ordering::Relaxed);
                                if next_lat == tgt {
                                    slew_act_c.store(false, Ordering::Relaxed);
                                }
                            }

                            let cur_lat = lat_c.load(Ordering::Relaxed);
                            let vernier = vernier_c.load(Ordering::Relaxed);
                            let sync_pkt = build_timing_packet(
                                &timing,
                                &mut last_ptp_clock_id,
                                &mut ptp_anchor,
                                &mut servo,
                                sr_hz,
                                !first_sync_sent,
                                rtptime,
                                cur_lat,
                                vernier,
                            );
                            cnt_c.servo_ppb.store(servo.last_ppb, Ordering::Relaxed);
                            for t in &tgts {
                                if let Some(ctrl) = t.control_addr {
                                    if sync_sock.send_to(&sync_pkt, ctrl).is_err() {
                                        cnt_c.send_errors.fetch_add(1, Ordering::Relaxed);
                                    }
                                }
                            }
                            first_sync_sent = true;
                            last_sync_at = slot_now;
                        }

                        let mut cache = cache_c.lock();
                        for t in &tgts {
                            let builder = RtpPacketBuilder {
                                ssrc: t.ssrc,
                                payload_type: 96,
                                cipher: t.cipher.clone(),
                            };
                            if let Ok(wire) = builder.build(seq, rtptime, pkt_count == 0, pkt_count, &encoded) {
                                if data_sock.send_to(&wire, t.data_addr).is_err() {
                                    cnt_c.send_errors.fetch_add(1, Ordering::Relaxed);
                                }
                                cache
                                    .entry(t.data_addr.ip())
                                    .or_insert_with(|| RtxRingBuffer::new(1024))
                                    .insert(seq, wire);
                            }
                        }
                        drop(cache);

                        pkt_count = pkt_count.wrapping_add(1);
                        cnt_c.packets.store(pkt_count, Ordering::Relaxed);
                        seq = seq.wrapping_add(1);
                        rtptime = rtptime.wrapping_add(format.frames_per_packet);

                        // Advance deadline with 2x catch-up cap (sender.c L910-964)
                        let next_target = next_deadline + Duration::from_nanos(adjusted_ns);
                        let min_earliest = Instant::now() + Duration::from_nanos(adjusted_ns / 2);
                        next_deadline = next_target.max(min_earliest);
                    }
                })?
        };

        Ok(Self {
            stop,
            targets,
            eq,
            latency_samples,
            slew_target_samples,
            slew_step_samples,
            slew_active,
            vernier_offset_samples,
            base_drift_ppb,
            calibration_tone,
            silent_ms,
            standby_enabled,
            standby_timeout_secs,
            in_standby,
            force_reanchor,
            counters,
            local_control_port,
            rtx_stats,
            sender_handle: Some(sender_handle),
            rtx_handle: Some(rtx_handle),
        })
    }

    pub fn eq(&self) -> Arc<Mutex<Equalizer10Band>> {
        self.eq.clone()
    }

    /// Immediate playout lead override (cancels any active slew; matches `streamer/timing.c 0x1403607c0`).
    pub fn set_latency_samples(&self, samples: u32) {
        self.slew_active.store(false, Ordering::Relaxed);
        self.latency_samples.store(samples.max(1), Ordering::Relaxed);
        self.slew_target_samples.store(samples.max(1), Ordering::Relaxed);
    }

    /// Initiate smooth per-sync playout lead slew (`±step_samples` per 1 Hz sync; matches `0x140360bd0`).
    pub fn set_latency_slew(&self, target_samples: u32, step_samples: u32) {
        self.slew_target_samples.store(target_samples.max(1), Ordering::Relaxed);
        self.slew_step_samples.store(step_samples.max(1), Ordering::Relaxed);
        self.slew_active.store(true, Ordering::Relaxed);
    }

    pub fn latency_samples(&self) -> u32 {
        self.latency_samples.load(Ordering::Relaxed)
    }

    pub fn set_vernier_offset_samples(&self, samples: i32) {
        self.vernier_offset_samples.store(samples, Ordering::Relaxed);
    }

    pub fn set_base_drift_ppb(&self, ppb: i64) {
        self.base_drift_ppb.store(ppb.clamp(-100_000, 100_000), Ordering::Relaxed);
    }

    pub fn set_calibration_tone(&self, state: u8) {
        self.calibration_tone.store(state, Ordering::Relaxed);
    }

    pub fn set_standby_config(&self, enabled: bool, timeout_mins: u32) {
        self.standby_enabled.store(enabled, Ordering::Relaxed);
        self.standby_timeout_secs.store((timeout_mins as u64).saturating_mul(60), Ordering::Relaxed);
    }

    pub fn is_in_standby(&self) -> bool {
        self.in_standby.load(Ordering::Relaxed)
    }

    pub fn request_reanchor(&self) {
        self.force_reanchor.store(true, Ordering::Relaxed);
    }

    pub fn silent_ms(&self) -> u64 {
        self.silent_ms.load(Ordering::Relaxed)
    }

    pub fn health_snapshot(&self) -> StreamerHealthSnapshot {
        StreamerHealthSnapshot {
            packets: self.counters.packets.load(Ordering::Relaxed),
            rtx_requests: self.rtx_stats.requests.load(Ordering::Relaxed),
            rtx_requested: self.rtx_stats.requested_packets.load(Ordering::Relaxed),
            rtx_resent: self.rtx_stats.resent_packets.load(Ordering::Relaxed),
            rtx_missing: self.rtx_stats.missing_packets.load(Ordering::Relaxed),
            underruns: self.counters.underruns.load(Ordering::Relaxed),
            input_drops: self.counters.input_drops.load(Ordering::Relaxed),
            decoded_drops: self.counters.decoded_drops.load(Ordering::Relaxed),
            deadline_resets: self.counters.deadline_resets.load(Ordering::Relaxed),
            send_errors: self.counters.send_errors.load(Ordering::Relaxed),
            source_silence_ms: self.silent_ms.load(Ordering::Relaxed),
            in_standby: self.in_standby.load(Ordering::Relaxed),
            lead_samples: self.latency_samples.load(Ordering::Relaxed),
            servo_ppb: self.counters.servo_ppb.load(Ordering::Relaxed),
            queue_depth: self.counters.queue_depth.load(Ordering::Relaxed) as usize,
        }
    }

    pub fn update_targets(&self, new_targets: Vec<RtpTarget>) {
        *self.targets.lock() = new_targets;
    }

    pub fn stop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.sender_handle.take() {
            let _ = h.join();
        }
        if let Some(h) = self.rtx_handle.take() {
            let _ = h.join();
        }
    }
}

impl Drop for AudioStreamer {
    fn drop(&mut self) {
        self.stop();
    }
}

#[allow(clippy::too_many_arguments)]
fn build_timing_packet(
    timing: &TimingAnchor,
    last_ptp_clock_id: &mut Option<u64>,
    ptp_anchor: &mut Option<(u64, u32, u64)>,
    servo: &mut TimelineServo,
    sr_hz: u64,
    first: bool,
    rtptime: u32,
    playout_lead_samples: u32,
    vernier_offset_samples: i32,
) -> Vec<u8> {
    match timing {
        TimingAnchor::Ptp { clock_id, now_nanos } => {
            let active_cid = clock_id();
            let mut is_first = first;
            if *last_ptp_clock_id != Some(active_cid) {
                if last_ptp_clock_id.is_some() {
                    *ptp_anchor = None;
                    servo.reset();
                    is_first = true;
                }
                *last_ptp_clock_id = Some(active_cid);
            }
            let live_ptp_ns = now_nanos();
            // Monotonic RTP-to-PTP anchor (sender.c 0x140018e50) + timeline servo lateness feed
            let ptp_ts_ns = match ptp_anchor {
                Some((anchor_ns, last_rtp, total_samples)) => {
                    *total_samples = total_samples.saturating_add(rtptime.wrapping_sub(*last_rtp) as u64);
                    *last_rtp = rtptime;
                    let ideal_ns = anchor_ns.saturating_add(
                        ((*total_samples as u128 * 1_000_000_000) / sr_hz.max(1) as u128) as u64,
                    );
                    let lateness_ns = (live_ptp_ns as i128 - ideal_ns as i128) as i64;
                    let _ = servo.update(lateness_ns);
                    ideal_ns
                }
                None => {
                    *ptp_anchor = Some((live_ptp_ns, rtptime, 0));
                    live_ptp_ns
                }
            };

            let rtp_now = rtptime.wrapping_sub(playout_lead_samples);
            let rtp_next = rtptime.wrapping_sub(vernier_offset_samples as u32);

            let mut pkt = vec![0u8; 28];
            pkt[0] = if is_first { 0x90 } else { 0x80 };
            pkt[1] = 0xD7;
            pkt[2] = 0x00;
            pkt[3] = 0x06;
            pkt[4..8].copy_from_slice(&rtp_now.to_be_bytes());
            pkt[8..16].copy_from_slice(&ptp_ts_ns.to_be_bytes());
            pkt[16..20].copy_from_slice(&rtp_next.to_be_bytes());
            pkt[20..28].copy_from_slice(&active_cid.to_be_bytes());
            pkt
        }
        TimingAnchor::Ntp => {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos();
            let secs = (nanos / 1_000_000_000) as u64 + NTP_EPOCH_OFFSET;
            let frac = (((nanos % 1_000_000_000) << 32) / 1_000_000_000) as u32;
            let mut pkt = vec![0u8; 20];
            pkt[0] = if first { 0x90 } else { 0x80 };
            pkt[1] = 0xD4;
            pkt[2] = 0x00;
            pkt[3] = 0x04;
            pkt[4..8].copy_from_slice(&rtptime.wrapping_sub(playout_lead_samples).to_be_bytes());
            pkt[8..12].copy_from_slice(&(secs as u32).to_be_bytes());
            pkt[12..16].copy_from_slice(&frac.to_be_bytes());
            pkt[16..20].copy_from_slice(&rtptime.to_be_bytes());
            pkt
        }
    }
}
