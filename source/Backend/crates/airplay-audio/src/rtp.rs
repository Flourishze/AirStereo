use airplay_core::Result;
use airplay_crypto::{
    aes::encrypt_raop_aes_cbc,
    chacha::{encrypt_aead, hap_nonce_from_8},
};

#[derive(Debug, Clone)]
pub enum RtpCipherMode {
    None,
    AirPlay2ChaCha20Poly1305 { key: [u8; 32] },
    RaopAes128Cbc { key: [u8; 16], iv: [u8; 16] },
}

pub struct RtpPacketBuilder {
    pub ssrc: u32,
    pub payload_type: u8,
    pub cipher: RtpCipherMode,
}

impl RtpPacketBuilder {
    pub fn build(
        &self,
        seq: u16,
        timestamp: u32,
        marker: bool,
        nonce_counter: u64,
        payload: &[u8],
    ) -> Result<Vec<u8>> {
        let mut header = [0u8; 12];
        header[0] = 0x80;
        header[1] = if marker { self.payload_type | 0x80 } else { self.payload_type };
        header[2..4].copy_from_slice(&seq.to_be_bytes());
        header[4..8].copy_from_slice(&timestamp.to_be_bytes());
        header[8..12].copy_from_slice(&self.ssrc.to_be_bytes());

        match &self.cipher {
            RtpCipherMode::None => {
                let mut pkt = Vec::with_capacity(12 + payload.len());
                pkt.extend_from_slice(&header);
                pkt.extend_from_slice(payload);
                Ok(pkt)
            }
            RtpCipherMode::RaopAes128Cbc { key, iv } => {
                let mut enc = payload.to_vec();
                encrypt_raop_aes_cbc(key, iv, &mut enc);
                let mut pkt = Vec::with_capacity(12 + enc.len());
                pkt.extend_from_slice(&header);
                pkt.extend_from_slice(&enc);
                Ok(pkt)
            }
            RtpCipherMode::AirPlay2ChaCha20Poly1305 { key } => {
                let nonce8 = nonce_counter.to_le_bytes();
                let nonce12 = hap_nonce_from_8(&nonce8);
                // AirPlay 2 RTP AEAD AAD = bytes 4..12 (RTP timestamp + SSRC)
                let aad = &header[4..12];
                let ct_and_tag = encrypt_aead(key, &nonce12, aad, payload)?;
                let mut pkt = Vec::with_capacity(12 + ct_and_tag.len() + 8);
                pkt.extend_from_slice(&header);
                pkt.extend_from_slice(&ct_and_tag);
                pkt.extend_from_slice(&nonce8);
                Ok(pkt)
            }
        }
    }
}

/// Fixed-size ring buffer storing recently sent RTP packets for AirPlay RTX retransmission requests.
pub struct RtxRingBuffer {
    slots: Vec<Option<(u16, Vec<u8>)>>,
    mask: usize,
}

impl RtxRingBuffer {
    pub fn new(power_of_two_size: usize) -> Self {
        let size = power_of_two_size.next_power_of_two().max(64);
        Self {
            slots: vec![None; size],
            mask: size - 1,
        }
    }

    pub fn insert(&mut self, seq: u16, packet: Vec<u8>) {
        self.slots[(seq as usize) & self.mask] = Some((seq, packet));
    }

    pub fn get(&self, seq: u16) -> Option<&[u8]> {
        self.slots[(seq as usize) & self.mask]
            .as_ref()
            .filter(|(s, _)| *s == seq)
            .map(|(_, p)| p.as_slice())
    }
}
