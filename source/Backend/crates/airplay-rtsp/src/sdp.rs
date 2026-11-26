use airplay_core::{AudioCodec, AudioFormat};
use base64::Engine;

/// Build Classic RAOP ANNOUNCE SDP body supporting both ALAC and L16 PCM at configured sample rate.
pub fn build_raop_announce_sdp(
    session_id: u32,
    local_ip: &str,
    remote_ip: &str,
    format: AudioFormat,
    rsa_aes_key_enc: Option<&[u8]>,
    aes_iv: Option<&[u8; 16]>,
) -> String {
    let sr = format.sample_rate.hz();
    let spf = format.frames_per_packet;
    let ch = format.channels;
    let mut sdp = format!(
        "v=0\r\n\
         o=iTunes {session_id} 0 IN IP4 {local_ip}\r\n\
         s=AirStereo2\r\n\
         c=IN IP4 {remote_ip}\r\n\
         t=0 0\r\n\
         m=audio 0 RTP/AVP 96\r\n"
    );
    match format.codec {
        AudioCodec::Alac => {
            sdp.push_str(&format!(
                "a=rtpmap:96 AppleLossless\r\n\
                 a=fmtp:96 {spf} 0 16 40 10 14 {ch} 255 0 0 {sr}\r\n"
            ));
        }
        AudioCodec::Pcm => {
            sdp.push_str(&format!(
                "a=rtpmap:96 L16/{sr}/{ch}\r\n"
            ));
        }
    }
    if let (Some(key), Some(iv)) = (rsa_aes_key_enc, aes_iv) {
        let b64 = base64::engine::general_purpose::STANDARD_NO_PAD;
        sdp.push_str(&format!("a=rsaaeskey:{}\r\n", b64.encode(key)));
        sdp.push_str(&format!("a=aesiv:{}\r\n", b64.encode(iv)));
    }
    sdp
}
