use thiserror::Error;

pub type Result<T> = std::result::Result<T, AirPlayError>;

#[derive(Debug, Error)]
pub enum AirPlayError {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),

    #[error("Invalid parameter: {0}")]
    InvalidInput(String),

    #[error("Authentication required: {0}")]
    AuthRequired(String),

    #[error("Authentication failed: {0}")]
    AuthFailed(String),

    #[error("RTSP error (status {status}): {message}")]
    Rtsp { status: u16, message: String },

    #[error("Codec unsupported: {0}")]
    CodecUnsupported(String),

    #[error("{0}")]
    Unsupported(String),

    #[error("Cryptography error: {0}")]
    Crypto(String),

    #[error("Timing sync error: {0}")]
    Timing(String),

    #[error("Audio pipeline error: {0}")]
    Audio(String),

    #[error("Device resolution error: {0}")]
    Discovery(String),

    #[error("Operation timed out: {0}")]
    Timeout(String),

    #[error("Disconnected: {0}")]
    Disconnected(String),
}
