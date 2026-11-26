use airplay_core::{AirPlayError, Result};

/// Standard HAP TLV8 tags used in AirPlay 2 Pair-Setup and Pair-Verify.
pub mod tag {
    pub const METHOD: u8 = 0x00;
    pub const IDENTIFIER: u8 = 0x01;
    pub const SALT: u8 = 0x02;
    pub const PUBLIC_KEY: u8 = 0x03;
    pub const PROOF: u8 = 0x04;
    pub const ENCRYPTED_DATA: u8 = 0x05;
    pub const STATE: u8 = 0x06;
    pub const ERROR: u8 = 0x07;
    pub const RETRY_DELAY: u8 = 0x08;
    pub const CERTIFICATE: u8 = 0x09;
    pub const SIGNATURE: u8 = 0x0A;
    pub const PERMISSIONS: u8 = 0x0B;
    pub const FRAGMENT_DATA: u8 = 0x0C;
    pub const FRAGMENT_LAST: u8 = 0x0D;
    pub const FLAGS: u8 = 0x13;
    pub const SEPARATOR: u8 = 0xFF;
}

#[derive(Debug, Default, Clone)]
pub struct Tlv8 {
    entries: Vec<(u8, Vec<u8>)>,
}

impl Tlv8 {
    pub fn new() -> Self {
        Self { entries: Vec::new() }
    }

    pub fn push(&mut self, tag: u8, value: impl Into<Vec<u8>>) -> &mut Self {
        self.entries.push((tag, value.into()));
        self
    }

    pub fn push_u8(&mut self, tag: u8, value: u8) -> &mut Self {
        self.entries.push((tag, vec![value]));
        self
    }

    pub fn get(&self, tag: u8) -> Option<&[u8]> {
        self.entries
            .iter()
            .find(|(t, _)| *t == tag)
            .map(|(_, v)| v.as_slice())
    }

    pub fn get_u8(&self, tag: u8) -> Option<u8> {
        self.get(tag).and_then(|v| v.first().copied())
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut out = Vec::new();
        for (tag, val) in &self.entries {
            if val.is_empty() {
                out.push(*tag);
                out.push(0);
                continue;
            }
            for chunk in val.chunks(255) {
                out.push(*tag);
                out.push(chunk.len() as u8);
                out.extend_from_slice(chunk);
            }
        }
        out
    }

    pub fn decode(mut data: &[u8]) -> Result<Self> {
        let mut entries: Vec<(u8, Vec<u8>)> = Vec::new();
        while !data.is_empty() {
            if data.len() < 2 {
                return Err(AirPlayError::Crypto("Truncated TLV8 header".into()));
            }
            let tag = data[0];
            let len = data[1] as usize;
            data = &data[2..];
            if data.len() < len {
                return Err(AirPlayError::Crypto("Truncated TLV8 value".into()));
            }
            let chunk = &data[..len];
            data = &data[len..];
            if let Some((last_tag, last_val)) = entries.last_mut() {
                if *last_tag == tag && tag != tag::SEPARATOR {
                    last_val.extend_from_slice(chunk);
                    continue;
                }
            }
            entries.push((tag, chunk.to_vec()));
        }
        Ok(Self { entries })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrips_large_tlv8_fragmented_value() {
        let big = vec![0xAB; 600];
        let mut tlv = Tlv8::new();
        tlv.push_u8(tag::STATE, 1).push(tag::PUBLIC_KEY, big.clone());
        let encoded = tlv.encode();
        let decoded = Tlv8::decode(&encoded).unwrap();
        assert_eq!(decoded.get_u8(tag::STATE), Some(1));
        assert_eq!(decoded.get(tag::PUBLIC_KEY), Some(big.as_slice()));
    }
}
