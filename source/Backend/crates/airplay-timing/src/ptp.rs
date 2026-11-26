use airplay_core::Result;
use std::collections::{HashMap, VecDeque};
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;
use tokio::sync::{Notify, RwLock};

/// Monotonic high-precision PTP clock anchored once to Unix epoch nanoseconds and
/// driven by Windows QPC (`Instant`). Supports true BMCA PTP Slave mode synced to
/// a HomePod stereo-pair shared Grandmaster as well as OwnTone Sender Master fallback.
#[derive(Debug)]
pub struct PtpClock {
    pub clock_id: u64,
    epoch_nanos: u64,
    origin: Instant,
    active_gm_clock_id: AtomicU64,
    offset_ns: AtomicI64,
    drift_ppb: AtomicI64,
    sample_count: AtomicU64,
    slave_mode: AtomicBool,
}

impl PtpClock {
    pub fn new(clock_id: u64) -> Self {
        let masked = clock_id & 0x7FFF_FFFF_FFFF_FFFF;
        let epoch_nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos() as u64;
        Self {
            clock_id: masked,
            epoch_nanos,
            origin: Instant::now(),
            active_gm_clock_id: AtomicU64::new(masked),
            offset_ns: AtomicI64::new(0),
            drift_ppb: AtomicI64::new(0),
            sample_count: AtomicU64::new(0),
            slave_mode: AtomicBool::new(false),
        }
    }

    pub fn local_now_nanos(&self) -> u64 {
        self.epoch_nanos
            .saturating_add(self.origin.elapsed().as_nanos() as u64)
    }

    /// Current PTP timeline time in nanoseconds (`local + filtered_offset` in slave mode).
    pub fn now_nanos(&self) -> u64 {
        let local = self.local_now_nanos() as i128;
        let off = self.offset_ns.load(Ordering::Relaxed) as i128;
        (local + off).max(0) as u64
    }

    pub fn active_clock_id(&self) -> u64 {
        self.active_gm_clock_id.load(Ordering::Relaxed)
    }

    pub fn is_slave(&self) -> bool {
        self.slave_mode.load(Ordering::Relaxed)
    }

    pub fn drift_ppb(&self) -> i64 {
        self.drift_ppb.load(Ordering::Relaxed)
    }

    pub fn sample_count(&self) -> u64 {
        self.sample_count.load(Ordering::Relaxed)
    }

    pub fn clock_id_hex(&self) -> String {
        format!("{:016x}", self.active_clock_id())
    }

    /// Encode 10-byte IEEE 1588v2 timestamp (48-bit seconds + 32-bit nanoseconds).
    pub fn encode_ptp_timestamp(nanos: u64) -> [u8; 10] {
        let secs = nanos / 1_000_000_000;
        let sub_ns = (nanos % 1_000_000_000) as u32;
        let mut out = [0u8; 10];
        let s_be = secs.to_be_bytes();
        out[0..6].copy_from_slice(&s_be[2..8]);
        out[6..10].copy_from_slice(&sub_ns.to_be_bytes());
        out
    }

    /// Decode 10-byte IEEE 1588v2 timestamp into nanoseconds.
    pub fn decode_ptp_timestamp(b: &[u8]) -> u64 {
        let secs = ((b[0] as u64) << 40)
            | ((b[1] as u64) << 32)
            | ((b[2] as u64) << 24)
            | ((b[3] as u64) << 16)
            | ((b[4] as u64) << 8)
            | (b[5] as u64);
        let ns = u32::from_be_bytes([b[6], b[7], b[8], b[9]]) as u64;
        secs.saturating_mul(1_000_000_000).saturating_add(ns)
    }
}

/// Active PTP offset filter & 64-sample OLS drift estimator
/// (exact match to decompiled `ptp.c 0x140102b20` & `0x140102520`).
#[derive(Debug, Default)]
pub struct PtpOffsetFilter {
    initialized: bool,
    filtered_ns: i64,
    fast_alpha_remaining: u8,
    history: VecDeque<(u64, i64)>,
    pub drift_ppb: i64,
}

impl PtpOffsetFilter {
    pub fn reset(&mut self) {
        self.initialized = false;
        self.filtered_ns = 0;
        self.fast_alpha_remaining = 0;
        self.history.clear();
        self.drift_ppb = 0;
    }

