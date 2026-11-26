/// Compute HTTP/RTSP Digest Auth response header value (RFC 2617 MD5).
pub fn rtsp_digest_header(
    username: &str,
    realm: &str,
    password: &str,
    nonce: &str,
    method: &str,
    uri: &str,
) -> String {
    let ha1 = format!("{:x}", md5::compute(format!("{username}:{realm}:{password}")));
    let ha2 = format!("{:x}", md5::compute(format!("{method}:{uri}")));
    let response = format!("{:x}", md5::compute(format!("{ha1}:{nonce}:{ha2}")));
    format!(
        "Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\""
    )
}
