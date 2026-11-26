use airplay_core::{AirPlayError, Result};
use num_bigint::BigUint;
use rand::RngCore;
use sha2::{Digest, Sha512};

/// RFC 5054 3072-bit safe prime used by Apple HomeKit / AirPlay 2 SRP-6a Pair-Setup.
const SRP_3072_N_HEX: &str = concat!(
    "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74",
    "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437",
    "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED",
    "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05",
    "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB",
    "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B",
    "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718",
    "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33",
    "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7",
    "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864",
    "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2",
    "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF"
);

const SRP_G: u32 = 5;

pub struct SrpClient {
    n: BigUint,
    g: BigUint,
    a: BigUint,
    pub public_a: Vec<u8>,
    session_key: Option<Vec<u8>>,
    client_proof: Option<Vec<u8>>,
    server_proof: Option<Vec<u8>>,
}

impl SrpClient {
    pub fn new() -> Result<Self> {
        let n = BigUint::parse_bytes(SRP_3072_N_HEX.as_bytes(), 16)
            .ok_or_else(|| AirPlayError::Crypto("Invalid SRP N constant".into()))?;
        let g = BigUint::from(SRP_G);
        let mut a_bytes = [0u8; 32];
        rand::thread_rng().fill_bytes(&mut a_bytes);
        let a = BigUint::from_bytes_be(&a_bytes);
        let big_a = g.modpow(&a, &n);
        let public_a = big_a.to_bytes_be();
        Ok(Self {
            n,
            g,
            a,
            public_a,
            session_key: None,
            client_proof: None,
            server_proof: None,
        })
    }

    pub fn process_server_challenge(
        &mut self,
        username: &[u8],
        password: &[u8],
        salt: &[u8],
        server_public_b: &[u8],
    ) -> Result<Vec<u8>> {
        let b_big = BigUint::from_bytes_be(server_public_b);
        if (&b_big % &self.n) == BigUint::from(0u8) {
            return Err(AirPlayError::AuthFailed("SRP server public key B mod N == 0".into()));
        }
        let a_big = BigUint::from_bytes_be(&self.public_a);
        let a_padded = pad_be(&a_big, 384);
        let b_padded = pad_be(&b_big, 384);
        let n_bytes = self.n.to_bytes_be();
        let g_bytes = self.g.to_bytes_be();
        let k = {
            let mut h = Sha512::new();
            h.update(&n_bytes);
            h.update(pad_be(&self.g, 384));
            BigUint::from_bytes_be(&h.finalize())
        };
        let u = {
            let mut h = Sha512::new();
            h.update(&a_padded);
            h.update(&b_padded);
            BigUint::from_bytes_be(&h.finalize())
        };
        if u == BigUint::from(0u8) {
            return Err(AirPlayError::AuthFailed("SRP u == 0".into()));
        }
        let inner_hash = {
            let mut h = Sha512::new();
            h.update(username);
            h.update(b":");
            h.update(password);
            h.finalize()
        };
        let x = {
            let mut h = Sha512::new();
            h.update(salt);
            h.update(inner_hash);
            BigUint::from_bytes_be(&h.finalize())
        };
        let gx = self.g.modpow(&x, &self.n);
        let kgx = (&k * &gx) % &self.n;
        let base = if b_big >= kgx {
            &b_big - &kgx
        } else {
            &self.n + &b_big - &kgx
        };
        let exp = &self.a + (&u * &x);
        let s = base.modpow(&exp, &self.n);
        let k_session = Sha512::digest(s.to_bytes_be()).to_vec();

        // Apple HAP SRP-6a uses UNPADDED g ([0x05]) inside H(N) ^ H(g) and unpadded A/B in M1/M2.
        let hn = Sha512::digest(&n_bytes);
        let hg = Sha512::digest(&g_bytes);
        let mut h_xor = [0u8; 64];
        for i in 0..64 {
            h_xor[i] = hn[i] ^ hg[i];
        }
        let h_user = Sha512::digest(username);
        let m1 = {
            let mut h = Sha512::new();
            h.update(h_xor);
            h.update(h_user);
            h.update(salt);
            h.update(&self.public_a);
            h.update(server_public_b);
            h.update(&k_session);
            h.finalize().to_vec()
        };
        let m2 = {
            let mut h = Sha512::new();
            h.update(&self.public_a);
            h.update(&m1);
            h.update(&k_session);
            h.finalize().to_vec()
        };
        self.session_key = Some(k_session);
        self.client_proof = Some(m1.clone());
        self.server_proof = Some(m2);
        Ok(m1)
    }

    pub fn verify_server_proof(&self, proof: &[u8]) -> Result<()> {
        match &self.server_proof {
            Some(expected) if expected.as_slice() == proof => Ok(()),
            _ => Err(AirPlayError::AuthFailed("SRP server proof M2 mismatch".into())),
        }
    }

    pub fn session_key(&self) -> Result<&[u8]> {
        self.session_key
            .as_deref()
            .ok_or_else(|| AirPlayError::Crypto("SRP session key not derived yet".into()))
    }
}

fn pad_be(v: &BigUint, len: usize) -> Vec<u8> {
    let raw = v.to_bytes_be();
    if raw.len() >= len {
        return raw;
    }
    let mut out = vec![0u8; len];
    out[len - raw.len()..].copy_from_slice(&raw);
    out
}