    pub fn update(&mut self, now_ns: u64, raw_offset_ns: i64, delay_ns: u64) -> Option<(i64, i64)> {
        if delay_ns > 200_000_000 {
            return None;
        }
        if !self.initialized || (raw_offset_ns - self.filtered_ns).abs() > 100_000_000 {
            self.initialized = true;
            self.filtered_ns = raw_offset_ns;
            self.fast_alpha_remaining = 7;
            self.history.clear();
            self.drift_ppb = 0;
            self.history.push_back((now_ns, self.filtered_ns));
            return Some((self.filtered_ns, 0));
        }

        // Asymmetric negative-spike clamp (-2.5 ms): guards against Wi-Fi downlink queuing bursts
        let clamped = if raw_offset_ns - self.filtered_ns < -2_500_000 {
            self.filtered_ns - 2_500_000
        } else {
            raw_offset_ns
        };

        let alpha = if self.fast_alpha_remaining > 0 {
            self.fast_alpha_remaining -= 1;
            0.75
        } else {
            0.25
        };
        self.filtered_ns =
            ((1.0 - alpha) * self.filtered_ns as f64 + alpha * clamped as f64).round() as i64;

        if self.history.len() >= 64 {
            self.history.pop_front();
        }
        self.history.push_back((now_ns, self.filtered_ns));

        // OLS linear regression drift estimator (len >= 3 && span >= 5.0 s; ±200_000 ppb).
        // Subtract BOTH `t0` and `y0` in integer domain before casting to `f64` so a ~1.8e18 ns
        // epoch offset does not suffer catastrophic cancellation in IEEE-754 f64 (53-bit mantissa).
        if self.history.len() >= 3 {
            let (t0, y0) = self.history.front().copied().unwrap();
            let (t1, _) = self.history.back().copied().unwrap();
            if t1.saturating_sub(t0) >= 5_000_000_000 {
                let n = self.history.len() as f64;
                let mut sum_x = 0.0f64;
                let mut sum_y = 0.0f64;
                for &(t, y) in &self.history {
                    sum_x += (t.saturating_sub(t0)) as f64;
                    sum_y += (y - y0) as f64;
                }
                let mean_x = sum_x / n;
                let mean_y = sum_y / n;
                let mut cov = 0.0f64;
                let mut var = 0.0f64;
                for &(t, y) in &self.history {
                    let dx = (t.saturating_sub(t0)) as f64 - mean_x;
                    let dy = (y - y0) as f64 - mean_y;
                    cov += dx * dy;
                    var += dx * dx;
                }
                if var > 1.0 {
                    let slope_ppb = ((cov / var) * 1_000_000_000.0)
                        .round()
                        .clamp(-200_000.0, 200_000.0) as i64;
                    self.drift_ppb = slope_ppb;
                }
            }
        }

        Some((self.filtered_ns, self.drift_ppb))
    }
}

/// Parsed IEEE 1588v2 Announce tuple for BMCA Peer Grandmaster Consensus (`ptp.c 0x140106430`).
#[derive(Debug, Clone, PartialEq, Eq)]
struct PeerAnnounce {
    sender_clock_id: u64,
    gm_clock_id: u64,
    gm_priority1: u8,
    gm_clock_class: u8,
    gm_clock_accuracy: u8,
    gm_variance: u16,
    gm_priority2: u8,
    steps_removed: u16,
}

impl PeerAnnounce {
    fn parse(buf: &[u8]) -> Option<Self> {
        if buf.len() < 64 || (buf[0] & 0x0F) != 0x0B {
            return None;
        }
        let sender_clock_id = u64::from_be_bytes(buf[20..28].try_into().ok()?);
        let gm_priority1 = buf[47];
        let gm_clock_class = buf[48];
        let gm_clock_accuracy = buf[49];
        let gm_variance = u16::from_be_bytes([buf[50], buf[51]]);
        let gm_priority2 = buf[52];
        let gm_clock_id = u64::from_be_bytes(buf[53..61].try_into().ok()?);
        let steps_removed = u16::from_be_bytes([buf[61], buf[62]]);
        Some(Self {
            sender_clock_id,
            gm_clock_id,
            gm_priority1,
            gm_clock_class,
            gm_clock_accuracy,
            gm_variance,
            gm_priority2,
            steps_removed,
        })
    }

    fn gm_tuple(&self) -> (u64, u16, u8, u8, u8, u8) {
        (
            self.gm_clock_id,
            self.gm_variance,
            self.gm_priority1,
            self.gm_clock_class,
            self.gm_clock_accuracy,
            self.gm_priority2,
        )
    }
}

#[derive(Debug, Clone)]
struct SlaveState {
    gm_ip: IpAddr,
    gm_clock_id: u64,
    entered_at: Instant,
    last_sync_at: Option<Instant>,
}

/// AirPlay 2 PTP engine with Mac-style BMCA Peer Consensus Slave Mode (`priority1=250` negotiation
/// yielding to HomePod stereo pair shared Grandmaster + `Delay_Req`/`Delay_Resp` + `PtpOffsetFilter`)
/// and OwnTone-compatible Sender Master fallback (`priority1=128`, `clockClass=6`, 106B Apple Signaling).
pub struct PtpMasterServer {
    pub clock: Arc<PtpClock>,
    peers: Arc<RwLock<Vec<IpAddr>>>,
    slave_state: Arc<RwLock<Option<SlaveState>>>,
    bmca_negotiating_until: Arc<RwLock<Option<Instant>>>,
    consensus_notify: Arc<Notify>,
    stop_tx: tokio::sync::watch::Sender<bool>,
}

