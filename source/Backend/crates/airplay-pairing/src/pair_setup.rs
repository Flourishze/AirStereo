use crate::HapSessionKeys;
use airplay_core::{AirPlayError, Result};
use airplay_crypto::{
    chacha::{decrypt_aead, encrypt_aead, hap_nonce_from_8},
    hkdf_sha512_32,
    srp::SrpClient,
    tlv::{tag, Tlv8},
};
use ed25519_dalek::{Signer, SigningKey};

/// HAP Pair-Setup client supporting both AirPlay 2 Transient mode (`flags=0x10`, PIN `"3939"`)
/// and interactive PIN pairing (M1..M6).
pub struct PairSetupClient {
    srp: SrpClient,
    transient: bool,
    pin: String,
    client_id: Vec<u8>,
    signing_key: SigningKey,
}

impl PairSetupClient {
    pub fn new_transient() -> Result<Self> {
        Self::new_with_pin("3939", true, b"AirStereo2-Client".to_vec())
    }

    pub fn new_with_pin(pin: &str, transient: bool, client_id: Vec<u8>) -> Result<Self> {
        let mut rng = rand::thread_rng();
        let signing_key = SigningKey::generate(&mut rng);
        Ok(Self {
            srp: SrpClient::new()?,
            transient,
            pin: pin.to_string(),
            client_id,
            signing_key,
        })
    }

    /// Build M1 request body (TLV8).
    pub fn build_m1(&self) -> Vec<u8> {
        let mut tlv = Tlv8::new();
        tlv.push_u8(tag::METHOD, 0x00).push_u8(tag::STATE, 0x01);
        if self.transient {
            // kPairingFlag_Transient = 0x10 (1-byte TLV value required by HomePod)
            tlv.push_u8(tag::FLAGS, 0x10);
        }
        tlv.encode()
    }

    /// Process M2 response and return M3 request body (TLV8).
    pub fn process_m2_build_m3(&mut self, m2_bytes: &[u8]) -> Result<Vec<u8>> {
        let tlv = Tlv8::decode(m2_bytes)?;
        if let Some(err) = tlv.get_u8(tag::ERROR) {
            return Err(AirPlayError::AuthFailed(format!(
                "Pair-Setup M2 error code 0x{err:02X}"
            )));
        }
        let salt = tlv
            .get(tag::SALT)
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Setup M2 missing salt".into()))?;
        let pub_b = tlv
            .get(tag::PUBLIC_KEY)
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Setup M2 missing public key".into()))?;

        let m1_proof = self
            .srp
            .process_server_challenge(b"Pair-Setup", self.pin.as_bytes(), salt, pub_b)?;

        let mut out = Tlv8::new();
        out.push_u8(tag::STATE, 0x03)
            .push(tag::PUBLIC_KEY, self.srp.public_a.clone())
            .push(tag::PROOF, m1_proof);
        Ok(out.encode())
    }

    /// Process M4 response. In transient mode this finishes pairing and returns `HapSessionKeys`.
    pub fn process_m4_transient(&self, m4_bytes: &[u8]) -> Result<HapSessionKeys> {
        let tlv = Tlv8::decode(m4_bytes)?;
        if let Some(err) = tlv.get_u8(tag::ERROR) {
            return Err(AirPlayError::AuthFailed(format!(
                "Pair-Setup M4 error code 0x{err:02X}"
            )));
        }
        let proof = tlv
            .get(tag::PROOF)
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Setup M4 missing server proof".into()))?;
        self.srp.verify_server_proof(proof)?;
        HapSessionKeys::derive_control_keys(self.srp.session_key()?)
    }

    /// Build M5 (for non-transient PIN pairing) after verifying M4.
    pub fn build_m5(&self, m4_bytes: &[u8]) -> Result<Vec<u8>> {
        let _ = self.process_m4_transient(m4_bytes)?;
        let sk = self.srp.session_key()?;
        let ios_x = hkdf_sha512_32(sk, b"Pair-Setup-Controller-Sign-Salt", b"Pair-Setup-Controller-Sign-Info")?;
        let mut device_info = Vec::new();
        device_info.extend_from_slice(&ios_x);
        device_info.extend_from_slice(&self.client_id);
        device_info.extend_from_slice(self.signing_key.verifying_key().as_bytes());
        let sig = self.signing_key.sign(&device_info);

        let mut sub = Tlv8::new();
        sub.push(tag::IDENTIFIER, self.client_id.clone())
            .push(tag::PUBLIC_KEY, self.signing_key.verifying_key().as_bytes().to_vec())
            .push(tag::SIGNATURE, sig.to_bytes().to_vec());

        let enc_key = hkdf_sha512_32(sk, b"Pair-Setup-Encrypt-Salt", b"Pair-Setup-Encrypt-Info")?;
        let ct = encrypt_aead(&enc_key, &hap_nonce_from_8(b"PS-Msg05"), &[], &sub.encode())?;

        let mut out = Tlv8::new();
        out.push_u8(tag::STATE, 0x05).push(tag::ENCRYPTED_DATA, ct);
        Ok(out.encode())
    }

    /// Verify M6 and return the accessory's Ed25519 LTPK plus session keys.
    pub fn process_m6(&self, m6_bytes: &[u8]) -> Result<(Vec<u8>, HapSessionKeys)> {
        let tlv = Tlv8::decode(m6_bytes)?;
        if let Some(err) = tlv.get_u8(tag::ERROR) {
            return Err(AirPlayError::AuthFailed(format!(
                "Pair-Setup M6 error code 0x{err:02X}"
            )));
        }
        let enc = tlv
            .get(tag::ENCRYPTED_DATA)
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Setup M6 missing encrypted data".into()))?;
        let sk = self.srp.session_key()?;
        let enc_key = hkdf_sha512_32(sk, b"Pair-Setup-Encrypt-Salt", b"Pair-Setup-Encrypt-Info")?;
        let plain = decrypt_aead(&enc_key, &hap_nonce_from_8(b"PS-Msg06"), &[], enc)?;
        let sub = Tlv8::decode(&plain)?;
        let ltpk = sub
            .get(tag::PUBLIC_KEY)
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Setup M6 missing accessory LTPK".into()))?
            .to_vec();
        Ok((ltpk, HapSessionKeys::derive_control_keys(sk)?))
    }

    pub fn client_signing_seed(&self) -> [u8; 32] {
        self.signing_key.to_bytes()
    }
}
