use crate::codec::{AudioCodec, SampleRate};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::net::IpAddr;

/// 64-bit AirPlay feature bitmask parsed from TXT `ft` or `features` (`0xLOW,0xHIGH`).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct FeatureFlags(pub u64);

impl FeatureFlags {
    pub const AUDIO: u64 = 1 << 9;
    pub const AUDIO_REDUNDANT: u64 = 1 << 11;
    pub const AUTHENTICATION_FAIRPLAY: u64 = 1 << 14;
    pub const METADATA_TEXT: u64 = 1 << 15;
    pub const METADATA_ARTWORK: u64 = 1 << 16;
    pub const METADATA_PROGRESS: u64 = 1 << 17;
    pub const AUDIO_FORMAT_ALAC: u64 = 1 << 18;
    pub const AUDIO_FORMAT_PCM: u64 = 1 << 19;
    pub const HAS_UNIFIED_ADVERTISER: u64 = 1 << 30;
    pub const SUPPORTS_BUFFERED_AUDIO: u64 = 1 << 40;
    pub const SUPPORTS_PTP: u64 = 1 << 41;
    pub const SUPPORTS_UNIFIED_MEDIA_CONTROL: u64 = 1 << 38;
    pub const SUPPORTS_AIRPLAY2_MULTIROOM: u64 = 1 << 48;

    pub fn parse(raw: &str) -> Self {
        let mut parts = raw.split(',');
        let low = parts
            .next()
            .and_then(|s| u64::from_str_radix(s.trim().trim_start_matches("0x").trim_start_matches("0X"), 16).ok())
            .unwrap_or(0);
        let high = parts
            .next()
            .and_then(|s| u64::from_str_radix(s.trim().trim_start_matches("0x").trim_start_matches("0X"), 16).ok())
            .unwrap_or(0);
        Self((low & 0xFFFF_FFFF) | (high << 32))
    }

    pub const fn has(self, mask: u64) -> bool {
        (self.0 & mask) != 0
    }

    /// True if receiver advertises AirPlay 2 timing/buffered/multiroom bits (bits 40, 41, or 48).
    pub const fn supports_airplay2(self) -> bool {
        self.has(Self::SUPPORTS_PTP)
            || self.has(Self::SUPPORTS_BUFFERED_AUDIO)
            || self.has(Self::SUPPORTS_AIRPLAY2_MULTIROOM)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum EncryptionType {
    None = 0,
    RsaAes = 1,
    FairPlay = 3,
    MfiSap = 4,
    FairPlayV25 = 5,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProtocolProfile {
    NativePtpHomePod,
    NativePtpAppleTv,
    GroupHomePod,
    GroupAppleTv,
    SoloNtpPyatv,
    ClassicRaopSolo,
    ClassicRaopGroup,
}

impl ProtocolProfile {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::NativePtpHomePod => "NativePtpHomePod",
            Self::NativePtpAppleTv => "NativePtpAppleTv",
            Self::GroupHomePod => "GroupHomePod",
            Self::GroupAppleTv => "GroupAppleTv",
            Self::SoloNtpPyatv => "SoloNtpPyatv",
            Self::ClassicRaopSolo => "ClassicRaopSolo",
            Self::ClassicRaopGroup => "ClassicRaopGroup",
        }
    }

    pub const fn is_classic_raop(self) -> bool {
        matches!(self, Self::ClassicRaopSolo | Self::ClassicRaopGroup)
    }
}

/// Wire payload for `DEVICE_HINT <json>` sent by the frontend.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FrontendDeviceHint {
    #[serde(rename = "InstanceName", alias = "instance_name")]
    pub instance_name: String,
    #[serde(rename = "Addresses", alias = "addresses")]
    pub addresses: Vec<String>,
    #[serde(rename = "Port", alias = "port")]
    pub port: u16,
    #[serde(rename = "IsRaop", alias = "is_raop")]
    pub is_raop: bool,
    #[serde(rename = "TxtRecords", alias = "txt_records", default)]
    pub txt_records: HashMap<String, String>,
}

/// Wire payload for `ADD_HINTED <json>` / `ADD_GROUP_HINTED <json>` / `REMOVE_HINTED <json>`.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct FrontendEndpointSelection {
    #[serde(rename = "Ip", alias = "ip")]
    pub ip: String,
    #[serde(rename = "InstanceName", alias = "instance_name")]
    pub instance_name: String,
    #[serde(rename = "IsRaop", alias = "is_raop")]
    pub is_raop: bool,
}

impl FrontendEndpointSelection {
    pub fn event_id(&self) -> String {
        build_endpoint_event_id(&self.ip, &self.instance_name, self.is_raop)
    }
}

/// Canonical endpoint event ID matching `DeviceViewModel.BuildBackendEndpointEventId`:
/// `"R-" + hex(instance.to_lowercase()) + "-" + hex(ip)` (or `"A-"` for AirPlay).
pub fn build_endpoint_event_id(ip: &str, instance_name: &str, is_raop: bool) -> String {
    let prefix = if is_raop { "R" } else { "A" };
    let inst_hex = hex_upper(instance_name.trim().to_ascii_lowercase().as_bytes());
    let ip_hex = hex_upper(ip.trim().as_bytes());
    format!("{prefix}-{inst_hex}-{ip_hex}")
}