impl PtpMasterServer {
    pub async fn start(clock_id: u64) -> Result<Self> {
        let clock = Arc::new(PtpClock::new(clock_id));
        let peers: Arc<RwLock<Vec<IpAddr>>> = Arc::new(RwLock::new(Vec::new()));
        let slave_state: Arc<RwLock<Option<SlaveState>>> = Arc::new(RwLock::new(None));
        let bmca_negotiating_until: Arc<RwLock<Option<Instant>>> = Arc::new(RwLock::new(None));
        let consensus_notify = Arc::new(Notify::new());
        let (stop_tx, stop_rx) = tokio::sync::watch::channel(false);

        let ev_sock = Arc::new(bind_udp_retry(319).await?);
        let gen_sock = Arc::new(bind_udp_retry(320).await?);

        let pending_sync_rx: Arc<RwLock<HashMap<(IpAddr, u16), u64>>> =
            Arc::new(RwLock::new(HashMap::new()));
        let pending_delay_req: Arc<RwLock<HashMap<u16, u64>>> =
            Arc::new(RwLock::new(HashMap::new()));
        let delay_req_seq_shared = Arc::new(AtomicU64::new(1));

        // Periodic 125 ms loop:
        // - In BMCA Negotiating phase (`peers >= 2`, first 650 ms): sends passive Announce (priority1=250) + Apple Signaling
        // - In BMCA Slave mode (yielded to HomePod shared GM): sends 1 Hz Apple Signaling (8 Hz Delay_Req is triggered directly on GM Follow_Up)
        // - Otherwise (OwnTone Sender Master): sends 8 Hz Sync/Follow_Up + 1 Hz Announce (priority1=128) + 1 Hz Apple Signaling
        {
            let clock = clock.clone();
            let peers = peers.clone();
            let slave_state = slave_state.clone();
            let neg_until = bmca_negotiating_until.clone();
            let ev_sock = ev_sock.clone();
            let gen_sock = gen_sock.clone();
            let mut stop = stop_rx.clone();
            tokio::spawn(async move {
                let mut seq: u16 = 0;
                let mut announce_seq: u16 = 0;
                let mut signaling_seq: u16 = 0;
                let mut tick = tokio::time::interval(Duration::from_millis(125));
                tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
                let mut counter: u64 = 0;
                loop {
                    tokio::select! {
                        _ = stop.changed() => break,
                        _ = tick.tick() => {
                            let peer_list = peers.read().await.clone();
                            if peer_list.is_empty() { continue; }

                            // Check whether BMCA Slave GM went silent for > 4s -> fall back cleanly to Master
                            let active_slave = {
                                let mut sl = slave_state.write().await;
                                if let Some(st) = sl.as_ref() {
                                    let ref_time = st.last_sync_at.unwrap_or(st.entered_at);
                                    if ref_time.elapsed() > Duration::from_secs(4) {
                                        tracing::warn!(
                                            "BMCA: Remote GM {} ({:016x}) silent >4s; falling back to Sender Master",
                                            st.gm_ip, st.gm_clock_id
                                        );
                                        *sl = None;
                                        clock.slave_mode.store(false, Ordering::Relaxed);
                                        clock.active_gm_clock_id.store(clock.clock_id, Ordering::Relaxed);
                                        clock.offset_ns.store(0, Ordering::Relaxed);
                                        None
                                    } else {
                                        Some(st.clone())
                                    }
                                } else {
                                    None
                                }
                            };

                            if active_slave.is_some() {
                                // BMCA Slave mode: send Apple Signaling every 1 s (8 Hz Delay_Req is triggered directly on GM Follow_Up)
                                if counter % 8 == 0 {
                                    let sig = build_apple_signaling(clock.clock_id, signaling_seq);
                                    signaling_seq = signaling_seq.wrapping_add(1);
                                    for &ip in &peer_list {
                                        let _ = gen_sock.send_to(&sig, SocketAddr::new(ip, 320)).await;
                                    }
                                }
                                counter = counter.wrapping_add(1);
                                continue;
                            }

                            // Check if still inside initial BMCA negotiation window (priority1=250 so HomePod wins)
                            let negotiating = neg_until
                                .read()
                                .await
                                .is_some_and(|until| Instant::now() < until);
                            if negotiating {
                                if counter % 2 == 0 {
                                    let sig = build_apple_signaling(clock.clock_id, signaling_seq);
                                    signaling_seq = signaling_seq.wrapping_add(1);
                                    let pass_ann = build_passive_announce(clock.clock_id, announce_seq, 250, 248);
                                    announce_seq = announce_seq.wrapping_add(1);
                                    for &ip in &peer_list {
                                        let _ = gen_sock.send_to(&sig, SocketAddr::new(ip, 320)).await;
                                        let _ = gen_sock.send_to(&pass_ann, SocketAddr::new(ip, 320)).await;
                                    }
                                }
                                counter = counter.wrapping_add(1);
                                continue;
                            }

                            // OwnTone Sender Master mode (125 ms Sync/Follow_Up, 1 s Announce + Apple Signaling)
                            let ts = clock.now_nanos();
                            let sync_pkt = build_sync(clock.clock_id, seq, ts);
                            let fup_pkt = build_follow_up(clock.clock_id, seq, ts);
                            seq = seq.wrapping_add(1);
                            for &ip in &peer_list {
                                let _ = ev_sock.send_to(&sync_pkt, SocketAddr::new(ip, 319)).await;
                            }
                            tokio::task::yield_now().await;
                            for &ip in &peer_list {
                                let _ = gen_sock.send_to(&fup_pkt, SocketAddr::new(ip, 320)).await;
                            }
                            if counter % 8 == 0 {
                                let ann = build_announce(clock.clock_id, announce_seq);
                                announce_seq = announce_seq.wrapping_add(1);
                                let sig = build_apple_signaling(clock.clock_id, signaling_seq);
                                signaling_seq = signaling_seq.wrapping_add(1);
                                for &ip in &peer_list {
                                    let _ = gen_sock.send_to(&ann, SocketAddr::new(ip, 320)).await;
                                    let _ = gen_sock.send_to(&sig, SocketAddr::new(ip, 320)).await;
                                }
                            }
                            counter = counter.wrapping_add(1);
                        }
                    }
                }
            });
        }

        // Event listener (UDP 319): Sync observation (0x00), Delay_Req (0x01), Pdelay_Req (0x02)
        {
            let clock = clock.clone();
            let ev_sock_rx = ev_sock.clone();
            let gen_sock_tx = gen_sock.clone();
            let sync_rx_map = pending_sync_rx.clone();
            let mut stop = stop_rx.clone();
            tokio::spawn(async move {
                let mut buf = [0u8; 512];
                let mut consecutive_errs: u32 = 0;
                loop {
                    tokio::select! {
                        _ = stop.changed() => break,
                        res = ev_sock_rx.recv_from(&mut buf) => {
                            let (n, src) = match res {
                                Ok(v) => { consecutive_errs = 0; v }
                                Err(_) => {
                                    consecutive_errs += 1;
                                    let delay_ms = if consecutive_errs <= 3 { 1 } else if consecutive_errs <= 10 { 10 } else if consecutive_errs < 50 { 50 } else { break };
                                    tokio::time::sleep(Duration::from_millis(delay_ms)).await;
                                    continue;
                                }
                            };
                            if n < 34 { continue; }
                            let rx_ts = clock.local_now_nanos();
                            let msg_type = buf[0] & 0x0F;
                            let req_port_id = &buf[20..30];
                            let req_seq = u16::from_be_bytes([buf[30], buf[31]]);
                            match msg_type {
                                0x00 => {
                                    let mut map = sync_rx_map.write().await;
                                    if map.len() > 64 { map.clear(); }
                                    map.insert((src.ip(), req_seq), rx_ts);
                                }
                                0x01 => {
                                    if !clock.is_slave() {
                                        let resp = build_delay_resp(clock.clock_id, req_seq, clock.now_nanos(), req_port_id);
                                        let _ = gen_sock_tx.send_to(&resp, SocketAddr::new(src.ip(), 320)).await;
                                    }
                                }
                                0x02 => {
                                    let resp = build_pdelay_resp(clock.clock_id, req_seq, clock.now_nanos(), req_port_id);
                                    let _ = ev_sock_rx.send_to(&resp, SocketAddr::new(src.ip(), 319)).await;
                                    let tx_ts = clock.now_nanos();
                                    let fup = build_pdelay_resp_follow_up(clock.clock_id, req_seq, tx_ts, req_port_id);
                                    let _ = gen_sock_tx.send_to(&fup, SocketAddr::new(src.ip(), 320)).await;
                                }
                                _ => {}
                            }
                        }
                    }
                }
            });
        }

        // General listener (UDP 320): Follow_Up (0x08), Delay_Resp (0x09), Announce (0x0B), Signaling (0x0C)
        {
            let clock = clock.clone();
            let peers_obs = peers.clone();
            let slave_state = slave_state.clone();
            let notify_c = consensus_notify.clone();
            let ev_sock_tx = ev_sock;
            let gen_sock_rx = gen_sock;
            let sync_rx_map = pending_sync_rx;
            let pending_dreq = pending_delay_req;
            let dreq_seq = delay_req_seq_shared;
            let mut stop = stop_rx;
            tokio::spawn(async move {
                let mut buf = [0u8; 512];
                let mut announces: HashMap<IpAddr, PeerAnnounce> = HashMap::new();
                let mut filter = PtpOffsetFilter::default();
                let mut last_t1_t2: Option<(u64, u64)> = None;
                let mut consecutive_errs: u32 = 0;
                loop {
                    tokio::select! {
                        _ = stop.changed() => break,
                        res = gen_sock_rx.recv_from(&mut buf) => {
                            let (n, src) = match res {
                                Ok(v) => { consecutive_errs = 0; v }
                                Err(_) => {
                                    consecutive_errs += 1;
                                    let delay_ms = if consecutive_errs <= 3 { 1 } else if consecutive_errs <= 10 { 10 } else if consecutive_errs < 50 { 50 } else { break };
                                    tokio::time::sleep(Duration::from_millis(delay_ms)).await;
                                    continue;
                                }
                            };
                            if n < 34 { continue; }
                            let msg_type = buf[0] & 0x0F;
                            match msg_type {
                                0x08 if n >= 44 => {
                                    let seq = u16::from_be_bytes([buf[30], buf[31]]);
                                    if let Some(t2_ns) = sync_rx_map.write().await.remove(&(src.ip(), seq)) {
                                        let t1_ns = PtpClock::decode_ptp_timestamp(&buf[34..44]);
                                        let corr_raw = i64::from_be_bytes(buf[8..16].try_into().unwrap_or([0; 8]));
                                        let corr_ns = corr_raw >> 16;
                                        let t1_adj = (t1_ns as i128 + corr_ns as i128).max(0) as u64;

                                        let is_our_gm = {
                                            let mut sl = slave_state.write().await;
                                            if let Some(st) = sl.as_mut() {
                                                if st.gm_ip == src.ip() {
                                                    st.last_sync_at = Some(Instant::now());
                                                    true
                                                } else {
                                                    false
                                                }
                                            } else {
                                                false
                                            }
                                        };

                                        if is_our_gm {
                                            // Exact match to original 1.0.9 (`ptp.c 0x140105ea0`):
                                            // Record `(t1, t2)` and immediately send `Delay_Req` (8 Hz tightly coupled 4-timestamp exchange).
                                            last_t1_t2 = Some((t1_adj, t2_ns));
                                            let dseq = dreq_seq.fetch_add(1, Ordering::Relaxed) as u16;
                                            let pkt = build_delay_req(clock.clock_id, dseq);
                                            let t3 = clock.local_now_nanos();
                                            {
                                                let mut m = pending_dreq.write().await;
                                                if m.len() > 32 { m.clear(); }
                                                m.insert(dseq, t3);
                                            }
                                            let _ = ev_sock_tx.send_to(&pkt, SocketAddr::new(src.ip(), 319)).await;
                                        } else if !clock.is_slave() {
                                            // Passive drift observation when acting as Master
                                            let raw_off = (t1_adj as i128 - t2_ns as i128) as i64;
                                            if let Some((_filt, drift_ppb)) = filter.update(t2_ns, raw_off, 0) {
                                                clock.drift_ppb.store(drift_ppb, Ordering::Relaxed);
                                                clock.sample_count.fetch_add(1, Ordering::Relaxed);
                                            }
                                        }
                                    }
                                }
                                0x09 if n >= 54 => {
                                    // Active Delay_Resp from GM (`ptp.c 0x140105ea0`):
                                    // Consume complete 4-timestamp `(t1, t2, t3, t4)` exchange:
                                    // offset = ((t1 - t2) + (t4 - t3)) / 2, delay = |((t2 - t1) + (t4 - t3))| / 2
                                    let is_our_gm = slave_state
                                        .read()
                                        .await
                                        .as_ref()
                                        .is_some_and(|st| st.gm_ip == src.ip());
                                    if is_our_gm {
                                        let seq = u16::from_be_bytes([buf[30], buf[31]]);
                                        if let (Some(t3_ns), Some((t1_ns, t2_ns))) =
                                            (pending_dreq.write().await.remove(&seq), last_t1_t2.take())
                                        {
                                            let t4_ns = PtpClock::decode_ptp_timestamp(&buf[34..44]);
                                            let d1 = t1_ns as i128 - t2_ns as i128;
                                            let d2 = t4_ns as i128 - t3_ns as i128;
                                            let offset = ((d1 + d2) / 2) as i64;
                                            let delay = (((t2_ns as i128 - t1_ns as i128) + d2).abs() / 2) as u64;
                                            if delay <= 200_000_000 {
                                                let rx_now_ns = clock.local_now_nanos();
                                                if let Some((filt_off, drift_ppb)) = filter.update(rx_now_ns, offset, delay) {
                                                    clock.offset_ns.store(filt_off, Ordering::Relaxed);
                                                    clock.drift_ppb.store(drift_ppb, Ordering::Relaxed);
                                                    clock.sample_count.fetch_add(1, Ordering::Relaxed);
                                                }
                                            }
                                        }
                                    }
                                }
                                0x0B if n >= 64 => {
                                    if let Some(ann) = PeerAnnounce::parse(&buf[..n]) {
                                        announces.insert(src.ip(), ann);
                                        let peer_list = peers_obs.read().await.clone();
                                        if let Some((gm_ip, gm_id)) = evaluate_peer_consensus(&announces, &peer_list) {
                                            let mut sl = slave_state.write().await;
                                            let changed = sl.as_ref().map(|s| (s.gm_ip, s.gm_clock_id)) != Some((gm_ip, gm_id));
                                            if changed {
                                                *sl = Some(SlaveState {
                                                    gm_ip,
                                                    gm_clock_id: gm_id,
                                                    entered_at: Instant::now(),
                                                    last_sync_at: None,
                                                });
                                                filter.reset();
                                                last_t1_t2 = None;
                                                clock.active_gm_clock_id.store(gm_id, Ordering::Relaxed);
                                                clock.slave_mode.store(true, Ordering::Relaxed);
                                                tracing::info!(
                                                    "BMCA: Yielded master to remote {} (GM={:016x}, {} peers); transitioning to slave mode",
                                                    gm_ip,
                                                    gm_id,
                                                    peer_list.len()
                                                );
                                                notify_c.notify_waiters();
                                            }
                                        }
                                    }
                                }
                                0x0C if n >= 54 => {
                                    if !clock.is_slave() {
                                        let req_port_id = &buf[20..30];
                                        let req_seq = u16::from_be_bytes([buf[30], buf[31]]);
                                        let tlv_type = u16::from_be_bytes([buf[44], buf[45]]);
                                        if tlv_type == 0x0004 {
                                            let req_msg_type = buf[48] & 0xF0;
                                            let log_interval = buf[49];
                                            let duration_secs = u32::from_be_bytes([buf[50], buf[51], buf[52], buf[53]]).max(60);
                                            let grant = build_signaling_grant(
                                                clock.clock_id,
                                                req_seq,
                                                req_port_id,
                                                req_msg_type,
                                                log_interval,
                                                duration_secs,
                                            );
                                            let _ = gen_sock_rx.send_to(&grant, SocketAddr::new(src.ip(), 320)).await;
                                        }
                                    }
                                }
                                _ => {}
                            }
                        }
                    }
                }
            });
        }

        Ok(Self {
            clock,
            peers,
            slave_state,
            bmca_negotiating_until,
            consensus_notify,
            stop_tx,
        })
    }

