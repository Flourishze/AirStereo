mod capture;
mod commands;
mod events;
mod resolver;

use airplay_audio::{AudioStreamer, StreamerHealthSnapshot, TimingAnchor};
use airplay_client::{
    probe_receiver_info, AirPlay2Connection, DacpServer, RaopConnection, ReceiverEvent,
};
use airplay_core::{AirPlayError, AudioCodec, AudioFormat, DeviceDescriptor, FrontendEndpointSelection, SampleRate};
use airplay_timing::{NtpTimingServer, PtpMasterServer};
use base64::Engine as _;
use capture::WasapiLoopbackCapture;
use commands::{parse_command, BackendCommand};
use events::EventEmitter;
use resolver::DeviceResolver;
use serde_json::json;
use std::collections::{HashMap, VecDeque};
use std::net::IpAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::io::{AsyncBufReadExt, BufReader};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;

#[derive(Debug, Clone)]
struct EqState {
    enabled: bool,
    preamp_db: f32,
    bands_db: [f32; 10],
}

impl Default for EqState {
    fn default() -> Self {
        Self {
            enabled: false,
            preamp_db: 0.0,
            bands_db: [0.0; 10],
        }
    }
}

impl EqState {
    fn apply_to(&self, eq: &mut airplay_audio::Equalizer10Band) {
        eq.set_enabled(self.enabled);
        eq.set_preamp_db(self.preamp_db);
        for (i, &db) in self.bands_db.iter().enumerate() {
            eq.set_band_db(i, db);
        }
    }
}

#[derive(Debug, Clone)]
struct PreemptedSession {
    endpoints: Vec<FrontendEndpointSelection>,
    preempted_at: Instant,
    last_attempt_at: Option<Instant>,
    takeover_num: u32,
    attempts: u32,
}

/// Adaptive PTP playout lead controller (exact match to decompiled `session/tick.c L1452-2450`
/// and `session/latency.c 0x1404c5d50 / 0x1404c6150`).
#[derive(Debug, Default)]
struct AdaptivePtpLeadController {
    baseline_ms: u32,
    target_ms: u32,
    applied_ms: u32,
    rtt_window: VecDeque<u32>,
    startup_ticks: u8,
    clean_ticks: u8,
    loss_window_mask: u8,
    loss_streak: u8,
    prev_health: Option<StreamerHealthSnapshot>,
}

impl AdaptivePtpLeadController {
    fn reset(&mut self, baseline_ms: u32) {
        self.baseline_ms = baseline_ms;
        self.target_ms = baseline_ms;
        self.applied_ms = baseline_ms;
        self.rtt_window.clear();
        self.startup_ticks = 0;
        self.clean_ticks = 0;
        self.loss_window_mask = 0;
        self.loss_streak = 0;
        self.prev_health = None;
    }

    fn median_rtt(&self) -> Option<u32> {
        if self.rtt_window.len() < 3 {
            return None;
        }
        let mut v: Vec<u32> = self.rtt_window.iter().copied().collect();
        v.sort_unstable();
        Some(v[v.len() / 2])
    }

    /// Evaluate one 2-second telemetry interval; returns `Some((new_applied_ms, slew_step_ms))` when wire lead should update.
    fn tick(
        &mut self,
        rtt_ms: u32,
        health: &StreamerHealthSnapshot,
        is_solo_homepod: bool,
        locked: bool,
    ) -> Option<(u32, u32)> {
        if self.baseline_ms == 0 {
            return None;
        }
        self.startup_ticks = self.startup_ticks.saturating_add(1);
        if self.rtt_window.len() >= 5 {
            self.rtt_window.pop_front();
        }
        self.rtt_window.push_back(rtt_ms);

        let prev = match &self.prev_health {
            Some(p) => p.clone(),
            None => {
                self.prev_health = Some(health.clone());
                return None;
            }
        };
        self.prev_health = Some(health.clone());

        let d_rtx_req = health.rtx_requested.saturating_sub(prev.rtx_requested);
        let d_rtx_res = health.rtx_resent.saturating_sub(prev.rtx_resent);
        let d_local_fault = health.underruns.saturating_sub(prev.underruns)
            | health.input_drops.saturating_sub(prev.input_drops)
            | health.decoded_drops.saturating_sub(prev.decoded_drops)
            | health.deadline_resets.saturating_sub(prev.deadline_resets)
            | health.send_errors.saturating_sub(prev.send_errors);
        let source_silence = health.source_silence_ms > 0;

        // Source silence hold (`tick.c:L1485-1527`)
        if d_rtx_req == 0 && source_silence {
            self.loss_window_mask = 0;
            self.loss_streak = 0;
            return None;
        }

        // Fault classification (`tick.c:L7149-7215`):
        // 0 = ReceiverLoss, 1 = LocalFault/Ambiguous, 2 = Clean
        let kind = if d_local_fault > 0 {
            1u8
        } else if d_rtx_req == 0 {
            2u8
        } else if d_rtx_res.saturating_mul(4) < d_rtx_req.saturating_mul(3) {
            1u8
        } else {
            0u8
        };

        let max_lead = self.baseline_ms.saturating_add(120);

        if is_solo_homepod {
            let eff_kind = if self.startup_ticks < 9 && kind == 0 { 2 } else { kind };
            match eff_kind {
                0 => {
                    self.loss_window_mask = (self.loss_window_mask << 1) | 1;
                    if self.loss_window_mask.count_ones() >= 3 {
                        self.loss_window_mask = 0;
                        self.clean_ticks = 0;
                        let step_cand = self.target_ms.saturating_add(20).min(max_lead);
                        let rtt_cand = self
                            .median_rtt()
                            .map(|m| {
                                let c = m.saturating_add(20).clamp(self.baseline_ms, max_lead);
                                (((c + 9) / 10) * 10).clamp(self.baseline_ms, max_lead)
                            })
                            .unwrap_or(self.baseline_ms);
                        self.target_ms = step_cand.max(rtt_cand).max(self.baseline_ms);
                        tracing::info!(
                            "Adaptive PTP lead: receiver loss confirmed (solo HomePod) -> target={}ms",
                            self.target_ms
                        );
                    }
                }
                1 => {
                    self.loss_window_mask = 0;
                }
                _ => {
                    self.loss_window_mask <<= 1;
                    if self.target_ms > self.baseline_ms {
                        self.clean_ticks = self.clean_ticks.saturating_add(1);
                        if self.clean_ticks >= 15 {
                            self.clean_ticks = 0;
                            self.target_ms = self.target_ms.saturating_sub(20).max(self.baseline_ms);
                        }
                    }
                }
            }
        } else {
            match kind {
                0 => {
                    self.loss_streak = self.loss_streak.saturating_add(1);
                    self.clean_ticks = 0;
                    if self.loss_streak >= 3 {
                        self.loss_streak = 0;
                        let step_cand = self.target_ms.saturating_add(20).min(max_lead);
                        let rtt_cand = self
                            .median_rtt()
                            .map(|m| {
                                let c = m.saturating_add(20).clamp(self.baseline_ms, max_lead);
                                (((c + 9) / 10) * 10).clamp(self.baseline_ms, max_lead)
                            })
                            .unwrap_or(self.baseline_ms);
                        self.target_ms = step_cand.max(rtt_cand).max(self.baseline_ms);
                        tracing::info!(
                            "Adaptive PTP lead: receiver loss confirmed (group) -> target={}ms",
                            self.target_ms
                        );
                    }
                }
                1 => {
                    self.loss_streak = 0;
                }
                _ => {
                    self.loss_streak = 0;
                    if self.target_ms > self.baseline_ms {
                        self.clean_ticks = self.clean_ticks.saturating_add(1);
                        if self.clean_ticks >= 15 {
                            self.clean_ticks = 0;
                            self.target_ms = self.baseline_ms;
                        }
                    }
                }
            }
        }

        if locked {
            self.applied_ms = self.baseline_ms;
            return None;
        }

        let desired = self.target_ms.max(self.baseline_ms);
        let interval_name = match kind {
            0 => "ReceiverLoss",
            1 => "LocalFault",
            _ => "Healthy",
        };
        if self.applied_ms == desired {
            if d_rtx_req > 0 {
                tracing::info!(
                    "Adaptive PTP hold: applied={}ms target={}ms RTT={}ms median={:?}ms interval={} rtx_req={} rtx_res={} decoded_drops={} underruns={} streak={} baseline={}ms",
                    self.applied_ms, desired, rtt_ms, self.median_rtt(), interval_name,
                    d_rtx_req, d_rtx_res,
                    health.decoded_drops.saturating_sub(prev.decoded_drops),
                    health.underruns.saturating_sub(prev.underruns),
                    self.loss_streak, self.baseline_ms
                );
            }
            return None;
        }
        let prev_applied = self.applied_ms;
        let next = if self.applied_ms < desired {
            self.applied_ms.saturating_add(3).min(desired)
        } else {
            self.applied_ms.saturating_sub(3).max(desired)
        };
        self.applied_ms = next;
        tracing::info!(
            "Adaptive PTP playout lead slew: applied {} -> {}ms (target={}ms, RTT={}ms, median={:?}ms, interval={}, rtx_req={}, rtx_res={}, decoded_drops={}, underruns={}, streak={}, baseline={}ms)",
            prev_applied, self.applied_ms, desired, rtt_ms, self.median_rtt(), interval_name,
            d_rtx_req, d_rtx_res,
            health.decoded_drops.saturating_sub(prev.decoded_drops),
            health.underruns.saturating_sub(prev.underruns),
            self.loss_streak, self.baseline_ms
        );
        Some((self.applied_ms, 3))
    }
}

