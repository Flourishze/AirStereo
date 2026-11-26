use airplay_core::{AudioCodec, FrontendDeviceHint, FrontendEndpointSelection, SampleRate};

#[derive(Debug, Clone)]
pub enum BackendCommand {
    IpcFormatJson3,
    DeviceHint(FrontendDeviceHint),
    Add(String),
    AddHinted(FrontendEndpointSelection),
    AddGroup(Vec<String>),
    AddGroupHinted(Vec<FrontendEndpointSelection>),
    Remove(String),
    RemoveHinted(FrontendEndpointSelection),
    RemoveAll,
    Volume(String, f32),
    VolumeHinted(FrontendEndpointSelection, f32),
    Codec(AudioCodec),
    CodecConfirm(String, bool),
    SampleRate(SampleRate),
    Latency(u32),
    ProfileLatency(Option<u32>),
    CalibrationLatency(Option<u32>),
    CalibrationTone(String),
    CalibrationSnapshot,
    CalibrationRttRate(f32),
    CalibrationSession(Option<u32>),
    SyncOffset(i32),
    SetStandby(bool, u32),
    EqEnable(bool),
    EqBand(usize, f32),
    EqPreamp(f32),
    EqReset,
    SetCaptureDefault,
    SetCaptureDisabled,
    SetCaptureDevice(String),
    AutoReceiverRecovery(bool),
    RecoverNow,
    CancelRecovery,
    NetworkCheck(Vec<String>),
    DiagSummary,
    Pin(String, String),
    Password(String, Option<String>),
    NowPlaying { title: String, artist: String, album: String },
    Quit,
    Unknown(String),
}