    pub async fn set_peers(&self, ips: Vec<IpAddr>) {
        if ips.len() >= 2 {
            *self.bmca_negotiating_until.write().await = Some(Instant::now() + Duration::from_millis(650));
        } else {
            *self.bmca_negotiating_until.write().await = None;
        }
        *self.peers.write().await = ips;
    }

    /// Wait up to `timeout` for BMCA peer Grandmaster consensus (and first Sync offset lock)
    /// when connecting to a multi-speaker group. Returns `Some((gm_ip, gm_clock_id))` if slave mode engaged.
    pub async fn wait_for_bmca_consensus(&self, timeout: Duration) -> Option<(IpAddr, u64)> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(st) = self.slave_state.read().await.as_ref() {
                // Give a brief 180 ms window for the first Sync/Follow_Up to lock `offset_ns`
                let lock_end = (Instant::now() + Duration::from_millis(180)).min(deadline);
                while self.clock.sample_count() == 0 && Instant::now() < lock_end {
                    tokio::time::sleep(Duration::from_millis(20)).await;
                }
                return Some((st.gm_ip, st.gm_clock_id));
            }
            let rem = deadline.saturating_duration_since(Instant::now());
            if rem.is_zero() {
                return None;
            }
            let _ = tokio::time::timeout(rem, self.consensus_notify.notified()).await;
        }
    }

    pub fn stop(&self) {
        let _ = self.stop_tx.send(true);
    }
}

