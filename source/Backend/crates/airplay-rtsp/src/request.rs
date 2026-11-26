use std::collections::BTreeMap;

#[derive(Debug, Clone)]
pub struct RtspRequest {
    pub method: String,
    pub uri: String,
    pub headers: BTreeMap<String, String>,
    pub body: Vec<u8>,
}

impl RtspRequest {
    pub fn new(method: impl Into<String>, uri: impl Into<String>) -> Self {
        Self {
            method: method.into(),
            uri: uri.into(),
            headers: BTreeMap::new(),
            body: Vec::new(),
        }
    }

    pub fn header(mut self, key: impl Into<String>, val: impl Into<String>) -> Self {
        self.headers.insert(key.into(), val.into());
        self
    }

    pub fn body(mut self, content_type: impl Into<String>, body: Vec<u8>) -> Self {
        self.headers.insert("Content-Type".into(), content_type.into());
        self.headers
            .insert("Content-Length".into(), body.len().to_string());
        self.body = body;
        self
    }

    pub fn encode(&self) -> Vec<u8> {
        let mut out = format!("{} {} RTSP/1.0\r\n", self.method, self.uri).into_bytes();
        for (k, v) in &self.headers {
            out.extend_from_slice(format!("{k}: {v}\r\n").as_bytes());
        }
        out.extend_from_slice(b"\r\n");
        out.extend_from_slice(&self.body);
        out
    }
}
