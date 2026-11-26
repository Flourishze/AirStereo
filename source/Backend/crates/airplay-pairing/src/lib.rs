pub mod pair_setup;
pub mod pair_verify;

use airplay_core::Result;
use airplay_crypto::{chacha::HapStreamCipher, hkdf_sha512_32};

/// Derived symmetric keys after HAP Pair-Setup (transient) or Pair-Verify completes.
#[derive(Debug, Clone)]
pub struct HapSessionKeys {
    pub shared_secret: Vec<u8>,
    pub write_key: [u8; 32],
    pub read_key: [u8; 32],
}

impl HapSessionKeys {
    pub fn derive_control_keys(shared_secret: &[u8]) -> Result<Self> {
        let write_key = hkdf_sha512_32(
            shared_secret,
            b"Control-Salt",
            b"Control-Write-Encryption-Key",
        )?;
        let read_key = hkdf_sha512_32(
            shared_secret,
            b"Control-Salt",
            b"Control-Read-Encryption-Key",
        )?;
        Ok(Self {
            shared_secret: shared_secret.to_vec(),
            write_key,
            read_key,
        })
    }

    pub fn derive_events_keys(&self) -> Result<([u8; 32], [u8; 32])> {
        let write_key = hkdf_sha512_32(
            &self.shared_secret,
            b"Events-Salt",
            b"Events-Write-Encryption-Key",
        )?;
        let read_key = hkdf_sha512_32(
            &self.shared_secret,
            b"Events-Salt",
            b"Events-Read-Encryption-Key",
        )?;
        Ok((write_key, read_key))
    }

    pub fn into_stream_cipher(&self) -> HapStreamCipher {
        HapStreamCipher::new(self.write_key, self.read_key)
    }
}