/// Exact decompiled BMCA Peer Grandmaster Consensus (`ptp.c 0x140106430`):
/// requires `>= 2` peers, every peer has announced the identical Grandmaster tuple,
/// and exactly one peer has `steps_removed == 0 && sender_clock_id == gm_clock_id`.
fn evaluate_peer_consensus(
    announces: &HashMap<IpAddr, PeerAnnounce>,
    peers: &[IpAddr],
) -> Option<(IpAddr, u64)> {
    if peers.len() < 2 {
        return None;
    }
    let mut expected_gm: Option<(u64, u16, u8, u8, u8, u8)> = None;
    let mut leader: Option<(IpAddr, u64)> = None;
    for &ip in peers {
        let ann = announces.get(&ip)?;
        let tuple = ann.gm_tuple();
        match expected_gm {
            None => expected_gm = Some(tuple),
            Some(ref exp) if exp != &tuple => return None,
            _ => {}
        }
        if ann.steps_removed == 0 && ann.sender_clock_id == ann.gm_clock_id {
            if leader.is_some() {
                return None;
            }
            leader = Some((ip, ann.gm_clock_id));
        }
    }
    leader
}

async fn bind_udp_retry(port: u16) -> Result<UdpSocket> {
    let mut last_err = None;
    for attempt in 0..4 {
        match UdpSocket::bind((Ipv4Addr::UNSPECIFIED, port)).await {
            Ok(s) => return Ok(s),
            Err(e) => {
                last_err = Some(e);
                if attempt < 3 {
                    tokio::time::sleep(Duration::from_millis(50)).await;
                }
            }
        }
    }
    Err(last_err.unwrap().into())
}

