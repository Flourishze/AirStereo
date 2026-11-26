use serde_json::{json, Value};
use std::io::{self, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

/// Emits AirStereo2 backend protocol v3 events on stdout (plain text at boot, JSON v3 after `IPC_FORMAT JSON 3`).
pub struct EventEmitter {
    json_mode: AtomicBool,
}

impl EventEmitter {
    pub const fn new() -> Self {
        Self {
            json_mode: AtomicBool::new(false),
        }
    }

    pub fn enable_json_v3(&self) {
        self.json_mode.store(true, Ordering::Relaxed);
    }

    pub fn emit(&self, tag: &str, kind: &str, data: Value, line_args: &str) {
        let ts = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos() as u64;
        let line = if line_args.is_empty() {
            format!("[{tag}] {kind}")
        } else {
            format!("[{tag}] {kind} {line_args}")
        };
        let out = if self.json_mode.load(Ordering::Relaxed) {
            json!({
                "v": 3,
                "ts": ts,
                "tag": tag,
                "kind": kind,
                "data": data,
                "line": line
            })
            .to_string()
        } else {
            format!("ts={ts} {line}")
        };
        let mut stdout = io::stdout().lock();
        let _ = writeln!(stdout, "{out}");
        let _ = stdout.flush();
    }

    pub fn emit_banner(&self) {
        self.emit("Engine", "PROTOCOL", json!({ "version": 3 }), "3");
        self.emit(
            "Engine",
            "BUILD",
            json!({ "version": "1.1.1.0", "source": "open-rust" }),
            "1.1.1.0 open-rust mit",
        );
    }

    pub fn status(&self, kind: &str, ip: &str, reason: Option<&str>) {
        match reason {
            Some(r) => self.emit("Status", kind, json!({ "ip": ip, "reason": r }), &format!("{ip} {r}")),
            None => self.emit("Status", kind, json!({ "ip": ip }), ip),
        }
    }

    pub fn status_global(&self, kind: &str) {
        self.emit("Status", kind, json!({}), "");
    }

    pub fn latency_drift(&self, ip: &str, drift_ppb: i64, samples: u64) {
        self.emit(
            "Latency",
            "DRIFT",
            json!({ "ip": ip, "drift_ppb": drift_ppb, "samples": samples }),
            &format!("{ip} {drift_ppb} {samples}"),
        );
    }

    pub fn calibration_below_floor(&self, requested_ms: u32, floor_ms: u32) {
        self.emit(
            "Calibration",
            "LATENCY_BELOW_FLOOR",
            json!({ "requested_ms": requested_ms, "floor_ms": floor_ms }),
            &format!("{requested_ms} {floor_ms}"),
        );
    }

    pub fn remote_command(&self, ip: &str, cmd: &str) {
        self.emit(
            "Remote",
            "COMMAND",
            json!({ "ip": ip, "command": cmd }),
            &format!("{ip} {cmd}"),
        );
    }

    pub fn sync_offset(&self, kind: &str, ms: i32) {
        self.emit("SyncOffset", kind, json!({ "ms": ms }), &ms.to_string());
    }

    pub fn preempted(&self, ip: &str, takeover: u32, limit: u32) {
        self.emit(
            "Status",
            "PREEMPTED",
            json!({ "ip": ip, "takeover": takeover, "limit": limit }),
            &format!("{ip} {takeover} {limit}"),
        );
    }

    pub fn endpoint_status(&self, kind: &str, endpoint_id: &str, ip: &str, reason: Option<&str>) {
        match reason {
            Some(r) => self.emit(
                "EndpointStatus",
                kind,
                json!({ "endpoint_id": endpoint_id, "ip": ip, "reason": r }),
                &format!("{endpoint_id} {ip} {r}"),
            ),
            None => self.emit(
                "EndpointStatus",
                kind,
                json!({ "endpoint_id": endpoint_id, "ip": ip, "reason": Value::Null }),
                &format!("{endpoint_id} {ip}"),
            ),
        }
    }

    /// Exact formula from decompiled `0x1404c52d0` (`latency_emit.c L111-194`):
    /// `playback_ms = playout_ms + 20 + (rtt_ms / 2)` (one-way network latency + 20 ms local pipeline).
    pub fn latency_telemetry(&self, ip: &str, rtt_ms: f64, playout_ms: u32) {
        let rtt_int = rtt_ms.max(0.0).floor() as u32;
        let one_way_ms = rtt_int / 2;
        let playback_ms = playout_ms.saturating_add(20).saturating_add(one_way_ms);
        self.emit(
            "Latency",
            "RTT",
            json!({ "ip": ip, "ms": rtt_ms, "proc_ms": 0 }),
            &format!("{ip} {rtt_ms:.2} 0"),
        );
        self.emit(
            "Latency",
            "PLAYBACK",
            json!({ "ip": ip, "ms": playback_ms, "rtt_ms": rtt_int }),
            &format!("{ip} {playback_ms} {rtt_int}"),
        );
        self.emit(
            "Latency",
            "PLAYOUT",
            json!({ "ip": ip, "ms": playout_ms, "rtt_ms": rtt_int }),
            &format!("{ip} {playout_ms} {rtt_int}"),
        );
    }

    pub fn volume_current(&self, ip: &str, percent: i32) {
        self.emit(
            "Volume",
            "CURRENT",
            json!({ "ip": ip, "percent": percent }),
            &format!("{ip} {percent}"),
        );
    }

    pub fn session_started(&self, session_id: u64, devices_csv: &str, profile: &str, codec: &str, sample_rate_hz: u32) {
        let args = format!("id={session_id} device={devices_csv} profile={profile} codec={codec} sr={sample_rate_hz}");
        self.emit(
            "SESSION",
            "ACTIVE",
            json!({
                "id": session_id,
                "device": devices_csv,
                "profile": profile,
                "codec": codec,
                "sample_rate": sample_rate_hz
            }),
            &args,
        );
    }

    pub fn codec_active(&self, codec: &str) {
        self.emit("Codec", "ACTIVE", json!({ "codec": codec }), codec);
    }

    pub fn codec_endpoint_active(&self, ip: &str, codec: &str) {
        self.emit(
            "Codec",
            "ENDPOINT_ACTIVE",
            json!({ "ip": ip, "codec": codec }),
            &format!("{ip} {codec}"),
        );
    }

    pub fn codec_exact_endpoint_active(&self, endpoint_id: &str, ip: &str, codec: &str) {
        self.emit(
            "Codec",
            "EXACT_ENDPOINT_ACTIVE",
            json!({ "endpoint_id": endpoint_id, "ip": ip, "codec": codec }),
            &format!("{endpoint_id} {ip} {codec}"),
        );
    }

    pub fn codec_confirm_required(&self, ip: &str, requested: &str, suggested: &str, reason: &str) {
        self.emit(
            "Codec",
            "CONFIRM_REQUIRED",
            json!({
                "ip": ip,
                "requested": requested,
                "suggested": suggested,
                "reason": reason
            }),
            &format!("{ip} {requested} {suggested} {reason}"),
        );
    }

    #[allow(dead_code)]
    pub fn latency_override_applied(&self, mode: &str, ms: u32) {
        self.emit(
            "Latency",
            "OVERRIDE_APPLIED",
            json!({ "mode": mode, "ms": ms }),
            &format!("{mode} {ms}"),
        );
    }

    pub fn latency_rtx(&self, ip: &str, requests: u64, requested: u64, resent: u64) {
        self.emit(
            "Latency",
            "RTX",
            json!({
                "ip": ip,
                "requests": requests,
                "requested": requested,
                "resent": resent
            }),
            &format!("{ip} {requests} {requested} {resent}"),
        );
    }

    pub fn calibration_latency_applied(&self, ms: u32, mode: &str) {
        self.emit(
            "Calibration",
            "LATENCY_APPLIED",
            json!({ "ms": ms, "mode": mode }),
            &format!("{ms} {mode}"),
        );
    }

    pub fn calibration_tone(&self, state: &str) {
        self.emit("Calibration", "TONE", json!({ "state": state }), state);
    }
}
