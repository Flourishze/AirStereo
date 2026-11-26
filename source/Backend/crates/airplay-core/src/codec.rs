use serde::{Deserialize, Serialize};
use std::fmt;

/// Supported AirPlay output sample rates.
/// Classic RAOP (AirPlay 1) always uses 44.1 kHz; AirPlay 2 realtime supports 44.1 kHz
/// universally and 48 kHz on receivers that advertise 48 kHz capability.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u32)]
pub enum SampleRate {
    Hz44100 = 44_100,
    Hz48000 = 48_000,
}

impl Default for SampleRate {
    fn default() -> Self {
        Self::Hz44100
    }
}

impl SampleRate {
    pub const fn hz(self) -> u32 {
        self as u32
    }

    pub const fn frames_per_packet(self) -> u32 {
        352
    }

    pub fn from_hz(hz: u32) -> Option<Self> {
        match hz {
            44_100 => Some(Self::Hz44100),
            48_000 => Some(Self::Hz48000),
            _ => None,
        }
    }

    pub fn ms_to_samples(self, ms: u64) -> u64 {
        (ms * self.hz() as u64 + 500) / 1000
    }

    pub fn samples_to_ms(self, samples: u64) -> u64 {
        (samples * 1000 + (self.hz() as u64 / 2)) / self.hz() as u64
    }
}

impl fmt::Display for SampleRate {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Hz44100 => write!(f, "44100"),
            Self::Hz48000 => write!(f, "48000"),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum AudioCodec {
    Alac,
    Pcm,
}

impl Default for AudioCodec {
    fn default() -> Self {
        Self::Alac
    }
}

impl AudioCodec {
    pub fn from_str_loose(s: &str) -> Option<Self> {
        match s.trim().to_ascii_uppercase().as_str() {
            "ALAC" => Some(Self::Alac),
            "PCM" | "L16" => Some(Self::Pcm),
            _ => None,
        }
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Alac => "ALAC",
            Self::Pcm => "PCM",
        }
    }

    /// AirPlay 2 SETUP compression type (`ct`): 1 = PCM, 2 = ALAC.
    pub const fn airplay2_ct(self) -> u32 {
        match self {
            Self::Pcm => 1,
            Self::Alac => 2,
        }
    }

    /// Classic RAOP TXT `cn` code: 0 = PCM, 1 = ALAC.
    pub const fn raop_cn(self) -> u8 {
        match self {
            Self::Pcm => 0,
            Self::Alac => 1,
        }
    }

    /// AirPlay 2 `audioFormat` bitmask for 16-bit stereo realtime streaming.
    pub const fn airplay2_audio_format(self, sample_rate: SampleRate) -> u64 {
        match (self, sample_rate) {
            (Self::Pcm, SampleRate::Hz44100) => 0x0000_0800,
            (Self::Pcm, SampleRate::Hz48000) => 0x0000_8000,
            (Self::Alac, SampleRate::Hz44100) => 0x0004_0000,
            (Self::Alac, SampleRate::Hz48000) => 0x0010_0000,
        }
    }
}

impl fmt::Display for AudioCodec {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct AudioFormat {
    pub codec: AudioCodec,
    pub sample_rate: SampleRate,
    pub channels: u16,
    pub bit_depth: u16,
    pub frames_per_packet: u32,
}

impl AudioFormat {
    pub const fn new(codec: AudioCodec, sample_rate: SampleRate) -> Self {
        Self {
            codec,
            sample_rate,
            channels: 2,
            bit_depth: 16,
            frames_per_packet: sample_rate.frames_per_packet(),
        }
    }

    pub const fn bytes_per_frame(self) -> usize {
        (self.channels as usize) * (self.bit_depth as usize / 8)
    }

    pub const fn pcm_bytes_per_packet(self) -> usize {
        (self.frames_per_packet as usize) * self.bytes_per_frame()
    }
}

impl Default for AudioFormat {
    fn default() -> Self {
        Self::new(AudioCodec::Alac, SampleRate::Hz44100)
    }
}