fn write_ptp_header(
    buf: &mut [u8],
    msg_type: u8,
    len: u16,
    flags: u16,
    clock_id: u64,
    seq: u16,
    control: u8,
    log_interval: i8,
) {
    buf[0] = 0x10 | (msg_type & 0x0F); // transportSpecific=1 (gPTP/AirPlay)
    buf[1] = 0x02; // PTPv2
    buf[2..4].copy_from_slice(&len.to_be_bytes());
    buf[4] = 0x00; // domainNumber
    buf[6..8].copy_from_slice(&flags.to_be_bytes());
    buf[20..28].copy_from_slice(&clock_id.to_be_bytes());
    buf[28..30].copy_from_slice(&1u16.to_be_bytes()); // sourcePortNumber = 1
    buf[30..32].copy_from_slice(&seq.to_be_bytes());
    buf[32] = control;
    buf[33] = log_interval as u8;
}

fn write_tlv(dst: &mut [u8], tlv_type: u16, data: &[u8]) {
    dst[0..2].copy_from_slice(&tlv_type.to_be_bytes());
    dst[2..4].copy_from_slice(&(data.len() as u16).to_be_bytes());
    dst[4..4 + data.len()].copy_from_slice(data);
}

fn build_sync(clock_id: u64, seq: u16, ts_ns: u64) -> [u8; 44] {
    let mut b = [0u8; 44];
    write_ptp_header(&mut b, 0x00, 44, 0x0608, clock_id, seq, 0x00, -3);
    b[34..44].copy_from_slice(&PtpClock::encode_ptp_timestamp(ts_ns));
    b
}

