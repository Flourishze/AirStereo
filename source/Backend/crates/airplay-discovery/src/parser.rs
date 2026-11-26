use airplay_core::{DeviceDescriptor, FrontendDeviceHint};
use mdns_sd::ServiceInfo;
use std::collections::HashMap;

pub fn parse_service_info(info: &ServiceInfo, is_raop: bool) -> DeviceDescriptor {
    let mut txt = HashMap::new();
    for prop in info.get_properties().iter() {
        txt.insert(prop.key().to_string(), prop.val_str().to_string());
    }
    let addresses: Vec<String> = info
        .get_addresses()
        .iter()
        .filter(|ip| ip.is_ipv4())
        .map(|ip| ip.to_string())
        .collect();
    let instance_name = info
        .get_fullname()
        .split("._")
        .next()
        .unwrap_or(info.get_fullname())
        .to_string();
    let hint = FrontendDeviceHint {
        instance_name,
        addresses,
        port: info.get_port(),
        is_raop,
        txt_records: txt,
    };
    DeviceDescriptor::from_frontend_hint(&hint)
}
