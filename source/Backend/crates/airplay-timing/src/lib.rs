pub mod ntp;
pub mod ptp;

pub use ntp::{unix_to_ntp_timestamp, NtpTimingServer};
pub use ptp::{PtpClock, PtpMasterServer};