fn build_delay_req(clock_id: u64, seq: u16) -> [u8; 44] {
    let mut b = [0u8; 44];
    write_ptp_header(&mut b, 0x01, 44, 0x0408, clock_id, seq, 0x01, 0x7F_u8 as i8);
    b
}

fn build_follow_up(clock_id: u64, seq: u16, ts_ns: u64) -> [u8; 96] {
    let mut b = [0u8; 96];
    write_ptp_header(&mut b, 0x08, 96, 0x0408, clock_id, seq, 0x02, -3);
    b[34..44].copy_from_slice(&PtpClock::encode_ptp_timestamp(ts_ns));

    let mut rate = [0u8; 28];
    rate[0..6].copy_from_slice(&[0x00, 0x80, 0xC2, 0x00, 0x00, 0x01]);
    write_tlv(&mut b[44..76], 0x0003, &rate);

    let mut ident = [0u8; 16];
    ident[0..6].copy_from_slice(&[0x00, 0x0D, 0x93, 0x00, 0x00, 0x04]);
    ident[6..14].copy_from_slice(&clock_id.to_be_bytes());
    write_tlv(&mut b[76..96], 0x0003, &ident);
    b
}

fn build_announce(clock_id: u64, seq: u16) -> [u8; 76] {
    let mut b = [0u8; 76];
    write_ptp_header(&mut b, 0x0B, 76, 0x0408, clock_id, seq, 0x05, 0);
    b[47] = 128; // grandmasterPriority1
    b[48] = 6; // clockClass
    b[49] = 0x21; // clockAccuracy (100 ns)
    b[50..52].copy_from_slice(&0x436Au16.to_be_bytes());
    b[52] = 128;
    b[53..61].copy_from_slice(&clock_id.to_be_bytes());
    b[63] = 0x20; // GPS
    b[64..68].copy_from_slice(&[0x00, 0x08, 0x00, 0x08]);
    b[68..76].copy_from_slice(&clock_id.to_be_bytes());
    b
}

/// 64-byte Passive BMCA Announce (`ptp.c 0x1403c5540`: priority1=250 so HomePod 248 wins BMCA).
fn build_passive_announce(clock_id: u64, seq: u16, priority1: u8, clock_class: u8) -> [u8; 64] {
    let mut b = [0u8; 64];
    write_ptp_header(&mut b, 0x0B, 64, 0x0000, clock_id, seq, 0x05, 1);
    b[44..46].copy_from_slice(&37u16.to_be_bytes()); // currentUtcOffset = 37
    b[47] = priority1;
    b[48] = clock_class;
    b[49] = 0xFE;
    b[50..52].copy_from_slice(&0xFFFFu16.to_be_bytes());
    b[52] = 128;
    b[53..61].copy_from_slice(&clock_id.to_be_bytes());
    b[63] = 0xA0; // Internal Oscillator
    b
}

