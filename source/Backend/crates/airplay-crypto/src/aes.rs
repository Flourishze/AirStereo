use aes::cipher::{BlockEncryptMut, KeyIvInit, StreamCipher};
use aes::Aes128;

type Aes128CbcEnc = cbc::Encryptor<Aes128>;
type Aes128Ctr = ctr::Ctr128BE<Aes128>;

/// Classic RAOP audio payload encryption: AES-128-CBC over full 16-byte blocks, leaving trailing <16 bytes unencrypted.
pub fn encrypt_raop_aes_cbc(key: &[u8; 16], iv: &[u8; 16], data: &mut [u8]) {
    let full_len = (data.len() / 16) * 16;
    if full_len == 0 {
        return;
    }
    let mut enc = Aes128CbcEnc::new(key.into(), iv.into());
    for block in data[..full_len].chunks_exact_mut(16) {
        enc.encrypt_block_mut(block.into());
    }
}

/// AES-128-CTR stream cipher used for FairPlay/AirPlay control payloads.
pub fn apply_aes_128_ctr(key: &[u8; 16], iv: &[u8; 16], data: &mut [u8]) {
    let mut cipher = Aes128Ctr::new(key.into(), iv.into());
    cipher.apply_keystream(data);
}