pub fn parse_command(raw: &str) -> BackendCommand {
    let line = raw.trim();
    if line.is_empty() {
        return BackendCommand::Unknown(String::new());
    }
    if line == "IPC_FORMAT JSON 3" {
        return BackendCommand::IpcFormatJson3;
    }
    if line == "REMOVE_ALL" {
        return BackendCommand::RemoveAll;
    }
    if line == "EQ_RESET" {
        return BackendCommand::EqReset;
    }
    if line == "SET_CAPTURE DEFAULT" {
        return BackendCommand::SetCaptureDefault;
    }
    if line == "SET_CAPTURE DISABLED" {
        return BackendCommand::SetCaptureDisabled;
    }
    if line == "DIAG_SUMMARY" {
        return BackendCommand::DiagSummary;
    }
    if line == "QUIT" {
        return BackendCommand::Quit;
    }

    if let Some(rest) = line.strip_prefix("DEVICE_HINT ") {
        if let Ok(h) = serde_json::from_str::<FrontendDeviceHint>(rest) {
            return BackendCommand::DeviceHint(h);
        }
    }
    if let Some(rest) = line.strip_prefix("ADD_HINTED ") {
        if let Ok(s) = serde_json::from_str::<FrontendEndpointSelection>(rest) {
            return BackendCommand::AddHinted(s);
        }
    }
    if let Some(rest) = line.strip_prefix("ADD_GROUP_HINTED ") {
        if let Ok(list) = serde_json::from_str::<Vec<FrontendEndpointSelection>>(rest) {
            return BackendCommand::AddGroupHinted(list);
        }
    }
    if let Some(rest) = line.strip_prefix("ADD_GROUP ") {
        if let Ok(ips) = serde_json::from_str::<Vec<String>>(rest) {
            return BackendCommand::AddGroup(ips);
        }
        let ips: Vec<String> = rest
            .split(|c: char| c == ',' || c.is_whitespace())
            .map(|s| s.trim().trim_matches('"').to_string())
            .filter(|s| !s.is_empty())
            .collect();
        return BackendCommand::AddGroup(ips);
    }
    if let Some(rest) = line.strip_prefix("ADD ") {
        return BackendCommand::Add(rest.trim().to_string());
    }
    if let Some(rest) = line.strip_prefix("REMOVE_HINTED ") {
        if let Ok(s) = serde_json::from_str::<FrontendEndpointSelection>(rest) {
            return BackendCommand::RemoveHinted(s);
        }
    }
    if let Some(rest) = line.strip_prefix("REMOVE ") {
        return BackendCommand::Remove(rest.trim().to_string());
    }
    if let Some(rest) = line.strip_prefix("VOL_HINTED ") {
        // Format: VOL_HINTED <volume> <json> or <json> <volume>
        if let Some(idx) = rest.find('{') {
            let before = rest[..idx].trim();
            if let Some(end) = rest.rfind('}') {
                let json_part = &rest[idx..=end];
                let after = rest[end + 1..].trim();
                let vol_str = if !before.is_empty() { before } else { after };
                if let (Ok(sel), Ok(vol)) = (
                    serde_json::from_str::<FrontendEndpointSelection>(json_part),
                    vol_str.parse::<f32>(),
                ) {
                    let norm = if vol > 1.0 { (vol / 100.0).clamp(0.0, 1.0) } else { vol.clamp(0.0, 1.0) };
                    return BackendCommand::VolumeHinted(sel, norm);
                }
            }
        }
    }
    if let Some(rest) = line.strip_prefix("CODEC_CONFIRM ") {
        let mut parts = rest.split_whitespace();
        if let (Some(ip), Some(dec)) = (parts.next(), parts.next()) {
            return BackendCommand::CodecConfirm(ip.to_string(), dec.eq_ignore_ascii_case("ACCEPT"));
        }
    }
    if let Some(rest) = line.strip_prefix("CODEC ") {
        if let Some(c) = AudioCodec::from_str_loose(rest) {
            return BackendCommand::Codec(c);
        }
    }
    if let Some(rest) = line.strip_prefix("CALIBRATION_TONE ") {
        return BackendCommand::CalibrationTone(rest.trim().to_ascii_uppercase());
    }
    if line == "CALIBRATION_SNAPSHOT" {
        return BackendCommand::CalibrationSnapshot;
    }
    if line == "RECOVER_NOW" {
        return BackendCommand::RecoverNow;
    }
    if line == "CANCEL_RECOVERY" {
        return BackendCommand::CancelRecovery;
    }
    if let Some(rest) = line.strip_prefix("CALIBRATION_RTT_RATE ") {
        if let Ok(hz) = rest.trim().parse::<f32>() {
            return BackendCommand::CalibrationRttRate(hz);
        }
    }
    if let Some(rest) = line.strip_prefix("CALIBRATION_SESSION ") {
        let t = rest.trim();
        return if t.eq_ignore_ascii_case("off") || t.eq_ignore_ascii_case("clear") || t == "0" {
            BackendCommand::CalibrationSession(None)
        } else {
            BackendCommand::CalibrationSession(t.parse::<u32>().ok())
        };
    }
    if let Some(rest) = line.strip_prefix("SYNC_OFFSET ").or_else(|| line.strip_prefix("VERNIER ")) {
        if let Ok(ms) = rest.trim().parse::<i32>() {
            return BackendCommand::SyncOffset(ms.clamp(-250, 250));
        }
    }
    if let Some(rest) = line.strip_prefix("SET_STANDBY ") {
        let mut parts = rest.split_whitespace();
        if let Some(en) = parts.next() {
            let mins = parts.next().and_then(|s| s.parse::<u32>().ok()).unwrap_or(120);
            return BackendCommand::SetStandby(en.eq_ignore_ascii_case("true"), mins);
        }
    }
    if let Some(rest) = line.strip_prefix("PASSWORD ") {
        let mut parts = rest.split_whitespace();
        if let Some(ip) = parts.next() {
            let pw = parts.next().map(|s| s.to_string());
            return BackendCommand::Password(ip.to_string(), pw);
        }
    }
    if let Some(rest) = line.strip_prefix("NOW_PLAYING ") {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(rest) {
            let title = v.get("title").and_then(|s| s.as_str()).unwrap_or("").to_string();
            let artist = v.get("artist").and_then(|s| s.as_str()).unwrap_or("").to_string();
            let album = v.get("album").and_then(|s| s.as_str()).unwrap_or("").to_string();
            return BackendCommand::NowPlaying { title, artist, album };
        }
        return BackendCommand::NowPlaying {
            title: rest.trim().to_string(),
            artist: String::new(),
            album: String::new(),
        };
    }
    if let Some(rest) = line.strip_prefix("SAMPLE_RATE ") {
        if let Ok(hz) = rest.trim().parse::<u32>() {
            if let Some(sr) = SampleRate::from_hz(hz) {
                return BackendCommand::SampleRate(sr);
            }
        }
    }
    if let Some(rest) = line.strip_prefix("LATENCY ") {
        if let Ok(ms) = rest.trim().parse::<u32>() {
            return BackendCommand::Latency(ms);
        }
    }
    if let Some(rest) = line.strip_prefix("PROFILE_LATENCY ") {
        let t = rest.trim();
        return if t.eq_ignore_ascii_case("auto") {
            BackendCommand::ProfileLatency(None)
        } else {
            BackendCommand::ProfileLatency(t.parse::<u32>().ok())
        };
    }
    if let Some(rest) = line.strip_prefix("CALIBRATION_LATENCY ") {
        let t = rest.split_whitespace().next().unwrap_or("");
        return if t.eq_ignore_ascii_case("auto") {
            BackendCommand::CalibrationLatency(None)
        } else {
            BackendCommand::CalibrationLatency(t.parse::<u32>().ok())
        };
    }
    if let Some(rest) = line.strip_prefix("EQ_ENABLE ") {
        return BackendCommand::EqEnable(rest.trim().eq_ignore_ascii_case("true"));
    }
    if let Some(rest) = line.strip_prefix("EQ_BAND ") {
        let mut parts = rest.split_whitespace();
        if let (Some(idx), Some(db)) = (parts.next(), parts.next()) {
            if let (Ok(i), Ok(g)) = (idx.parse::<usize>(), db.parse::<f32>()) {
                return BackendCommand::EqBand(i, g);
            }
        }
    }
    if let Some(rest) = line.strip_prefix("EQ_PREAMP ") {
        if let Ok(db) = rest.trim().parse::<f32>() {
            return BackendCommand::EqPreamp(db);
        }
    }
    if let Some(rest) = line.strip_prefix("SET_CAPTURE DEVICE ") {
        return BackendCommand::SetCaptureDevice(rest.trim().to_string());
    }
    if let Some(rest) = line.strip_prefix("AUTO_RECEIVER_RECOVERY ") {
        return BackendCommand::AutoReceiverRecovery(rest.trim().eq_ignore_ascii_case("true"));
    }
    if let Some(rest) = line.strip_prefix("NETWORK_CHECK") {
        return BackendCommand::NetworkCheck(rest.split_whitespace().map(|s| s.to_string()).collect());
    }
    if let Some(rest) = line.strip_prefix("VOL ") {
        let mut parts = rest.split_whitespace();
        if let (Some(ip), Some(v)) = (parts.next(), parts.next()) {
            if let Ok(vol) = v.parse::<f32>() {
                let norm = if vol > 1.0 { (vol / 100.0).clamp(0.0, 1.0) } else { vol.clamp(0.0, 1.0) };
                return BackendCommand::Volume(ip.to_string(), norm);
            }
        }
    }
    if let Some(rest) = line.strip_prefix("PIN ") {
        let mut parts = rest.split_whitespace();
        if let (Some(ip), Some(pin)) = (parts.next(), parts.next()) {
            return BackendCommand::Pin(ip.to_string(), pin.to_string());
        }
    }
    BackendCommand::Unknown(line.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_sample_rate_and_commands() {
        assert!(matches!(parse_command("SAMPLE_RATE 48000"), BackendCommand::SampleRate(SampleRate::Hz48000)));
        assert!(matches!(parse_command("SAMPLE_RATE 44100"), BackendCommand::SampleRate(SampleRate::Hz44100)));
        assert!(matches!(parse_command("IPC_FORMAT JSON 3"), BackendCommand::IpcFormatJson3));
        assert!(matches!(parse_command("LATENCY 250"), BackendCommand::Latency(250)));
    }
}