/// 106-byte Apple AirPlay 2 Signaling packet (`ptp.c 0x1405b0950` + `0x1405b0e70`).
fn build_apple_signaling(clock_id: u64, seq: u16) -> [u8; 106] {
    let mut b = [0u8; 106];
    write_ptp_header(&mut b, 0x0C, 106, 0x0408, clock_id, seq, 0x05, -128);
    let mut tlv1 = [0u8; 22];
    tlv1[0..6].copy_from_slice(&[0x00, 0x0D, 0x93, 0x00, 0x00, 0x01]);
    tlv1[6..10].copy_from_slice(&[0x00, 0x00, 0x03, 0x01]);
    write_tlv(&mut b[44..70], 0x0003, &tlv1);

    let mut tlv2 = [0u8; 32];
    tlv2[0..6].copy_from_slice(&[0x00, 0x0D, 0x93, 0x00, 0x00, 0x05]);
    tlv2[6..10].copy_from_slice(&[0x00, 0x00, 0x03, 0x01]);
    write_tlv(&mut b[70..106], 0x0003, &tlv2);
    b
}

fn build_delay_resp(clock_id: u64, seq: u16, rx_ts_ns: u64, req_port_id: &[u8]) -> [u8; 54] {
    let mut b = [0u8; 54];
    write_ptp_header(&mut b, 0x09, 54, 0x0608, clock_id, seq, 0x03, -3);
    b[34..44].copy_from_slice(&PtpClock::encode_ptp_timestamp(rx_ts_ns));
    b[44..54].copy_from_slice(&req_port_id[..10]);
    b
}

fn build_pdelay_resp(clock_id: u64, seq: u16, rx_ts_ns: u64, req_port_id: &[u8]) -> [u8; 54] {
    let mut b = [0u8; 54];
    write_ptp_header(&mut b, 0x03, 54, 0x0608, clock_id, seq, 0x05, -3);
    b[34..44].copy_from_slice(&PtpClock::encode_ptp_timestamp(rx_ts_ns));
    b[44..54].copy_from_slice(&req_port_id[..10]);
    b
}

fn build_pdelay_resp_follow_up(clock_id: u64, seq: u16, tx_ts_ns: u64, req_port_id: &[u8]) -> [u8; 54] {
    let mut b = [0u8; 54];
    write_ptp_header(&mut b, 0x0A, 54, 0x0408, clock_id, seq, 0x05, -3);
    b[34..44].copy_from_slice(&PtpClock::encode_ptp_timestamp(tx_ts_ns));
    b[44..54].copy_from_slice(&req_port_id[..10]);
    b
}

fn build_signaling_grant(
    clock_id: u64,
    seq: u16,
    target_port_id: &[u8],
    msg_type_high_nibble: u8,
    log_interval: u8,
    duration_secs: u32,
) -> [u8; 56] {
    let mut b = [0u8; 56];
    write_ptp_header(&mut b, 0x0C, 56, 0x0400, clock_id, seq, 0x05, 0x7F_u8 as i8);
    b[34..44].copy_from_slice(&target_port_id[..10]);
    b[44..46].copy_from_slice(&0x0005u16.to_be_bytes());
    b[46..48].copy_from_slice(&8u16.to_be_bytes());
    b[48] = msg_type_high_nibble;
    b[49] = log_interval;
    b[50..54].copy_from_slice(&duration_secs.to_be_bytes());
    b[55] = 0x01;
    b
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn offset_filter_estimates_drift_at_large_epoch_offset() {
        let mut f = PtpOffsetFilter::default();
        let base_t: u64 = 1_795_694_898_000_000_000;
        let base_off: i64 = -1_795_694_800_000_000_000;
        let true_drift_ppb: i64 = 18_500; // +18.5 ppm
        let mut last_drift = 0i64;
        for i in 0..80u64 {
            let dt_ns = i * 100_000_000; // 10 Hz for 8.0 s
            let off = base_off + ((dt_ns as i128 * true_drift_ppb as i128) / 1_000_000_000) as i64;
            if let Some((_, d)) = f.update(base_t + dt_ns, off, 2_000_000) {
                last_drift = d;
            }
        }
        assert!(
            (last_drift - true_drift_ppb).abs() < 500,
            "expected drift ~{true_drift_ppb} ppb, got {last_drift} ppb"
        );
    }

    #[test]
    fn bmca_consensus_detects_shared_homepod_gm() {
        let ip1: IpAddr = "192.168.0.126".parse().unwrap();
        let ip2: IpAddr = "192.168.0.148".parse().unwrap();
        let gm_id: u64 = 0x40edcf020cc60008;
        let mut map = HashMap::new();
        map.insert(
            ip1,
            PeerAnnounce {
                sender_clock_id: 0x1111,
                gm_clock_id: gm_id,
                gm_priority1: 248,
                gm_clock_class: 248,
                gm_clock_accuracy: 0xFE,
                gm_variance: 0xFFFF,
                gm_priority2: 248,
                steps_removed: 1,
            },
        );
        map.insert(
            ip2,
            PeerAnnounce {
                sender_clock_id: gm_id,
                gm_clock_id: gm_id,
                gm_priority1: 248,
                gm_clock_class: 248,
                gm_clock_accuracy: 0xFE,
                gm_variance: 0xFFFF,
                gm_priority2: 248,
                steps_removed: 0,
            },
        );
        let res = evaluate_peer_consensus(&map, &[ip1, ip2]);
        assert_eq!(res, Some((ip2, gm_id)));
    }
}