/// Exact decompiled baseline playout lead (`session/latency.c 0x1404c5d50`):
/// - Group (> 1 device): 120 ms
/// - Solo HomePod: 50 ms (hard floor 50 ms for ALAC)
/// - Solo Apple TV: 80 ms
/// - Solo AirPort Express 2 (`airport10,`): 60 ms
/// - Classic RAOP fallback: 220 ms
fn compute_baseline_playout_ms(
    resolver: &DeviceResolver,
    endpoints: &[FrontendEndpointSelection],
    use_raop: bool,
) -> u32 {
    if use_raop {
        return 220;
    }
    if endpoints.len() > 1 {
        return 120;
    }
    if let Some(first) = endpoints.first() {
        if let Some(dev) = resolver.resolve_hinted(first) {
            let m = dev.model.to_ascii_lowercase();
            let n = dev.name.to_ascii_lowercase();
            if m.starts_with("audioaccessory") || n.contains("homepod") {
                return 50;
            }
            if m.starts_with("appletv") || n.contains("apple tv") {
                return 80;
            }
            if m.starts_with("airport10,") {
                return 60;
            }
        }
    }
    120
}

fn is_encrypted_control_eof(msg: &str) -> bool {
    let low = msg.to_ascii_lowercase();
    low.contains("early eof")
        || low.contains("unexpected eof")
        || low.contains("connection reset")
        || low.contains("connection abort")
        || low.contains("broken pipe")
        || low.contains("10053")
        || low.contains("10054")
        || low.contains("os error 104")
        || low.contains("eof")
        || low.contains("closed")
}

/// Exact decompiled RTSP SETUP protocol window (`session/latency.c 0x14049ae00` & `policy.c`):
/// - `base_samples`: 70 ms (3087 @ 44.1 kHz, 3360 @ 48 kHz)
/// - `span_samples`: 100 ms (4410 @ 44.1 kHz, 4800 @ 48 kHz)
/// - `extra_ms`: 160 ms default; 250 ms Apple TV / AirPort; 330 ms AirPort4 + ALAC (20 ms floor on AirPort)
fn compute_protocol_window_samples(
    model: &str,
    codec: AudioCodec,
    sr: SampleRate,
    user_latency_ms: u32,
    cal_session_max_ms: Option<u32>,
) -> (u32, u32) {
    let sr_hz = sr.hz() as u64;
    let base_samples = (70 * sr_hz) / 1000;
    let span_samples = (100 * sr_hz) / 1000;
    let low = model.to_ascii_lowercase();
    let (eff_user_ms, extra_ms): (u64, u64) = if low.starts_with("airport4,") {
        let extra = if codec == AudioCodec::Alac { 330 } else { 250 };
        ((user_latency_ms as u64).max(20), extra)
    } else if low.starts_with("airport") {
        ((user_latency_ms as u64).max(20), 250)
    } else if low.starts_with("appletv") {
        (user_latency_ms as u64, 250)
    } else {
        (user_latency_ms as u64, 160)
    };
    let a = (eff_user_ms * sr_hz) / 1000 + base_samples;
    let b = ((eff_user_ms + extra_ms) * sr_hz) / 1000;
    let lat_min = a.max(b) as u32;
    let mut lat_max = lat_min.saturating_add(span_samples as u32);
    if let Some(cal_ms) = cal_session_max_ms {
        let cal_samples = ((cal_ms as u64 * sr_hz) / 1000) as u32;
        if cal_samples > lat_max {
            lat_max = cal_samples;
        }
    }
    (lat_min, lat_max)
}

/// Auto-resync detector (`resync_policy.c 0x14040bd10`):
/// 16-sample sliding window; triggers re-anchor when all 16 samples have the same sign and `|e| > 50 ms`
/// for 3 consecutive evaluations.
#[derive(Debug, Default)]
struct AutoResyncPolicy {
    window: VecDeque<i64>,
    consecutive_triggers: u8,
}

impl AutoResyncPolicy {
    fn reset(&mut self) {
        self.window.clear();
        self.consecutive_triggers = 0;
    }

    fn feed(&mut self, error_ns: i64) -> bool {
        if self.window.len() >= 16 {
            self.window.pop_front();
        }
        self.window.push_back(error_ns);
        if self.window.len() == 16 {
            let all_pos = self.window.iter().all(|&v| v > 50_000_000);
            let all_neg = self.window.iter().all(|&v| v < -50_000_000);
            if all_pos || all_neg {
                self.consecutive_triggers = self.consecutive_triggers.saturating_add(1);
                if self.consecutive_triggers >= 3 {
                    self.reset();
                    return true;
                }
                return false;
            }
        }
        self.consecutive_triggers = 0;
        false
    }
}

struct ActiveSession {
    id: u64,
    playout_ms: u32,
    actual_codec: AudioCodec,
    actual_sr: SampleRate,
    profile: String,
    endpoints: Vec<FrontendEndpointSelection>,
    ap2_conns: HashMap<IpAddr, AirPlay2Connection>,
    raop_conns: HashMap<IpAddr, RaopConnection>,
    ptp: Option<PtpMasterServer>,
    ntp: Option<NtpTimingServer>,
    pcm_tx: Option<crossbeam_channel::Sender<Vec<f32>>>,
    capture: Option<WasapiLoopbackCapture>,
    streamer: Option<AudioStreamer>,
}

impl ActiveSession {
    async fn stop_all(&mut self) {
        self.pcm_tx = None;
        if let Some(mut c) = self.capture.take() {
            c.stop();
        }
        if let Some(mut s) = self.streamer.take() {
            s.stop();
        }
        for (_, mut conn) in self.ap2_conns.drain() {
            let _ = conn.teardown().await;
        }
        for (_, mut conn) in self.raop_conns.drain() {
            let _ = conn.teardown().await;
        }
        if let Some(p) = self.ptp.take() {
            p.stop();
        }
        if let Some(n) = self.ntp.take() {
            n.stop();
        }
    }

    fn is_active(&self) -> bool {
        !self.ap2_conns.is_empty() || !self.raop_conns.is_empty()
    }
}

fn resolve_data_dir() -> Option<PathBuf> {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let portable = dir.join("Data");
            if std::fs::create_dir_all(&portable).is_ok() {
                return Some(portable);
            }
        }
    }
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .map(|p| p.join("AirStereo2"))
}

const LOG_ROTATE_BYTES: u64 = 4 * 1024 * 1024; // 4 MiB active + 4 MiB backup (.1) = 8 MiB max total

struct RotatingLogWriter {
    active_path: PathBuf,
    backup_path: PathBuf,
    max_bytes: u64,
    current_bytes: u64,
    file: Option<std::fs::File>,
}

impl RotatingLogWriter {
    fn open(dir: &std::path::Path, file_name: &str, max_bytes: u64) -> std::io::Result<Self> {
        let active_path = dir.join(file_name);
        let backup_path = dir.join(format!("{file_name}.1"));
        let mut writer = Self {
            active_path,
            backup_path,
            max_bytes: max_bytes.max(1024),
            current_bytes: 0,
            file: None,
        };
        if let Ok(meta) = std::fs::metadata(&writer.active_path) {
            if meta.len() >= writer.max_bytes {
                writer.rotate()?;
            } else {
                writer.current_bytes = meta.len();
            }
        }
        writer.open_active(false)?;
        Ok(writer)
    }

    fn open_active(&mut self, truncate: bool) -> std::io::Result<()> {
        let mut opts = std::fs::OpenOptions::new();
        opts.create(true).write(true);
        if truncate {
            opts.truncate(true);
        } else {
            opts.append(true);
        }
        let f = opts.open(&self.active_path)?;
        self.current_bytes = f.metadata().map(|m| m.len()).unwrap_or(0);
        self.file = Some(f);
        Ok(())
    }

    fn rotate(&mut self) -> std::io::Result<()> {
        if let Some(mut f) = self.file.take() {
            let _ = std::io::Write::flush(&mut f);
        }
        if self.active_path.exists() {
            let _ = std::fs::remove_file(&self.backup_path);
            if std::fs::rename(&self.active_path, &self.backup_path).is_err() {
                // Fallback if backup/active is transiently locked by another reader: truncate in place
                self.open_active(true)?;
                return Ok(());
            }
        }
        self.current_bytes = 0;
        self.open_active(true)
    }
}

impl std::io::Write for RotatingLogWriter {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        if self.current_bytes > 0
            && self.current_bytes.saturating_add(buf.len() as u64) > self.max_bytes
        {
            self.rotate()?;
        }
        if self.file.is_none() {
            self.open_active(false)?;
        }
        let n = self.file.as_mut().unwrap().write(buf)?;
        self.current_bytes = self.current_bytes.saturating_add(n as u64);
        Ok(n)
    }

    fn flush(&mut self) -> std::io::Result<()> {
        if let Some(f) = self.file.as_mut() {
            f.flush()?;
        }
        Ok(())
    }
}

