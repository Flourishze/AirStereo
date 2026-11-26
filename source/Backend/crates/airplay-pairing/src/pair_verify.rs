use crate::HapSessionKeys;
use airplay_core::{AirPlayError, Result};
use airplay_crypto::{
    chacha::{decrypt_aead, encrypt_aead, hap_nonce_from_8},
    hkdf_sha512_32,
    tlv::{tag, Tlv8},
};
use ed25519_dalek::{Signer, SigningKey};
use x25519_dalek::{PublicKey, StaticSecret};

/// HAP Pair-Verify client (M1..M4) establishing encrypted AirPlay 2 control & event channels.
pub struct PairVerifyClient {
    client_id: Vec<u8>,
    signing_key: SigningKey,
    ephemeral_secret: StaticSecret,
    ephemeral_public: PublicKey,
    shared_secret: Option<[u8; 32]>,
}

impl PairVerifyClient {
    pub fn new(client_id: Vec<u8>, signing_seed: [u8; 32]) -> Self {
        let mut rng = rand::thread_rng();
        let ephemeral_secret = StaticSecret::random_from_rng(&mut rng);
        let ephemeral_public = PublicKey::from(&ephemeral_secret);
        Self {
            client_id,
            signing_key: SigningKey::from_bytes(&signing_seed),
            ephemeral_secret,
            ephemeral_public,
            shared_secret: None,
        }
    }

    pub fn build_m1(&self) -> Vec<u8> {
        let mut tlv = Tlv8::new();
        tlv.push_u8(tag::STATE, 0x01)
            .push(tag::PUBLIC_KEY, self.ephemeral_public.as_bytes().to_vec());
        tlv.encode()
    }

    pub fn process_m2_build_m3(&mut self, m2_bytes: &[u8]) -> Result<Vec<u8>> {
        let tlv = Tlv8::decode(m2_bytes)?;
        if let Some(err) = tlv.get_u8(tag::ERROR) {
            return Err(AirPlayError::AuthFailed(format!(
                "Pair-Verify M2 error code 0x{err:02X}"
            )));
        }
        let server_pub_bytes: [u8; 32] = tlv
            .get(tag::PUBLIC_KEY)
            .and_then(|b| b.try_into().ok())
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Verify M2 missing 32-byte server public key".into()))?;
        let enc_data = tlv
            .get(tag::ENCRYPTED_DATA)
            .ok_or_else(|| AirPlayError::AuthFailed("Pair-Verify M2 missing encrypted data".into()))?;

        let server_pub = PublicKey::from(server_pub_bytes);
        let shared = self.ephemeral_secret.diffie_hellman(&server_pub).to_bytes();
        let session_key = hkdf_sha512_32(&shared, b"Pair-Verify-Encrypt-Salt", b"Pair-Verify-Encrypt-Info")?;

        // Decrypt & validate M2 sub-TLV
        let _server_plain = decrypt_aead(&session_key, &hap_nonce_from_8(b"PV-Msg02"), &[], enc_data)?;

        // Sign client ephemeral pub || client_id || server ephemeral pub
        let mut info = Vec::new();
        info.extend_from_slice(self.ephemeral_public.as_bytes());
        info.extend_from_slice(&self.client_id);
        info.extend_from_slice(&server_pub_bytes);
        let sig = self.signing_key.sign(&info);

        let mut sub = Tlv8::new();
        sub.push(tag::IDENTIFIER, self.client_id.clone())
            .push(tag::SIGNATURE, sig.to_bytes().to_vec());
        let ct = encrypt_aead(&session_key, &hap_nonce_from_8(b"PV-Msg03"), &[], &sub.encode())?;

        self.shared_secret = Some(shared);
        let mut out = Tlv8::new();
        out.push_u8(tag::STATE, 0x03).push(tag::ENCRYPTED_DATA, ct);
        Ok(out.encode())
    }

    pub fn process_m4(&self, m4_bytes: &[u8]) -> Result<HapSessionKeys> {
        let tlv = Tlv8::decode(m4_bytes)?;
        if let Some(err) = tlv.get_u8(tag::ERROR) {
            return Err(AirPlayError::AuthFailed(format!(
                "Pair-Verify M4 error code 0x{err:02X}"
            )));
        }
        let shared = self
            .shared_secret
            .ok_or_else(|| AirPlayError::Crypto("Pair-Verify shared secret not computed".into()))?;
        HapSessionKeys::derive_control_keys(&shared)
    }
}
