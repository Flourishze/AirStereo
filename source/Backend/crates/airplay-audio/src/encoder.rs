use airplay_core::{AudioCodec, AudioFormat, SampleRate};
use alac_encoder::{AlacEncoder, FormatDescription};

/// Standard 24-byte Apple ALACSpecificConfig (`asc` MagicCookie) required in AirPlay 2 SETUP Phase 2.
pub fn alac_magic_cookie(sample_rate: SampleRate) -> [u8; 24] {
    let mut c = [0u8; 24];
    c[0..4].copy_from_slice(&sample_rate.frames_per_packet().to_be_bytes());
    c[4] = 0; // compatibleVersion
    c[5] = 16; // bitDepth
    c[6] = 40; // pb
    c[7] = 10; // mb
    c[8] = 14; // kb
    c[9] = 2; // numChannels
    c[10..12].copy_from_slice(&255u16.to_be_bytes()); // maxRun
    c[12..16].copy_from_slice(&0u32.to_be_bytes()); // maxFrameBytes
    c[16..20].copy_from_slice(&0u32.to_be_bytes()); // avgBitRate
    c[20..24].copy_from_slice(&sample_rate.hz().to_be_bytes());
    c
}

/// Encodes 352-frame 16-bit stereo packets into either Apple Lossless (ALAC) or L16 big-endian PCM
/// at any configured sample rate (44.1 kHz or 48 kHz).
pub struct AudioPacketEncoder {
    format: AudioFormat,
    alac: Option<AlacEncoder>,
    alac_scratch: Vec<u8>,
}

impl AudioPacketEncoder {
    pub fn new(format: AudioFormat) -> Self {
        let alac = match format.codec {
            AudioCodec::Alac => {
                let desc = FormatDescription::alac(
                    format.sample_rate.hz() as f64,
                    format.frames_per_packet,
                    format.channels as u32,
                );
                Some(AlacEncoder::new(&desc))
            }
            AudioCodec::Pcm => None,
        };
        Self {
            format,
            alac,
            alac_scratch: vec![0u8; format.pcm_bytes_per_packet() + 256],
        }
    }

    pub fn format(&self) -> AudioFormat {
        self.format
    }

    /// Encode interleaved 16-bit signed PCM samples (`352 * 2 = 704` `i16` values) into wire payload bytes.
    pub fn encode_packet(&mut self, interleaved_i16: &[i16]) -> Vec<u8> {
        match self.format.codec {
            AudioCodec::Pcm => {
                // AirPlay L16 PCM is big-endian signed 16-bit interleaved stereo.
                let mut out = Vec::with_capacity(interleaved_i16.len() * 2);
                for &s in interleaved_i16 {
                    out.extend_from_slice(&s.to_be_bytes());
                }
                out
            }
            AudioCodec::Alac => {
                // alac-encoder expects native-endian PCM i16 byte slice.
                let mut pcm_bytes = Vec::with_capacity(interleaved_i16.len() * 2);
                for &s in interleaved_i16 {
                    pcm_bytes.extend_from_slice(&s.to_ne_bytes());
                }
                let in_desc = FormatDescription::pcm::<i16>(
                    self.format.sample_rate.hz() as f64,
                    self.format.channels as u32,
                );
                let enc = self.alac.as_mut().unwrap();
                let n = enc.encode(&in_desc, &pcm_bytes, &mut self.alac_scratch);
                self.alac_scratch[..n].to_vec()
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use airplay_core::SampleRate;

    #[test]
    fn encodes_alac_44100_and_48000() {
        for sr in [SampleRate::Hz44100, SampleRate::Hz48000] {
            let fmt = AudioFormat::new(AudioCodec::Alac, sr);
            let mut enc = AudioPacketEncoder::new(fmt);
            let samples: Vec<i16> = (0..704).map(|i| ((i * 37) % 10000) as i16).collect();
            let pkt = enc.encode_packet(&samples);
            assert!(!pkt.is_empty());
            assert!(pkt.len() < 1408);
        }
    }

    #[test]
    fn encodes_pcm_big_endian() {
        let fmt = AudioFormat::new(AudioCodec::Pcm, SampleRate::Hz48000);
        let mut enc = AudioPacketEncoder::new(fmt);
        let samples = vec![0x1234i16; 704];
        let pkt = enc.encode_packet(&samples);
        assert_eq!(pkt.len(), 1408);
        assert_eq!(&pkt[0..2], &[0x12, 0x34]);
    }
}
