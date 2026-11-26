use airplay_core::{AirPlayError, Result};
use chacha20poly1305::{
    aead::{Aead, KeyInit, Payload},
    ChaCha20Poly1305, Nonce,
};

/// Build a 12-byte HAP nonce: 4 zero prefix bytes + 8-byte payload (e.g. little-endian counter or ASCII label).
pub fn hap_nonce_from_8(nonce8: &[u8; 8]) -> [u8; 12] {
    let mut n = [0u8; 12];
    n[4..12].copy_from_slice(nonce8);
    n
}

pub fn hap_nonce_from_u64(counter: u64) -> [u8; 12] {
    hap_nonce_from_8(&counter.to_le_bytes())
}

pub fn encrypt_aead(key: &[u8; 32], nonce12: &[u8; 12], aad: &[u8], plaintext: &[u8]) -> Result<Vec<u8>> {
    let cipher = ChaCha20Poly1305::new(key.into());
    cipher
        .encrypt(Nonce::from_slice(nonce12), Payload { msg: plaintext, aad })
        .map_err(|e| AirPlayError::Crypto(format!("ChaCha20-Poly1305 encrypt failed: {e}")))
}

pub fn decrypt_aead(key: &[u8; 32], nonce12: &[u8; 12], aad: &[u8], ciphertext_and_tag: &[u8]) -> Result<Vec<u8>> {
    let cipher = ChaCha20Poly1305::new(key.into());
    cipher
        .decrypt(
            Nonce::from_slice(nonce12),
            Payload {
                msg: ciphertext_and_tag,
                aad,
            },
        )
        .map_err(|e| AirPlayError::Crypto(format!("ChaCha20-Poly1305 decrypt failed: {e}")))
}

/// HAP encrypted TCP framing (2-byte LE plaintext length as AAD, 1024-byte max chunks).
pub struct HapStreamCipher {
    write_key: [u8; 32],
    read_key: [u8; 32],
    write_nonce: u64,
    read_nonce: u64,
}

impl HapStreamCipher {
    pub fn new(write_key: [u8; 32], read_key: [u8; 32]) -> Self {
        Self {
            write_key,
            read_key,
            write_nonce: 0,
            read_nonce: 0,
        }
    }

    pub fn encrypt_frame(&mut self, plaintext: &[u8]) -> Result<Vec<u8>> {
        let mut out = Vec::with_capacity(plaintext.len() + 32);
        for chunk in plaintext.chunks(1024) {
            let aad = (chunk.len() as u16).to_le_bytes();
            let nonce = hap_nonce_from_u64(self.write_nonce);
            self.write_nonce = self.write_nonce.wrapping_add(1);
            let ct = encrypt_aead(&self.write_key, &nonce, &aad, chunk)?;
            out.extend_from_slice(&aad);
            out.extend_from_slice(&ct);
        }
        Ok(out)
    }

    pub fn decrypt_chunk(&mut self, len_header: [u8; 2], ciphertext_and_tag: &[u8]) -> Result<Vec<u8>> {
        let nonce = hap_nonce_from_u64(self.read_nonce);
        self.read_nonce = self.read_nonce.wrapping_add(1);
        decrypt_aead(&self.read_key, &nonce, &len_header, ciphertext_and_tag)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hap_stream_cipher_roundtrip() {
        let k1 = [0x11u8; 32];
        let k2 = [0x22u8; 32];
        let mut tx = HapStreamCipher::new(k1, k2);
        let mut rx = HapStreamCipher::new(k2, k1);
        let msg = b"RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n";
        let wire = tx.encrypt_frame(msg).unwrap();
        let len_hdr = [wire[0], wire[1]];
        let pt = rx.decrypt_chunk(len_hdr, &wire[2..]).unwrap();
        assert_eq!(pt, msg);
    }
}