fn hex_upper(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut out = String::with_capacity(bytes.len() * 2);
    for &b in bytes {
        out.push(HEX[(b >> 4) as usize] as char);
        out.push(HEX[(b & 0x0F) as usize] as char);
    }
    out
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceDescriptor {
    pub id: String,
    pub name: String,
    pub instance_name: String,
    pub model: String,
    pub addresses: Vec<IpAddr>,
    pub airplay_port: u16,
    pub raop_port: Option<u16>,
    pub features: FeatureFlags,
    pub status_flags: u64,
    pub codecs: Vec<u8>,
    pub encryption_types: Vec<u8>,
    pub public_key: Option<String>,
    pub group_id: Option<String>,
    pub tight_sync_id: Option<String>,
    pub password_required: bool,
    pub txt: HashMap<String, String>,
}

impl DeviceDescriptor {
    pub fn from_frontend_hint(hint: &FrontendDeviceHint) -> Self {
        let txt = &hint.txt_records;
        let get = |k: &str| {
            txt.get(k)
                .or_else(|| txt.iter().find(|(tk, _)| tk.eq_ignore_ascii_case(k)).map(|(_, v)| v))
                .cloned()
        };
        let (mac_prefix, display_name) = match hint.instance_name.split_once('@') {
            Some((m, n)) => (m.to_string(), n.to_string()),
            None => (String::new(), hint.instance_name.clone()),
        };
        let id = get("deviceid").unwrap_or_else(|| {
            if mac_prefix.is_empty() {
                hint.instance_name.clone()
            } else {
                mac_prefix
            }
        });
        let model = get("model").or_else(|| get("am")).unwrap_or_default();
        let features = get("features")
            .or_else(|| get("ft"))
            .map(|s| FeatureFlags::parse(&s))
            .unwrap_or_default();
        let status_flags = get("flags")
            .or_else(|| get("sf"))
            .and_then(|s| u64::from_str_radix(s.trim().trim_start_matches("0x").trim_start_matches("0X"), 16).ok())
            .unwrap_or(0);
        let codecs = get("cn")
            .map(|s| s.split(',').filter_map(|p| p.trim().parse::<u8>().ok()).collect())
            .unwrap_or_else(|| vec![0, 1]);
        let encryption_types = get("et")
            .map(|s| s.split(',').filter_map(|p| p.trim().parse::<u8>().ok()).collect())
            .unwrap_or_else(|| vec![0, 1]);
        let addresses: Vec<IpAddr> = hint
            .addresses
            .iter()
            .filter_map(|a| a.trim().parse::<IpAddr>().ok())
            .collect();
        let password_required = get("pw")
            .map(|v| v.eq_ignore_ascii_case("true") || v == "1")
            .unwrap_or(false)
            || (status_flags & 0x80) != 0;

        Self {
            id,
            name: display_name,
            instance_name: hint.instance_name.clone(),
            model,
            addresses,
            airplay_port: hint.port,
            raop_port: if hint.is_raop { Some(hint.port) } else { None },
            features,
            status_flags,
            codecs,
            encryption_types,
            public_key: get("pk"),
            group_id: get("gid"),
            tight_sync_id: get("tsid"),
            password_required,
            txt: hint.txt_records.clone(),
        }
    }

    pub fn supports_airplay2(&self) -> bool {
        self.features.supports_airplay2()
    }

    pub fn supports_raop(&self) -> bool {
        self.raop_port.is_some()
    }

    pub fn is_homepod(&self) -> bool {
        self.model.starts_with("AudioAccessory")
    }

    pub fn is_apple_tv(&self) -> bool {
        self.model.starts_with("AppleTV")
    }

    pub fn advertises_codec(&self, codec: AudioCodec) -> bool {
        self.codecs.contains(&codec.raop_cn())
    }

    pub fn supports_sample_rate(&self, rate: SampleRate, use_raop: bool) -> bool {
        match rate {
            SampleRate::Hz44100 => true,
            SampleRate::Hz48000 => !use_raop && self.supports_airplay2(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_homepod_feature_flags_and_raop_stripped_alias() {
        let ft = FeatureFlags::parse("0x4A7FCA00,0x3C356BD0");
        assert!(ft.supports_airplay2());
        assert!(ft.has(FeatureFlags::SUPPORTS_PTP));

        let stripped = FeatureFlags::default();
        assert!(!stripped.supports_airplay2());
    }

    #[test]
    fn endpoint_event_id_matches_engine_format() {
        let id = build_endpoint_event_id("127.0.0.21", "FF9900000001@RaopAlias1", true);
        assert_eq!(
            id,
            "R-6666393930303030303030314072616F70616C69617331-3132372E302E302E3231"
        );
    }
}