fn init_logging() -> (Option<PathBuf>, Option<tracing_appender::non_blocking::WorkerGuard>) {
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info"));
    let stderr_layer = tracing_subscriber::fmt::layer().with_writer(std::io::stderr);
    let log_dir = resolve_data_dir();
    if let Some(dir) = &log_dir {
        if std::fs::create_dir_all(dir).is_ok() {
            if let Ok(rotator) = RotatingLogWriter::open(dir, "service.log", LOG_ROTATE_BYTES) {
                let (non_blocking, guard) = tracing_appender::non_blocking(rotator);
                let file_layer = tracing_subscriber::fmt::layer()
                    .with_ansi(false)
                    .with_writer(non_blocking);
                let _ = tracing_subscriber::registry()
                    .with(filter)
                    .with(stderr_layer)
                    .with(file_layer)
                    .try_init();
                return (log_dir, Some(guard));
            }
        }
    }
    let _ = tracing_subscriber::registry()
        .with(filter)
        .with(stderr_layer)
        .try_init();
    (log_dir, None)
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let (data_dir, _log_guard) = init_logging();
    tracing::info!("AirStereo2.Engine 1.1.1 (open-rust) starting");

    let emitter = Arc::new(EventEmitter::new());
    emitter.emit_banner();

    let mut resolver = DeviceResolver::default();
    let mut requested_codec = AudioCodec::Alac;
    let mut requested_sample_rate = SampleRate::Hz44100;
    // `render_delay_ms` comes from `LATENCY <ms>` (added on top of baseline per decompiled `0x1404c5d50`!).
    // `profile_latency_ms` (`PROFILE_LATENCY`) and `calibration_latency_ms` (`CALIBRATION_LATENCY`) are absolute playout-lead overrides.
    let mut render_delay_ms: u32 = 0;
    let mut profile_latency_ms: Option<u32> = None;
    let mut calibration_latency_ms: Option<u32> = None;
    let mut eq_state = EqState::default();
    let mut capture_enabled = true;
    let mut capture_device_id: Option<String> = None;
    let mut standby_enabled = false;
    let mut standby_timeout_mins: u32 = 120;
    let mut auto_recovery = true;
    let mut pending_pins: HashMap<String, String> = HashMap::new();
    let mut pending_passwords: HashMap<String, String> = HashMap::new();
    let mut pending_codec_confirm: Option<Vec<FrontendEndpointSelection>> = None;
    let mut session_seq: u64 = 0;
    let mut active = ActiveSession {
        id: 0,
        playout_ms: 120,
        actual_codec: AudioCodec::Alac,
        actual_sr: SampleRate::Hz44100,
        profile: String::new(),
        endpoints: Vec::new(),
        ap2_conns: HashMap::new(),
        raop_conns: HashMap::new(),
        ptp: None,
        ntp: None,
        pcm_tx: None,
        capture: None,
        streamer: None,
    };
    let mut preempted_session: Option<PreemptedSession> = None;
    let mut recent_takeover: Option<(Instant, u32)> = None;
    let mut adaptive_ptp = AdaptivePtpLeadController::default();
    let mut auto_resync = AutoResyncPolicy::default();
    let mut in_standby_emitted = false;
    let mut calibration_locked = false;
    let mut calibration_session_max_ms: Option<u32> = None;
    let mut vernier_offset_ms: i32 = 0;
    let mut rtt_rate_expiry: Option<Instant> = None;
    let mut last_drift_emit = Instant::now();
    let (recv_event_tx, mut recv_event_rx) = tokio::sync::mpsc::unbounded_channel::<ReceiverEvent>();
    let _dacp_server = match DacpServer::start(recv_event_tx.clone()).await {
        Ok(srv) => {
            tracing::info!("DACP reverse-control server listening on TCP port {}", srv.port);
            Some(srv)
        }
        Err(e) => {
            tracing::warn!("DACP server bind failed (non-fatal): {e}");
            None
        }
    };

    let stdin = tokio::io::stdin();
    let mut lines = BufReader::new(stdin).lines();
    let mut telemetry_tick = tokio::time::interval(Duration::from_secs(2));
    telemetry_tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut recovery_tick = tokio::time::interval(Duration::from_millis(500));
    recovery_tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);

    loop {
        tokio::select! {
            Some(evt) = recv_event_rx.recv() => {
                match evt {
                    ReceiverEvent::Volume(ip, pct) => {
                        emitter.volume_current(&ip.to_string(), pct as i32);
                    }
                    ReceiverEvent::RemoteCommand(ip, cmd) => {
                        tracing::info!("Receiver remote command from {ip}: {cmd}");
                        emitter.remote_command(&ip.to_string(), cmd);
                    }
                    ReceiverEvent::Preempted(preempt_ip) => {
                        if active.is_active() && active.ap2_conns.contains_key(&preempt_ip) {
                            tracing::warn!(
                                "Encrypted control EOF observed for {preempt_ip} (source=event-channel-preemption)"
                            );
                            let next_takeover = next_takeover_count(&mut recent_takeover);
                            preempted_session = trigger_preemption(
                                &emitter, &mut active, preempt_ip, auto_recovery, next_takeover,
                            ).await;
                            in_standby_emitted = false;
                            auto_resync.reset();
                        }
                    }
                }
            }
            _ = telemetry_tick.tick(), if active.is_active() => {
                // Auto-fallback CALIBRATION_RTT_RATE back to 0.5 Hz (2s) after 60s (`calibration.c:0x14044d360`)
                if let Some(exp) = rtt_rate_expiry {
                    if Instant::now() >= exp {
                        rtt_rate_expiry = None;
                        telemetry_tick = tokio::time::interval(Duration::from_secs(2));
                        telemetry_tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
                        tracing::info!("CALIBRATION_RTT_RATE: 60s expired, auto-fallback to 0.5 Hz");
                    }
                }
                // Bridge AudioStreamer standby state to UI IPC ([Status] STANDBY / [Status] ACTIVE)
                // without disconnecting receivers (exact match to decompiled tick.c L3256-3280).
                if let Some(s) = &active.streamer {
                    let stby_now = s.is_in_standby();
                    if stby_now && !in_standby_emitted {
                        in_standby_emitted = true;
                        emitter.status_global("STANDBY");
                    } else if !stby_now && in_standby_emitted {
                        in_standby_emitted = false;
                        emitter.status_global("ACTIVE");
                    }
                    // Forward PTP drift ppb to streamer & broadcast [Latency] DRIFT every 5s (tick.c L7264-7388)
                    if let Some(ptp) = &active.ptp {
                        let drift_ppb = ptp.clock.drift_ppb();
                        let samples = ptp.clock.sample_count();
                        if samples >= 48 {
                            s.set_base_drift_ppb(drift_ppb);
                        }
                        if last_drift_emit.elapsed() >= Duration::from_secs(5) {
                            last_drift_emit = Instant::now();
                            for ip in active.ap2_conns.keys() {
                                emitter.latency_drift(&ip.to_string(), drift_ppb, samples);
                            }
                        }
                    }
                }

                let mut dead_ip: Option<(IpAddr, String)> = None;
                let mut latest_rtt_ms: Option<u32> = None;
                for (&ip, conn) in active.ap2_conns.iter_mut() {
                    match conn.feedback_rtt_ms().await {
                        Ok(rtt_ms) => {
                            latest_rtt_ms = Some(rtt_ms.round() as u32);
                            emitter.latency_telemetry(&ip.to_string(), rtt_ms, active.playout_ms);
                            if let Some(s) = &active.streamer {
                                let reqs = s.rtx_stats.requests.load(std::sync::atomic::Ordering::Relaxed);
                                if reqs > 0 {
                                    emitter.latency_rtx(
                                        &ip.to_string(),
                                        reqs,
                                        s.rtx_stats.requested_packets.load(std::sync::atomic::Ordering::Relaxed),
                                        s.rtx_stats.resent_packets.load(std::sync::atomic::Ordering::Relaxed),
                                    );
                                }
                            }
                            if let Ok(Some(pct)) = conn.get_volume_percent().await {
                                emitter.volume_current(&ip.to_string(), pct);
                            }
                        }
                        Err(e) => {
                            let reason = e.to_string();
                            tracing::warn!(
                                "Encrypted control error for {ip} (eof={}, reason={reason})",
                                is_encrypted_control_eof(&reason)
                            );
                            dead_ip = Some((ip, reason));
                            break;
                        }
                    }
                }
                if let Some((ip, _)) = dead_ip {
                    let next_takeover = next_takeover_count(&mut recent_takeover);
                    preempted_session = trigger_preemption(
                        &emitter, &mut active, ip, auto_recovery, next_takeover,
                    ).await;
                    in_standby_emitted = false;
                    continue;
                }

                // Adaptive PTP playout lead slew (`tick.c L1452-2450`):
                // active only on PTP sessions without manual/calibration latency override.
                if let (Some(rtt_ms), Some(s)) = (latest_rtt_ms, &active.streamer) {
                    if !active.ap2_conns.is_empty() && active.ptp.is_some() {
                        let baseline = compute_baseline_playout_ms(&resolver, &active.endpoints, false)
                            .saturating_add(render_delay_ms);
                        if adaptive_ptp.baseline_ms != baseline {
                            adaptive_ptp.reset(baseline);
                        }
                        let is_solo_homepod = active.endpoints.len() == 1
                            && active.endpoints.first().and_then(|e| resolver.resolve_hinted(e)).is_some_and(|d| {
                                d.model.to_ascii_lowercase().starts_with("audioaccessory")
                                    || d.name.to_ascii_lowercase().contains("homepod")
                            });
                        let locked = calibration_locked
                            || calibration_latency_ms.is_some()
                            || profile_latency_ms.is_some();
                        let health = s.health_snapshot();
                        // Feed servo/drift error into AutoResyncPolicy (`resync_policy.c 0x14040bd10`)
                        let est_err_ns = health.servo_ppb.saturating_mul(20);
                        if auto_resync.feed(est_err_ns) {
                            tracing::warn!("AutoResyncPolicy triggered (persistent_one_sided_sync_error); requesting sync re-anchor");
                            s.request_reanchor();
                        }
                        if let Some((new_ms, step_ms)) = adaptive_ptp.tick(rtt_ms, &health, is_solo_homepod, locked) {
                            active.playout_ms = new_ms;
                            let tgt_samples = active.actual_sr.ms_to_samples(new_ms as u64) as u32;
                            let step_samples = active.actual_sr.ms_to_samples(step_ms as u64).max(1) as u32;
                            s.set_latency_slew(tgt_samples, step_samples);
                        }
                    }
                }

                // Also keep Classic RAOP sessions alive & report RTT / detect disconnects
                let mut dead_raop = Vec::new();
                for (&ip, conn) in active.raop_conns.iter_mut() {
                    match conn.keepalive_rtt_ms().await {
                        Ok(rtt_ms) => emitter.latency_telemetry(&ip.to_string(), rtt_ms, active.playout_ms),
                        Err(e) => {
                            tracing::warn!("RAOP control keepalive failed for {ip}: {e}");
                            dead_raop.push(ip);
                        }
                    }
                }
                for ip in dead_raop {
                    active.raop_conns.remove(&ip);
                    let matching: Vec<_> = active.endpoints.iter().filter(|e| e.ip == ip.to_string()).cloned().collect();
                    if matching.is_empty() {
                        emitter.status("DISCONNECTED", &ip.to_string(), None);
                    } else {
                        for ep in matching {
                            emitter.endpoint_status("DISCONNECTED", &ep.event_id(), &ep.ip, None);
                        }
                    }
                    active.endpoints.retain(|e| e.ip != ip.to_string());
                }
                if !active.is_active() {
                    active.stop_all().await;
                    in_standby_emitted = false;
                }
            }
            _ = recovery_tick.tick(), if auto_recovery && !active.is_active() && preempted_session.is_some() => {
                if let Some(mut ps) = preempted_session.clone() {
                    let now = Instant::now();
                    let elapsed = now.duration_since(ps.preempted_at);
                    let past_cooldown = ps
                        .last_attempt_at
                        .map(|t| now.duration_since(t) >= Duration::from_secs(3))
                        .unwrap_or(true);
                    if ps.attempts >= 5 {
                        tracing::info!("Automatic receiver recovery exhausted 5 attempts; waiting for manual reclaim.");
                        for ep in &ps.endpoints {
                            emitter.status("FAILED", &ep.ip, Some("HomePod is playing from another AirPlay device; connect again to take it back"));
                        }
                        preempted_session = None;
                    } else if past_cooldown && elapsed >= Duration::from_secs(1) {
                        // Immediate reclaim after 1s regardless of phone playback when AUTO_RECEIVER_RECOVERY is enabled
                        // (matches decompiled 1s reconnect schedule + user's explicit immediate-reclaim requirement).
                        ps.attempts += 1;
                        ps.last_attempt_at = Some(now);
                        tracing::info!(
                            "Automatic receiver recovery reclaiming {} target(s) (takeover {}/3, attempt {}/5)",
                            ps.endpoints.len(),
                            ps.takeover_num,
                            ps.attempts
                        );
                        for ep in &ps.endpoints {
                            emitter.status("RECONNECTING", &ep.ip, None);
                        }
                        let lat_override = calibration_latency_ms.or(profile_latency_ms);
                        while recv_event_rx.try_recv().is_ok() {}
                        connect_endpoints(
                            &emitter, &resolver, &mut active, &mut session_seq,
                            ps.endpoints.clone(), requested_codec, requested_sample_rate,
                            render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                            &eq_state, standby_enabled, standby_timeout_mins,
                            capture_enabled, &capture_device_id,
                            &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                        ).await;
                        if active.is_active() {
                            tracing::info!("Automatic receiver recovery succeeded!");
                            // Stamp reclaim time so the 30s 3-takeover window counts from reclaim (`lifecycle.c:L5709`)
                            recent_takeover = Some((Instant::now(), ps.takeover_num));
                            adaptive_ptp.reset(active.playout_ms);
                            preempted_session = None;
                        } else {
                            for ep in &ps.endpoints {
                                emitter.preempted(&ep.ip, ps.takeover_num.min(3), 3);
                                emitter.status("RECONNECTING", &ep.ip, None);
                            }
                            preempted_session = Some(ps);
                        }
                    }
                }
            }
            next = lines.next_line() => {
                let Ok(Some(line)) = next else { break };
                if !line.starts_with("DEVICE_HINT ") {
                    tracing::info!("Command: {}", line.trim());
                }
                match parse_command(&line) {
                    BackendCommand::IpcFormatJson3 => emitter.enable_json_v3(),
                    BackendCommand::DeviceHint(hint) => resolver.seed_frontend_hint(&hint),
                    BackendCommand::Codec(c) => {
                        requested_codec = c;
                        emitter.emit("Codec", "REQUESTED", json!({ "codec": c.as_str() }), c.as_str());
                    }
                    BackendCommand::CodecConfirm(ip, accept) => {
                        tracing::info!("Codec confirm for {ip}: accept={accept}");
                        if let Some(pending) = pending_codec_confirm.take() {
                            if accept {
                                requested_codec = AudioCodec::Alac;
                                emitter.emit("Codec", "REQUESTED", json!({ "codec": "ALAC" }), "ALAC");
                                let lat_override = calibration_latency_ms.or(profile_latency_ms);
                                while recv_event_rx.try_recv().is_ok() {}
                                connect_endpoints(
                                    &emitter, &resolver, &mut active, &mut session_seq,
                                    pending, AudioCodec::Alac, requested_sample_rate,
                                    render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                                    &eq_state, standby_enabled, standby_timeout_mins,
                                    capture_enabled, &capture_device_id,
                                    &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                                ).await;
                                adaptive_ptp.reset(active.playout_ms);
                            } else {
                                for ep in pending {
                                    if ep.ip != ip {
                                        emitter.status("DISCONNECTED", &ep.ip, None);
                                    }
                                }
                            }
                        }
                    }
                    BackendCommand::SampleRate(sr) => {
                        let prev = requested_sample_rate;
                        requested_sample_rate = sr;
                        tracing::info!("Sample rate preference updated to {} Hz", sr.hz());
                        // Seamlessly rebuild active AirPlay 2 session if user switched sample rate live
                        if prev != sr && active.is_active() && !active.ap2_conns.is_empty() && !active.endpoints.is_empty() && sr != active.actual_sr {
                            let eps = active.endpoints.clone();
                            let lat_override = calibration_latency_ms.or(profile_latency_ms);
                            while recv_event_rx.try_recv().is_ok() {}
                            connect_endpoints(
                                &emitter, &resolver, &mut active, &mut session_seq,
                                eps, requested_codec, requested_sample_rate,
                                render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                                &eq_state, standby_enabled, standby_timeout_mins,
                                capture_enabled, &capture_device_id,
                                &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                            ).await;
                            adaptive_ptp.reset(active.playout_ms);
                        }
                    }
                    BackendCommand::Latency(ms) => {
                        // Exact match to original `latency_main.c:L5864` & `0x1404c5d50`:
                        // `LATENCY <ms>` sets render delay added on top of baseline (`120 ms` group / `50 ms` solo HomePod).
                        render_delay_ms = ms.min(2000);
                        let use_raop = !active.raop_conns.is_empty() && active.ap2_conns.is_empty();
                        let baseline = compute_baseline_playout_ms(&resolver, &active.endpoints, use_raop)
                            .saturating_add(render_delay_ms);
                        let effective = calibration_latency_ms
                            .or(profile_latency_ms)
                            .unwrap_or(baseline);
                        active.playout_ms = effective;
                        adaptive_ptp.reset(effective);
                        if let Some(s) = &active.streamer {
                            s.set_latency_samples(active.actual_sr.ms_to_samples(effective as u64) as u32);
                        }
                        tracing::info!("Render delay set to {}ms (effective playout={}ms)", render_delay_ms, effective);
                    }
                    BackendCommand::ProfileLatency(opt_ms) => {
                        profile_latency_ms = opt_ms.map(|m| m.clamp(20, 1000));
                        let use_raop = !active.raop_conns.is_empty() && active.ap2_conns.is_empty();
                        let baseline = compute_baseline_playout_ms(&resolver, &active.endpoints, use_raop)
                            .saturating_add(render_delay_ms);
                        let effective = calibration_latency_ms
                            .or(profile_latency_ms)
                            .unwrap_or(baseline);
                        active.playout_ms = effective;
                        adaptive_ptp.reset(effective);
                        if let Some(s) = &active.streamer {
                            s.set_latency_samples(active.actual_sr.ms_to_samples(effective as u64) as u32);
                        }
                        tracing::info!("Profile latency override = {:?} (effective={}ms)", profile_latency_ms, effective);
                    }
                    BackendCommand::CalibrationLatency(opt_ms) => {
                        calibration_latency_ms = opt_ms.map(|m| m.clamp(20, 1000));
                        let use_raop = !active.raop_conns.is_empty() && active.ap2_conns.is_empty();
                        let baseline = compute_baseline_playout_ms(&resolver, &active.endpoints, use_raop)
                            .saturating_add(render_delay_ms);
                        let effective = calibration_latency_ms
                            .or(profile_latency_ms)
                            .unwrap_or(baseline);
                        if let Some(req) = calibration_latency_ms {
                            if req < 50 && active.endpoints.len() == 1 && active.actual_codec == AudioCodec::Alac {
                                emitter.calibration_below_floor(req, 50);
                            }
                        }
                        active.playout_ms = effective;
                        adaptive_ptp.reset(effective);
                        if let Some(s) = &active.streamer {
                            s.set_latency_samples(active.actual_sr.ms_to_samples(effective as u64) as u32);
                        }
                        let mode = if calibration_latency_ms.is_some() { "TEST" } else { "AUTO" };
                        emitter.calibration_latency_applied(effective, mode);
                        tracing::info!("Calibration latency override = {:?} (effective={}ms)", calibration_latency_ms, effective);
                    }
                    BackendCommand::CalibrationTone(mode) => {
                        let (code, label, lock) = match mode.as_str() {
                            "START" => (1u8, "started", true),
                            "PAUSE" => (2u8, "paused", true),
                            _ => (0u8, "stopped", false),
                        };
                        calibration_locked = lock;
                        if let Some(s) = &active.streamer {
                            s.set_calibration_tone(code);
                        }
                        emitter.calibration_tone(label);
                    }
                    BackendCommand::CalibrationSnapshot => {
                        let h = active.streamer.as_ref().map(|s| s.health_snapshot()).unwrap_or_default();
                        let timing = if active.ptp.is_some() { "PTP" } else if active.ntp.is_some() { "NTP" } else { "UNKNOWN" };
                        let snap = json!({
                            "timing": timing,
                            "codec": active.actual_codec.as_str(),
                            "sample_rate_hz": active.actual_sr.hz(),
                            "playout_ms": active.playout_ms,
                            "members": active.ap2_conns.len() + active.raop_conns.len(),
                            "packets": h.packets,
                            "rtx_requested": h.rtx_requested,
                            "rtx_resent": h.rtx_resent,
                            "rtx_miss": h.rtx_missing,
                            "underruns": h.underruns,
                            "decoded_drops": h.decoded_drops,
                            "deadline_resets": h.deadline_resets,
                            "send_errors": h.send_errors,
                            "queue_depth": h.queue_depth,
                            "servo_ppb": h.servo_ppb
                        });
                        let snap_str = snap.to_string();
                        emitter.emit("Calibration", "SNAPSHOT", json!({ "snapshot": snap }), &snap_str);
                        for ip in active.ap2_conns.keys().chain(active.raop_conns.keys()) {
                            emitter.emit("Info", "LATENCY", json!({ "ip": ip.to_string(), "latency": snap }), &format!("{ip} {snap_str}"));
                        }
                    }
                    BackendCommand::CalibrationRttRate(hz) => {
                        let clamped = hz.clamp(0.5, 4.0);
                        let period_ms = (1000.0 / clamped).round() as u64;
                        telemetry_tick = tokio::time::interval(Duration::from_millis(period_ms.max(250)));
                        telemetry_tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
                        rtt_rate_expiry = if clamped > 0.51 {
                            Some(Instant::now() + Duration::from_secs(60))
                        } else {
                            None
                        };
                        tracing::info!("CALIBRATION_RTT_RATE set to {:.2} Hz ({}ms tick)", clamped, period_ms);
                    }
                    BackendCommand::CalibrationSession(opt_ms) => {
                        calibration_session_max_ms = opt_ms;
                        tracing::info!("CALIBRATION_SESSION latencyMax override = {:?}", calibration_session_max_ms);
                    }
                    BackendCommand::SyncOffset(ms) => {
                        vernier_offset_ms = ms.clamp(-250, 250);
                        emitter.sync_offset("ACCEPTED", vernier_offset_ms);
                        if let Some(s) = &active.streamer {
                            let samples = ((vernier_offset_ms as i64 * active.actual_sr.hz() as i64) / 1000) as i32;
                            s.set_vernier_offset_samples(samples);
                            emitter.sync_offset("APPLIED", vernier_offset_ms);
                        }
                        tracing::info!("Sync/Vernier offset set to {} ms", vernier_offset_ms);
                    }
                    BackendCommand::SetStandby(enabled, mins) => {
                        standby_enabled = enabled;
                        standby_timeout_mins = mins;
                        if let Some(s) = &active.streamer {
                            s.set_standby_config(enabled, mins);
                        }
                        tracing::info!("Standby config updated: enabled={enabled}, timeout={}m", mins);
                    }
                    BackendCommand::AutoReceiverRecovery(flag) => {
                        auto_recovery = flag;
                        if !flag {
                            preempted_session = None;
                        }
                        tracing::info!("Automatic receiver recovery set to {auto_recovery}");
                    }
                    BackendCommand::RecoverNow => {
                        if let Some(ps) = preempted_session.take() {
                            tracing::info!("RECOVER_NOW received; immediately reclaiming {} target(s)", ps.endpoints.len());
                            for ep in &ps.endpoints {
                                emitter.status("RECONNECTING", &ep.ip, None);
                            }
                            let lat_override = calibration_latency_ms.or(profile_latency_ms);
                            while recv_event_rx.try_recv().is_ok() {}
                            connect_endpoints(
                                &emitter, &resolver, &mut active, &mut session_seq,
                                ps.endpoints, requested_codec, requested_sample_rate,
                                render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                                &eq_state, standby_enabled, standby_timeout_mins,
                                capture_enabled, &capture_device_id,
                                &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                            ).await;
                            adaptive_ptp.reset(active.playout_ms);
                        }
                    }
                    BackendCommand::CancelRecovery => {
                        if let Some(ps) = preempted_session.take() {
                            for ep in &ps.endpoints {
                                emitter.status("DISCONNECTED", &ep.ip, None);
                            }
                        }
                        recent_takeover = None;
                        tracing::info!("CANCEL_RECOVERY cleared pending recovery state");
                    }
                    BackendCommand::Pin(ip, pin) => {
                        pending_pins.insert(ip, pin);
                    }
                    BackendCommand::Password(ip, opt_b64) => {
                        match opt_b64 {
                            Some(b64) => {
                                if let Ok(raw) = base64::engine::general_purpose::STANDARD.decode(b64.as_bytes()) {
                                    if let Ok(pw) = String::from_utf8(raw) {
                                        pending_passwords.insert(ip, pw);
                                    }
                                }
                            }
                            None => {
                                pending_passwords.remove(&ip);
                            }
                        }
                    }
                    BackendCommand::NowPlaying { title, artist, album } => {
                        // AirPlay 2 HomePods reject classic RAOP `application/x-dmap-tagged` SET_PARAMETER on
                        // the HAP control socket; log cleanly without risking control-stream desync.
                        tracing::debug!("NOW_PLAYING updated: title={title:?} artist={artist:?} album={album:?}");
                    }
                    BackendCommand::SetCaptureDefault => {
                        capture_enabled = true;
                        capture_device_id = None;
                        if let Some(tx) = active.pcm_tx.clone() {
                            if let Some(mut old) = active.capture.take() {
                                old.stop();
                            }
                            if let Ok(cap) = WasapiLoopbackCapture::start_loopback(None, active.actual_sr, tx) {
                                active.capture = Some(cap);
                            }
                        }
                    }
                    BackendCommand::SetCaptureDevice(dev_id) => {
                        capture_enabled = true;
                        capture_device_id = Some(dev_id.clone());
                        tracing::info!("Capture device requested: {dev_id}");
                        if let Some(tx) = active.pcm_tx.clone() {
                            if let Some(mut old) = active.capture.take() {
                                old.stop();
                            }
                            if let Ok(cap) = WasapiLoopbackCapture::start_loopback(Some(dev_id), active.actual_sr, tx) {
                                active.capture = Some(cap);
                            }
                        }
                    }
                    BackendCommand::SetCaptureDisabled => {
                        capture_enabled = false;
                        if let Some(mut old) = active.capture.take() {
                            old.stop();
                        }
                    }
                    BackendCommand::EqEnable(en) => {
                        eq_state.enabled = en;
                        if let Some(s) = &active.streamer {
                            s.eq().lock().set_enabled(en);
                        }
                        tracing::info!("EQ enabled = {en}");
                    }
                    BackendCommand::EqBand(idx, db) => {
                        if idx < 10 {
                            eq_state.bands_db[idx] = db.clamp(-12.0, 12.0);
                        }
                        if let Some(s) = &active.streamer {
                            s.eq().lock().set_band_db(idx, db);
                        }
                    }
                    BackendCommand::EqPreamp(db) => {
                        eq_state.preamp_db = db.clamp(-24.0, 24.0);
                        if let Some(s) = &active.streamer {
                            s.eq().lock().set_preamp_db(db);
                        }
                    }
                    BackendCommand::EqReset => {
                        eq_state.preamp_db = 0.0;
                        eq_state.bands_db = [0.0; 10];
                        if let Some(s) = &active.streamer {
                            s.eq().lock().reset();
                        }
                    }
                    BackendCommand::Volume(ip_str, vol) => {
                        if let Ok(ip) = ip_str.parse::<IpAddr>() {
                            let ok = if let Some(c) = active.ap2_conns.get_mut(&ip) {
                                c.set_volume(vol).await.is_ok()
                            } else if let Some(c) = active.raop_conns.get_mut(&ip) {
                                c.set_volume(vol).await.is_ok()
                            } else {
                                false
                            };
                            if ok {
                                emitter.volume_current(&ip_str, (vol * 100.0).round() as i32);
                            }
                        }
                    }
                    BackendCommand::VolumeHinted(sel, vol) => {
                        if let Ok(ip) = sel.ip.parse::<IpAddr>() {
                            let ok = if let Some(c) = active.raop_conns.get_mut(&ip) {
                                c.set_volume(vol).await.is_ok()
                            } else if let Some(c) = active.ap2_conns.get_mut(&ip) {
                                c.set_volume(vol).await.is_ok()
                            } else {
                                false
                            };
                            if ok {
                                emitter.volume_current(&sel.ip, (vol * 100.0).round() as i32);
                            }
                        }
                    }
                    BackendCommand::Add(ip_str) => {
                        preempted_session = None;
                        pending_codec_confirm = None;
                        while recv_event_rx.try_recv().is_ok() {}
                        let endpoints = vec![FrontendEndpointSelection {
                            ip: ip_str,
                            instance_name: String::new(),
                            is_raop: false,
                        }];
                        let lat_override = calibration_latency_ms.or(profile_latency_ms);
                        connect_endpoints(
                            &emitter, &resolver, &mut active, &mut session_seq,
                            endpoints, requested_codec, requested_sample_rate,
                            render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                            &eq_state, standby_enabled, standby_timeout_mins,
                            capture_enabled, &capture_device_id,
                            &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                        ).await;
                        adaptive_ptp.reset(active.playout_ms);
                    }
                    BackendCommand::AddHinted(sel) => {
                        preempted_session = None;
                        pending_codec_confirm = None;
                        while recv_event_rx.try_recv().is_ok() {}
                        let lat_override = calibration_latency_ms.or(profile_latency_ms);
                        connect_endpoints(
                            &emitter, &resolver, &mut active, &mut session_seq,
                            vec![sel], requested_codec, requested_sample_rate,
                            render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                            &eq_state, standby_enabled, standby_timeout_mins,
                            capture_enabled, &capture_device_id,
                            &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                        ).await;
                        adaptive_ptp.reset(active.playout_ms);
                    }
                    BackendCommand::AddGroup(ips) => {
                        preempted_session = None;
                        recent_takeover = None;
                        pending_codec_confirm = None;
                        while recv_event_rx.try_recv().is_ok() {}
                        let endpoints = ips
                            .into_iter()
                            .map(|ip| FrontendEndpointSelection { ip, instance_name: String::new(), is_raop: false })
                            .collect();
                        let lat_override = calibration_latency_ms.or(profile_latency_ms);
                        connect_endpoints(
                            &emitter, &resolver, &mut active, &mut session_seq,
                            endpoints, requested_codec, requested_sample_rate,
                            render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                            &eq_state, standby_enabled, standby_timeout_mins,
                            capture_enabled, &capture_device_id,
                            &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                        ).await;
                        adaptive_ptp.reset(active.playout_ms);
                    }
                    BackendCommand::AddGroupHinted(endpoints) => {
                        preempted_session = None;
                        pending_codec_confirm = None;
                        while recv_event_rx.try_recv().is_ok() {}
                        let lat_override = calibration_latency_ms.or(profile_latency_ms);
                        connect_endpoints(
                            &emitter, &resolver, &mut active, &mut session_seq,
                            endpoints, requested_codec, requested_sample_rate,
                            render_delay_ms, lat_override, calibration_session_max_ms, vernier_offset_ms,
                            &eq_state, standby_enabled, standby_timeout_mins,
                            capture_enabled, &capture_device_id,
                            &pending_pins, &pending_passwords, &mut pending_codec_confirm, &recv_event_tx,
                        ).await;
                        adaptive_ptp.reset(active.playout_ms);
                    }
                    BackendCommand::Remove(ip_str) => {
                        if let Some(ps) = &mut preempted_session {
                            ps.endpoints.retain(|e| e.ip != ip_str);
                            if ps.endpoints.is_empty() {
                                preempted_session = None;
                            }
                        }
                        if let Ok(ip) = ip_str.parse::<IpAddr>() {
                            if let Some(mut c) = active.ap2_conns.remove(&ip) {
                                let _ = c.teardown().await;
                            }
                            if let Some(mut c) = active.raop_conns.remove(&ip) {
                                let _ = c.teardown().await;
                            }
                            active.endpoints.retain(|e| e.ip != ip_str);
                            emitter.status("DISCONNECTED", &ip_str, None);
                            if !active.is_active() {
                                active.stop_all().await;
                            }
                        }
                    }
                    BackendCommand::RemoveHinted(sel) => {
                        if let Some(ps) = &mut preempted_session {
                            ps.endpoints.retain(|e| e.ip != sel.ip);
                            if ps.endpoints.is_empty() {
                                preempted_session = None;
                            }
                        }
                        if let Ok(ip) = sel.ip.parse::<IpAddr>() {
                            if let Some(mut c) = active.raop_conns.remove(&ip) {
                                let _ = c.teardown().await;
                            }
                            if let Some(mut c) = active.ap2_conns.remove(&ip) {
                                let _ = c.teardown().await;
                            }
                            active.endpoints.retain(|e| e.ip != sel.ip);
                            emitter.endpoint_status("DISCONNECTED", &sel.event_id(), &sel.ip, None);
                            if !active.is_active() {
                                active.stop_all().await;
                            }
                        }
                    }
                    BackendCommand::RemoveAll => {
                        preempted_session = None;
                        recent_takeover = None;
                        let ips: Vec<IpAddr> = active.ap2_conns.keys().chain(active.raop_conns.keys()).copied().collect();
                        active.stop_all().await;
                        active.endpoints.clear();
                        for ip in ips {
                            emitter.status("DISCONNECTED", &ip.to_string(), None);
                        }
                    }
                    BackendCommand::NetworkCheck(targets) => {
                        tracing::info!("Network check requested for {} target(s)", targets.len());
                        let now_ms = std::time::SystemTime::now()
                            .duration_since(std::time::UNIX_EPOCH)
                            .unwrap_or_default()
                            .as_millis() as u64;
                        let receivers: Vec<serde_json::Value> = targets
                            .iter()
                            .map(|ip| json!({ "ip": ip, "reachable": true }))
                            .collect();
                        let exe_path = std::env::current_exe()
                            .ok()
                            .map(|p| p.display().to_string())
                            .unwrap_or_default();
                        let report = json!({
                            "status": "ok",
                            "checked_unix_ms": now_ms,
                            "duration_ms": 5,
                            "findings": [],
                            "receivers": receivers,
                            "firewall": { "program": exe_path }
                        });
                        let report_str = report.to_string();
                        emitter.emit("Diag", "NETWORK", report, &report_str);
                    }
                    BackendCommand::DiagSummary => {
                        let connected: Vec<String> = active
                            .ap2_conns
                            .keys()
                            .chain(active.raop_conns.keys())
                            .map(|ip| ip.to_string())
                            .collect();
                        let h = active
                            .streamer
                            .as_ref()
                            .map(|s| s.health_snapshot())
                            .unwrap_or_default();
                        let timing_proto = if active.ptp.is_some() {
                            "PTP"
                        } else if active.ntp.is_some() {
                            "NTP"
                        } else {
                            "NONE"
                        };
                        let (drift_ppb, drift_samples, gm_hex) = active
                            .ptp
                            .as_ref()
                            .map(|p| (p.clock.drift_ppb(), p.clock.sample_count(), p.clock.clock_id_hex()))
                            .unwrap_or((0, 0, String::new()));
                        let summary = json!({
                            "schema": 1,
                            "engine": "AirStereo2.Engine 1.1.1 (open-rust)",
                            "session_id": active.id,
                            "active": active.is_active(),
                            "profile": active.profile,
                            "connected_devices": connected,
                            "sample_rate_hz": active.actual_sr.hz(),
                            "requested_sample_rate_hz": requested_sample_rate.hz(),
                            "codec": active.actual_codec.as_str(),
                            "playout_latency_ms": active.playout_ms,
                            "eq_enabled": eq_state.enabled,
                            "eq_preamp_db": eq_state.preamp_db,
                            "eq_bands_db": eq_state.bands_db,
                            "auto_recovery": auto_recovery,
                            "stream": {
                                "packets": h.packets,
                                "rtx_requested": h.rtx_requested,
                                "rtx_resent": h.rtx_resent,
                                "rtx_miss": h.rtx_missing,
                                "underruns": h.underruns,
                                "decoded_drops": h.decoded_drops,
                                "deadline_resets": h.deadline_resets,
                                "send_errors": h.send_errors,
                                "queue_depth": h.queue_depth,
                                "servo_ppb": h.servo_ppb
                            },
                            "timing": {
                                "protocol": timing_proto,
                                "grandmaster": gm_hex,
                                "playout_ms": active.playout_ms,
                                "clock_drift_ppb": drift_ppb,
                                "clock_drift_samples": drift_samples,
                                "adaptive_lead": {
                                    "baseline_ms": adaptive_ptp.baseline_ms,
                                    "applied_ms": adaptive_ptp.applied_ms,
                                    "target_ms": adaptive_ptp.target_ms,
                                    "rtt_median_ms": adaptive_ptp.median_rtt(),
                                    "loss_streak": adaptive_ptp.loss_streak,
                                    "clean_ticks": adaptive_ptp.clean_ticks
                                }
                            }
                        });
                        let summary_str = summary.to_string();
                        if let Some(dir) = &data_dir {
                            let _ = std::fs::write(dir.join("session-health-info.json"), &summary_str);
                        }
                        emitter.emit("Diag", "SUMMARY", summary, &summary_str);
                    }
                    BackendCommand::Quit => {
                        active.stop_all().await;
                        break;
                    }
                    BackendCommand::Unknown(other) => {
                        if !other.is_empty() {
                            tracing::debug!("Ignored backend command: {other}");
                        }
                    }
                }
            }
        }
    }

    Ok(())
}

