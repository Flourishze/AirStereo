use crate::parser::parse_service_info;
use airplay_core::{AirPlayError, DeviceDescriptor, Result};
use mdns_sd::{ServiceDaemon, ServiceEvent};
use std::time::Duration;
use tokio::sync::mpsc;

pub const AIRPLAY_SERVICE: &str = "_airplay._tcp.local.";
pub const RAOP_SERVICE: &str = "_raop._tcp.local.";

pub struct MdnsBrowser {
    daemon: ServiceDaemon,
}

impl MdnsBrowser {
    pub fn new() -> Result<Self> {
        let daemon = ServiceDaemon::new()
            .map_err(|e| AirPlayError::Discovery(format!("mDNS daemon start failed: {e}")))?;
        Ok(Self { daemon })
    }

    pub fn start_browsing(&self) -> Result<mpsc::UnboundedReceiver<DeviceDescriptor>> {
        let (tx, rx) = mpsc::unbounded_channel();
        for &(svc, is_raop) in &[(AIRPLAY_SERVICE, false), (RAOP_SERVICE, true)] {
            let receiver = self
                .daemon
                .browse(svc)
                .map_err(|e| AirPlayError::Discovery(format!("mDNS browse {svc} failed: {e}")))?;
            let tx_clone = tx.clone();
            std::thread::spawn(move || {
                while let Ok(event) = receiver.recv() {
                    if let ServiceEvent::ServiceResolved(info) = event {
                        let desc = parse_service_info(&info, is_raop);
                        if !desc.addresses.is_empty() && tx_clone.send(desc).is_err() {
                            break;
                        }
                    }
                }
            });
        }
        Ok(rx)
    }

    pub async fn prescan(&self, duration: Duration) -> Result<Vec<DeviceDescriptor>> {
        let mut rx = self.start_browsing()?;
        let mut out = Vec::new();
        let deadline = tokio::time::Instant::now() + duration;
        while let Ok(Some(dev)) = tokio::time::timeout_at(deadline, rx.recv()).await {
            out.push(dev);
        }
        Ok(out)
    }
}
