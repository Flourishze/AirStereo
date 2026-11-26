#![allow(dead_code)]
use airplay_core::{DeviceDescriptor, FrontendDeviceHint, FrontendEndpointSelection};
use std::collections::HashMap;
use std::net::IpAddr;

/// Caches discovered mDNS descriptors and frontend `DEVICE_HINT` snapshots.
/// Respects distinct RAOP-only aliases (`is_raop: true` without AirPlay 2 `ft` flags).
#[derive(Default)]
pub struct DeviceResolver {
    by_instance: HashMap<(bool, String), DeviceDescriptor>,
    by_ip: HashMap<IpAddr, DeviceDescriptor>,
}

impl DeviceResolver {
    pub fn seed_frontend_hint(&mut self, hint: &FrontendDeviceHint) {
        let desc = DeviceDescriptor::from_frontend_hint(hint);
        let key = (hint.is_raop, hint.instance_name.to_ascii_lowercase());
        self.by_instance.insert(key, desc.clone());
        for &ip in &desc.addresses {
            // Do not let a synthetic RAOP-only alias overwrite the primary IP lookup used for Auto/AirPlay 2.
            if !hint.instance_name.starts_with("A1") || !self.by_ip.contains_key(&ip) {
                self.by_ip.insert(ip, desc.clone());
            }
        }
    }

    pub fn upsert_mdns(&mut self, desc: DeviceDescriptor) {
        let is_raop = desc.raop_port.is_some() && !desc.supports_airplay2();
        self.by_instance
            .insert((is_raop, desc.instance_name.to_ascii_lowercase()), desc.clone());
        for &ip in &desc.addresses {
            self.by_ip.insert(ip, desc.clone());
        }
    }

    pub fn resolve_by_ip(&self, ip: IpAddr) -> Option<DeviceDescriptor> {
        self.by_ip.get(&ip).cloned()
    }

    pub fn resolve_hinted(&self, sel: &FrontendEndpointSelection) -> Option<DeviceDescriptor> {
        let key = (sel.is_raop, sel.instance_name.to_ascii_lowercase());
        if let Some(d) = self.by_instance.get(&key) {
            return Some(d.clone());
        }
        sel.ip
            .parse::<IpAddr>()
            .ok()
            .and_then(|ip| self.by_ip.get(&ip).cloned())
    }
}