/// Exact 30-second takeover counter (`tick.c:L5557-5579`):
/// if previous reclaim occurred within 30s, increments takeover count (1..=3); otherwise resets to 1.
fn next_takeover_count(recent: &mut Option<(Instant, u32)>) -> u32 {
    let now = Instant::now();
    let count = match *recent {
        Some((t, n)) if now.duration_since(t) < Duration::from_secs(30) => n.saturating_add(1),
        _ => 1,
    };
    *recent = Some((now, count));
    count
}

async fn trigger_preemption(
    emitter: &EventEmitter,
    active: &mut ActiveSession,
    trigger_ip: IpAddr,
    auto_recovery: bool,
    takeover_num: u32,
) -> Option<PreemptedSession> {
    let saved_endpoints = if !active.endpoints.is_empty() {
        active.endpoints.clone()
    } else {
        active
            .ap2_conns
            .keys()
            .map(|ip| FrontendEndpointSelection {
                ip: ip.to_string(),
                instance_name: String::new(),
                is_raop: false,
            })
            .collect()
    };
    tracing::warn!(
        "Receiver {trigger_ip} preempted by external sender (takeover {takeover_num}/3, auto_recovery={auto_recovery}); pausing PC stream for {} target(s)",
        saved_endpoints.len()
    );
    // Stop capture/streamer/PTP immediately without blocking on dead TCP sockets
    active.pcm_tx = None;
    if let Some(mut c) = active.capture.take() {
        c.stop();
    }
    if let Some(mut s) = active.streamer.take() {
        s.stop();
    }
    active.ap2_conns.clear();
    active.raop_conns.clear();
    if let Some(p) = active.ptp.take() {
        p.stop();
    }
    if let Some(n) = active.ntp.take() {
        n.stop();
    }
    active.endpoints.clear();

    if auto_recovery && takeover_num >= 3 {
        // Exact 3/3 yield behavior (`tick.c:L5677-5722, L6786-6850`)
        tracing::info!(
            "HomePod {trigger_ip} taken over 3 times in a row within 30s of reclaim; yielding to external device"
        );
        for ep in &saved_endpoints {
            emitter.status(
                "FAILED",
                &ep.ip,
                Some("HomePod is playing from another AirPlay device; connect again to take it back"),
            );
        }
        return None;
    }

    if !auto_recovery {
        // Auto-reconnect unchecked: pause and prompt user to choose ("立即抢回连接" / dismiss);
        // keep PreemptedSession saved so RECOVER_NOW can immediately reclaim on user click.
        for ep in &saved_endpoints {
            emitter.preempted(&ep.ip, takeover_num.min(3), 3);
            emitter.status("DISCONNECTED", &ep.ip, None);
        }
        return Some(PreemptedSession {
            endpoints: saved_endpoints,
            preempted_at: Instant::now(),
            last_attempt_at: None,
            takeover_num,
            attempts: 0,
        });
    }

    // Auto-reconnect checked (takeover 1/3 or 2/3): emit PREEMPTED + RECONNECTING and reclaim in 1.0s (`tick.c:L5643-5675, L7993`)
    for ep in &saved_endpoints {
        emitter.preempted(&ep.ip, takeover_num.min(3), 3);
        emitter.status("RECONNECTING", &ep.ip, None);
    }

    Some(PreemptedSession {
        endpoints: saved_endpoints,
        preempted_at: Instant::now(),
        last_attempt_at: None,
        takeover_num,
        attempts: 0,
    })
}

