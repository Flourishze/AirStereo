pub mod aes;
pub mod chacha;
pub mod digest;
pub mod srp;
pub mod tlv;

use airplay_core::{AirPlayError, Result};
use hkdf::Hkdf;
use sha2::Sha512;

/// HKDF-SHA512 key derivation used throughout HAP Pair-Setup, Pair-Verify, and AirPlay 2 control/event channels.
pub fn hkdf_sha512(ikm: &[u8], salt: &[u8], info: &[u8], out_len: usize) -> Result<Vec<u8>> {
    let hk = Hkdf::<Sha512>::new(Some(salt), ikm);
    let mut okm = vec![0u8; out_len];
    hk.expand(info, &mut okm)
        .map_err(|e| AirPlayError::Crypto(format!("HKDF-SHA512 expand failed: {e}")))?;
    Ok(okm)
}

pub fn hkdf_sha512_32(ikm: &[u8], salt: &[u8], info: &[u8]) -> Result<[u8; 32]> {
    let v = hkdf_sha512(ikm, salt, info, 32)?;
    let mut arr = [0u8; 32];
    arr.copy_from_slice(&v);
    Ok(arr)
}
