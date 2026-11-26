use plist::Value;
use std::io::Cursor;
use std::net::IpAddr;

/// Upstream receiver events parsed from HAP Event Channel (`events_channel.c`) or DACP (`dacp.c`).
#[derive(Debug, Clone)]
pub enum ReceiverEvent {
    Preempted(IpAddr),
    Volume(IpAddr, u8),
    RemoteCommand(IpAddr, &'static str),
}

/// Normalize AirPlay volume float (`0..1`, `1..100`, or `-30..0 dB`) into `0..=100`
/// (exact match to decompiled `events_channel.c 0x140057170`).
pub fn normalize_volume_percent(v: f32) -> u8 {
    if !v.is_finite() {
        return 0;
    }
    let pct = if v > 0.0 && v <= 1.0 {
        (v * 100.0).round()
    } else if v > 1.0 {
        v.min(100.0).round()
    } else if v <= -30.0 {
        0.0
    } else if v < 0.0 {
        ((v / 30.0 + 1.0) * 100.0).round()
    } else {
        // v == 0.0 dB => full volume (100%)
        100.0
    };
    pct.clamp(0.0, 100.0) as u8
}

/// Parse hardware volume from binary plist (`volume` or `params.volume`) or plain-text `volume: <f32>`.
pub fn parse_event_volume(body: &[u8]) -> Option<u8> {
    if body.starts_with(b"bplist00") {
        if let Ok(val) = Value::from_reader(Cursor::new(body)) {
            if let Some(dict) = val.as_dictionary() {
                if let Some(v) = dict.get("volume").and_then(plist_as_f32) {
                    return Some(normalize_volume_percent(v));
                }
                if let Some(params) = dict.get("params").and_then(|p| p.as_dictionary()) {
                    if let Some(v) = params.get("volume").and_then(plist_as_f32) {
                        return Some(normalize_volume_percent(v));
                    }
                }
            }
        }
    }
    let text = String::from_utf8_lossy(body);
    for line in text.lines() {
        let trimmed = line.trim();
        if let Some((k, v)) = trimmed.split_once(':') {
            if k.trim().eq_ignore_ascii_case("volume") {
                if let Ok(fv) = v.trim().parse::<f32>() {
                    return Some(normalize_volume_percent(fv));
                }
            }
        }
    }
    None
}

fn plist_as_f32(v: &Value) -> Option<f32> {
    if let Some(f) = v.as_real() {
        Some(f as f32)
    } else if let Some(i) = v.as_signed_integer() {
        Some(i as f32)
    } else {
        v.as_unsigned_integer().map(|u| u as f32)
    }
}

/// Map MediaRemote integer or string command name to canonical backend action
/// (exact match to decompiled `events_channel.c 0x140057ef0 / 0x140057b60`).
pub fn map_remote_command_int(code: i64) -> Option<&'static str> {
    match code {
        0 => Some("PLAY"),
        1 => Some("PAUSE"),
        2 => Some("TOGGLE"),
        3 => Some("STOP"),
        4 => Some("NEXT"),
        5 => Some("PREV"),
        _ => None,
    }
}

pub fn map_remote_command_str(s: &str) -> Option<&'static str> {
    match s.trim().to_ascii_lowercase().as_str() {
        "play" => Some("PLAY"),
        "paus" | "pause" => Some("PAUSE"),
        "togl" | "plps" | "toggle" | "playpause" | "toggleplaypause" => Some("TOGGLE"),
        "nitm" | "next" | "forward" | "nextitem" | "nexttrack" | "skipforward" => Some("NEXT"),
        "pitm" | "prev" | "rewind" | "previous" | "previtem" | "backward" | "prevtrack"
        | "skipbackward" | "previoustrack" => Some("PREV"),
        "stop" => Some("STOP"),
        _ => None,
    }
}

/// Parse remote control command from Event Channel request line + body (`bplist00` or text).
pub fn parse_event_remote_command(first_line: &str, body: &[u8]) -> Option<&'static str> {
    if body.starts_with(b"bplist00") {
        if let Ok(val) = Value::from_reader(Cursor::new(body)) {
            if let Some(root) = val.as_dictionary() {
                let dicts = [
                    Some(root),
                    root.get("params").and_then(|v| v.as_dictionary()),
                    root.get("options").and_then(|v| v.as_dictionary()),
                    root.get("arguments").and_then(|v| v.as_dictionary()),
                ];
                let keys = ["value", "command", "mrCommand", "cmd", "action", "name"];
                for d in dicts.into_iter().flatten() {
                    for k in keys {
                        if let Some(v) = d.get(k) {
                            if let Some(i) = v.as_signed_integer() {
                                if let Some(cmd) = map_remote_command_int(i) {
                                    return Some(cmd);
                                }
                            } else if let Some(u) = v.as_unsigned_integer() {
                                if let Some(cmd) = map_remote_command_int(u as i64) {
                                    return Some(cmd);
                                }
                            } else if let Some(s) = v.as_string() {
                                if let Some(cmd) = map_remote_command_str(s) {
                                    return Some(cmd);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    let text = String::from_utf8_lossy(body);
    for line in text.lines() {
        if let Some((k, v)) = line.split_once(':') {
            let kl = k.trim().to_ascii_lowercase();
            if matches!(kl.as_str(), "type" | "value" | "action" | "command") {
                if let Some(cmd) = map_remote_command_str(v) {
                    return Some(cmd);
                }
            }
        }
    }

    // Request line query fallback: ?command=<val>
    if let Some(uri) = first_line.split_whitespace().nth(1) {
        if let Some((_, query)) = uri.split_once('?') {
            for pair in query.split('&') {
                if let Some((k, v)) = pair.split_once('=') {
                    if k.eq_ignore_ascii_case("command") {
                        if let Some(cmd) = map_remote_command_str(v) {
                            return Some(cmd);
                        }
                    }
                }
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_event_volumes_matching_original() {
        assert_eq!(normalize_volume_percent(0.5), 50);
        assert_eq!(normalize_volume_percent(0.0), 100);
        assert_eq!(normalize_volume_percent(-15.0), 50);
        assert_eq!(normalize_volume_percent(-30.0), 0);
        assert_eq!(normalize_volume_percent(-144.0), 0);
        assert_eq!(normalize_volume_percent(75.0), 75);
    }

    #[test]
    fn maps_mediaremote_commands() {
        assert_eq!(map_remote_command_int(0), Some("PLAY"));
        assert_eq!(map_remote_command_int(2), Some("TOGGLE"));
        assert_eq!(map_remote_command_int(4), Some("NEXT"));
        assert_eq!(map_remote_command_str("plps"), Some("TOGGLE"));
        assert_eq!(map_remote_command_str("nextitem"), Some("NEXT"));
        assert_eq!(
            parse_event_remote_command("POST /command?command=pause RTSP/1.0", b""),
            Some("PAUSE")
        );
    }
}