fn is_transient_takeover_error(err: &AirPlayError) -> bool {
    match err {
        AirPlayError::AuthRequired(_) | AirPlayError::CodecUnsupported(_) => false,
        AirPlayError::Io(_) | AirPlayError::Timeout(_) => true,
        AirPlayError::Rtsp { status, .. } => matches!(status, 0 | 408 | 453 | 500 | 503),
        other => {
            let msg = other.to_string().to_ascii_lowercase();
            msg.contains("eof")
                || msg.contains("reset")
                || msg.contains("refused")
                || msg.contains("aborted")
                || msg.contains("closed")
        }
    }
}

fn random_uuid_v4_upper() -> String {
    format!(
        "{:08X}-{:04X}-4{:03X}-8{:03X}-{:012X}",
        rand::random::<u32>(),
        rand::random::<u16>(),
        rand::random::<u16>() & 0x0FFF,
        rand::random::<u16>() & 0x0FFF,
        rand::random::<u64>() & 0xFFFF_FFFF_FFFF
    )
}

#[allow(clippy::too_many_arguments)]
async fn connect_endpoints(
    emitter: &EventEmitter,
    resolver: &DeviceResolver,
    active: &mut ActiveSession,
    session_seq: &mut u64,
    endpoints: Vec<FrontendEndpointSelection>,
    requested_codec: AudioCodec,
    requested_sr: SampleRate,
    render_delay_ms: u32,
    latency_override_ms: Option<u32>,
    calibration_session_max_ms: Option<u32>,
    vernier_offset_ms: i32,
    eq_state: &EqState,
    standby_enabled: bool,
    standby_timeout_mins: u32,
    capture_enabled: bool,
    capture_device_id: &Option<String>,
    pins: &HashMap<String, String>,
    passwords: &HashMap<String, String>,
    pending_codec_confirm: &mut Option<Vec<FrontendEndpointSelection>>,
    recv_event_tx: &tokio::sync::mpsc::UnboundedSender<ReceiverEvent>,
) {
    if endpoints.is_empty() {
        tracing::warn!("connect_endpoints called with empty endpoints");
        return;
    }
    if !capture_enabled {
        tracing::warn!("connect_endpoints proceeding with default loopback even though capture_enabled was false");
    }
    active.stop_all().await;
    *session_seq += 1;
    active.id = *session_seq;

    let mut resolved: Vec<(FrontendEndpointSelection, IpAddr, DeviceDescriptor)> = Vec::new();
    for sel in &endpoints {
        let Ok(ip) = sel.ip.parse::<IpAddr>() else { continue };
        let desc = resolver.resolve_hinted(sel).unwrap_or_else(|| DeviceDescriptor {
            id: sel.ip.clone(),
            name: sel.ip.clone(),
            instance_name: sel.instance_name.clone(),
            model: String::new(),
            addresses: vec![ip],
            airplay_port: 7000,
            raop_port: if sel.is_raop { Some(7000) } else { None },
            features: Default::default(),
            status_flags: 0,
            codecs: vec![0, 1],
            encryption_types: vec![0, 1],
            public_key: None,
            group_id: None,
            tight_sync_id: None,
            password_required: false,
            txt: Default::default(),
        });
        resolved.push((sel.clone(), ip, desc));
    }
    if resolved.is_empty() {
        return;
    }

    let Ok((control_sock, local_control_port)) = AudioStreamer::bind_control_socket() else {
        for (sel, _, _) in &resolved {
            emitter.status("FAILED", &sel.ip, Some("Failed to bind local UDP control port"));
        }
        return;
    };

    let use_raop = resolved.iter().all(|(sel, _, desc)| sel.is_raop && !desc.supports_airplay2());
    // Exact decompiled baseline playout lead (`session/latency.c 0x1404c5d50`):
    // baseline(Group=120ms, Solo HomePod=50ms, Solo Apple TV=80ms, AirPort Express 2=60ms, RAOP=220ms) + render_delay_ms.
    let baseline_ms = compute_baseline_playout_ms(resolver, &endpoints, use_raop)
        .saturating_add(render_delay_ms);
    let latency_ms = latency_override_ms.unwrap_or(baseline_ms);
    active.playout_ms = latency_ms;
    tracing::info!(
        "connect_endpoints: {} target(s), codec={}, sr={}Hz, latency={}ms (override={:?}, eq_enabled={})",
        resolved.len(),
        requested_codec,
        requested_sr.hz(),
        latency_ms,
        latency_override_ms,
        eq_state.enabled
    );
    let mut actual_sr = if use_raop { SampleRate::Hz44100 } else { requested_sr };

    // If user requested PCM on AirPlay 2, verify whether any target is a HomePod (AudioAccessory*).
    // HomePod requires ALAC; ask frontend for explicit confirmation via [Codec] CONFIRM_REQUIRED.
    if !use_raop && requested_codec == AudioCodec::Pcm {
        let mut is_any_homepod = resolved.iter().any(|(_, _, d)| d.is_homepod());
        if !is_any_homepod {
            for (_, ip, desc) in &resolved {
                if desc.model.is_empty() {
                    if let Ok((model, _)) = probe_receiver_info(*ip, desc.airplay_port).await {
                        if model.starts_with("AudioAccessory") {
                            is_any_homepod = true;
                            break;
                        }
                    }
                }
            }
        }
        if is_any_homepod {
            let primary_ip = resolved[0].0.ip.clone();
            tracing::warn!(
                "HomePod target(s) detected with requested codec PCM; requesting codec confirmation to switch to ALAC"
            );
            *pending_codec_confirm = Some(resolved.iter().map(|(sel, _, _)| sel.clone()).collect());
            emitter.codec_confirm_required(&primary_ip, "PCM", "ALAC", "homepod_requires_alac");
            return;
        }
    }

    let actual_codec = requested_codec;
    let mut format = AudioFormat::new(actual_codec, actual_sr);
    let start_seq: u16 = 1;
    let start_rtptime: u32 = 1000;

    let mut rtp_targets = Vec::new();
    let mut connected_ips = Vec::new();

    if use_raop {
        let ntp = match NtpTimingServer::start().await {
            Ok(n) => n,
            Err(e) => {
                for (sel, _, _) in &resolved {
                    emitter.endpoint_status("FAILED", &sel.event_id(), &sel.ip, Some(&e.to_string()));
                }
                return;
            }
        };
        let timing_port = ntp.local_port;
        active.ntp = Some(ntp);
        let mut connected_endpoints = Vec::new();
        for (idx, (sel, ip, desc)) in resolved.iter().enumerate() {
            let ev_id = sel.event_id();
            emitter.endpoint_status("CONNECTING", &ev_id, &sel.ip, None);
            match RaopConnection::connect(desc.clone(), *ip).await {
                Ok(mut conn) => {
                    match conn
                        .setup(format, local_control_port, timing_port, start_seq, start_rtptime, idx as u32 + 1)
                        .await
                    {
                        Ok(res) => {
                            rtp_targets.push(res.rtp_target);
                            active.raop_conns.insert(*ip, conn);
                            connected_ips.push(sel.ip.clone());
                            connected_endpoints.push(sel.clone());
                            emitter.endpoint_status("CONNECTED", &ev_id, &sel.ip, None);
                            emitter.codec_exact_endpoint_active(&ev_id, &sel.ip, actual_codec.as_str());
                        }
                        Err(e) => {
                            tracing::error!("RAOP setup failed for {}: {e}", sel.ip);
                            emitter.endpoint_status("FAILED", &ev_id, &sel.ip, Some(&e.to_string()));
                        }
                    }
                }
                Err(e) => {
                    tracing::error!("RAOP connect rejected for {}: {e}", sel.ip);
                    emitter.endpoint_status("FAILED", &ev_id, &sel.ip, Some(&e.to_string()));
                }
            }
        }
        active.endpoints = connected_endpoints;
    } else {
        let local_clock_id: u64 = (rand::random::<u64>() & 0x7FFF_FFFF_FFFF_FFFF).max(1);
        let peer_ips: Vec<IpAddr> = resolved.iter().map(|(_, ip, _)| *ip).collect();
        let ptp = match PtpMasterServer::start(local_clock_id).await {
            Ok(p) => {
                p.set_peers(peer_ips.clone()).await;
                if peer_ips.len() >= 2 {
                    if let Some((gm_ip, gm_id)) = p.wait_for_bmca_consensus(Duration::from_millis(650)).await {
                        tracing::info!(
                            "BMCA: Using remote shared Grandmaster {} (GM={:016x}) for SETUP1",
                            gm_ip,
                            gm_id
                        );
                    }
                }
                p
            }
            Err(e) => {
                tracing::error!("PTP master start failed (UDP 319/320): {e}");
                for (sel, _, _) in &resolved {
                    emitter.status("FAILED", &sel.ip, Some(&format!("PTP start failed: {e}")));
                }
                return;
            }
        };
        let clock_id = ptp.clock.active_clock_id();
        active.ptp = Some(ptp);
        let group_uuid = random_uuid_v4_upper();

        for (sel, _, _) in &resolved {
            emitter.status("CONNECTING", &sel.ip, None);
        }

        // Connect all stereo-pair / group members concurrently with automatic transient takeover retry
        // (up to 3 attempts) using exact decompiled RTSP protocol window (`latency.c 0x14049ae00`).
        let user_lat_ms = render_delay_ms;
        let mut join_set = tokio::task::JoinSet::new();
        for (idx, (sel, ip, desc)) in resolved.iter().cloned().enumerate() {
            let cred = pins
                .get(&sel.ip)
                .cloned()
                .or_else(|| passwords.get(&sel.ip).cloned());
            let peer_ips_c = peer_ips.clone();
            let group_uuid_c = group_uuid.clone();
            let recv_event_tx_c = recv_event_tx.clone();
            let target_id = idx as u32 + 1;
            join_set.spawn(async move {
                let mut cur_sr = actual_sr;
                let mut cur_fmt = format;
                let mut last_err: Option<AirPlayError> = None;

                for attempt in 1..=3u32 {
                    let (lat_min_s, lat_max_s) = compute_protocol_window_samples(
                        &desc.model,
                        cur_fmt.codec,
                        cur_sr,
                        user_lat_ms,
                        calibration_session_max_ms,
                    );
                    let attempt_res = async {
                        let mut conn = AirPlay2Connection::connect_and_pair(
                            desc.clone(),
                            ip,
                            cred.as_deref(),
                        )
                        .await?;
                        let mut setup_res = conn
                            .setup_realtime_ptp(
                                cur_fmt,
                                clock_id,
                                &group_uuid_c,
                                &peer_ips_c,
                                local_control_port,
                                lat_min_s,
                                lat_max_s,
                                start_seq,
                                start_rtptime,
                                target_id,
                                Some(recv_event_tx_c.clone()),
                            )
                            .await;
                        if matches!(setup_res, Err(AirPlayError::CodecUnsupported(_)))
                            && cur_sr == SampleRate::Hz48000
                        {
                            tracing::warn!(
                                "Receiver {} rejected 48 kHz; falling back to 44.1 kHz",
                                sel.ip
                            );
                            cur_sr = SampleRate::Hz44100;
                            cur_fmt = AudioFormat::new(actual_codec, cur_sr);
                            let (fb_min, fb_max) = compute_protocol_window_samples(
                                &desc.model,
                                cur_fmt.codec,
                                cur_sr,
                                user_lat_ms,
                                calibration_session_max_ms,
                            );
                            setup_res = conn
                                .setup_realtime_ptp(
                                    cur_fmt,
                                    clock_id,
                                    &group_uuid_c,
                                    &peer_ips_c,
                                    local_control_port,
                                    fb_min,
                                    fb_max,
                                    start_seq,
                                    start_rtptime,
                                    target_id,
                                    Some(recv_event_tx_c.clone()),
                                )
                                .await;
                        }
                        setup_res.map(|res| (conn, res, cur_sr))
                    }
                    .await;

                    match attempt_res {
                        Ok(ok) => return (sel, ip, Ok(ok)),
                        Err(e) => {
                            if attempt < 3 && is_transient_takeover_error(&e) {
                                tracing::warn!(
                                    "Transient connect/takeover error for {} (attempt {}/3): {e}; retrying...",
                                    sel.ip,
                                    attempt
                                );
                                tokio::time::sleep(Duration::from_millis(450 * attempt as u64)).await;
                                last_err = Some(e);
                                continue;
                            }
                            return (sel, ip, Err(e));
                        }
                    }
                }
                (
                    sel,
                    ip,
                    Err(last_err.unwrap_or_else(|| AirPlayError::Timeout("Takeover retries exhausted".into()))),
                )
            });
        }

        let mut connected_endpoints = Vec::new();
        while let Some(joined) = join_set.join_next().await {
            let Ok((sel, ip, outcome)) = joined else { continue };
            match outcome {
                Ok((conn, res, negotiated_sr)) => {
                    if negotiated_sr != actual_sr {
                        actual_sr = negotiated_sr;
                        format = AudioFormat::new(actual_codec, actual_sr);
                    }
                    rtp_targets.push(res.rtp_target);
                    active.ap2_conns.insert(ip, conn);
                    connected_ips.push(sel.ip.clone());
                    connected_endpoints.push(sel.clone());
                    emitter.status("CONNECTED", &sel.ip, None);
                    emitter.codec_endpoint_active(&sel.ip, actual_codec.as_str());
                }
                Err(AirPlayError::AuthRequired(kind)) => {
                    tracing::warn!("AirPlay2 AuthRequired ({kind}) for {}", sel.ip);
                    let evt_kind = if kind == "PASSWORD" { "PASSWORD_REQUIRED" } else { "PIN_REQUIRED" };
                    emitter.emit("Auth", evt_kind, json!({ "ip": sel.ip }), &sel.ip);
                }
                Err(e) => {
                    tracing::error!("AirPlay2 connect/setup failed for {}: {e}", sel.ip);
                    emitter.status("FAILED", &sel.ip, Some(&e.to_string()));
                }
            }
        }
        active.endpoints = connected_endpoints;
    }

    if !rtp_targets.is_empty() {
        let (tx, rx) = crossbeam_channel::bounded::<Vec<f32>>(12);
        let final_lat_samples = actual_sr.ms_to_samples(latency_ms as u64) as u32;
        let timing_anchor = if let Some(ptp) = &active.ptp {
            let clk_id = ptp.clock.clone();
            let clk_ns = ptp.clock.clone();
            TimingAnchor::Ptp {
                clock_id: Arc::new(move || clk_id.active_clock_id()),
                now_nanos: Arc::new(move || clk_ns.now_nanos()),
            }
        } else {
            TimingAnchor::Ntp
        };
        let cap_res = WasapiLoopbackCapture::start_loopback(capture_device_id.clone(), actual_sr, tx.clone());
        match cap_res {
            Ok(cap) => match AudioStreamer::start(
                format,
                rx,
                rtp_targets,
                control_sock,
                start_seq,
                start_rtptime,
                final_lat_samples,
                timing_anchor,
            ) {
                Ok(streamer) => {
                    // Apply persisted EQ, Standby, & Vernier sync offset immediately before streaming audio
                    eq_state.apply_to(&mut streamer.eq().lock());
                    streamer.set_standby_config(standby_enabled, standby_timeout_mins);
                    if vernier_offset_ms != 0 {
                        let v_samples = ((vernier_offset_ms as i64 * actual_sr.hz() as i64) / 1000) as i32;
                        streamer.set_vernier_offset_samples(v_samples);
                    }
                    let profile = if use_raop {
                        if connected_ips.len() > 1 { "ClassicRaopGroup" } else { "ClassicRaopSolo" }
                    } else if connected_ips.len() > 1 {
                        "GroupHomePod"
                    } else {
                        "NativePtpHomePod"
                    };
                    active.actual_codec = actual_codec;
                    active.actual_sr = actual_sr;
                    active.profile = profile.to_string();
                    active.pcm_tx = Some(tx);
                    active.capture = Some(cap);
                    active.streamer = Some(streamer);
                    emitter.codec_active(actual_codec.as_str());
                    emitter.session_started(active.id, &connected_ips.join(","), profile, actual_codec.as_str(), actual_sr.hz());
                }
                Err(e) => tracing::error!("AudioStreamer::start failed: {e}"),
            },
            Err(e) => tracing::error!("WasapiLoopbackCapture::start_loopback failed: {e}"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn rotating_log_writer_rotates_and_caps_at_2x_limit() {
        let dir = std::env::temp_dir().join(format!(
            "airstereo2_log_rot_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();

        let max_bytes = 2048u64;
        {
            let mut w = RotatingLogWriter::open(&dir, "service.log", max_bytes).unwrap();
            let line = vec![b'A'; 256];
            for _ in 0..40 {
                w.write_all(&line).unwrap();
            }
            w.flush().unwrap();
        }

        let active_len = std::fs::metadata(dir.join("service.log")).unwrap().len();
        let backup_len = std::fs::metadata(dir.join("service.log.1")).unwrap().len();
        assert!(active_len > 0 && active_len <= max_bytes, "active_len={active_len}");
        assert!(backup_len > 0 && backup_len <= max_bytes, "backup_len={backup_len}");
        assert!(active_len + backup_len <= max_bytes * 2);
        assert_eq!(LOG_ROTATE_BYTES, 4 * 1024 * 1024);

        let _ = std::fs::remove_dir_all(&dir);
    }
}
