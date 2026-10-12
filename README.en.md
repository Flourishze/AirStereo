# AirStereo

[简体中文](README.md) · [Download 1.0.7](https://github.com/Flourishze/AirStereo/releases/tag/v1.0.7) · [Microsoft Store](https://apps.microsoft.com/detail/9NMX9H3GBGJ4)

**Stream the sound playing on your Windows PC to compatible AirPlay / AirPlay 2 speakers over your local network.** Current standalone release: **1.0.7**.

AirStereo captures the system mix from the default output device for music, video, games and everyday applications. Choose speakers from the tray panel without importing audio files. It does not use Bluetooth or capture individual applications separately.

## Features

| Feature | Behavior |
| --- | --- |
| Speaker checklist | Automatic discovery and manual refresh; selection determines playback mode |
| One independent speaker | Complete stereo output; balance is disabled |
| Two independent speakers | First selection is L, second is R, sharing an audio timeline and PTP clock; either device disconnecting stops the group |
| Native Apple stereo pair | A confirmed pair appears as one logical target; the receivers assign physical channels |
| Manual / live volume | Drag then click ✓ to apply, or enable live volume to apply while dragging |
| Ten-band equalizer | Vertical peaking EQ integrated into Settings, presets including Pop and Bass, bypass and L/R balance |
| Encoding and sample rate | Lossless ALAC or uncompressed PCM, 44.1 / 48 kHz; ALAC, 16-bit/44.1 kHz by default |
| Latency settings | Preset or custom buffering; latency, encoding and sample rate are locked during a connection |
| Automatic reconnection | Optional 1–10 attempt limit; independent stereo reconnects as a complete group |
| WinUI 3 Fluent interface | Compact tray panel, refresh animation, transparent tray icons, system/light/dark themes and high DPI layouts |
| Chinese / English | Instant language switching with a saved preference |
| Tray controls | Click the desktop to dismiss; Stop keeps the application running; Exit also closes Settings |
| Startup options | Windows startup, silent startup and reconnecting the previous speakers on launch |
| Local output mute | Optional PC mute while streaming, restoring the previous mute state when stopped |
| Diagnostics and faults | Separate Settings pages and an Open folder action; empty discovery is a status, while actual network/playback errors are recorded |
| Update checking | Standalone installations check this repository's stable Releases; Store installations use Microsoft Store |

## screenshot
<img width="1920" height="1080" alt="01-dark-overview" src="https://github.com/user-attachments/assets/2115c7ed-62ff-45c8-98de-2071ba3d29f0" />
<img width="1920" height="1080" alt="02-light-overview" src="https://github.com/user-attachments/assets/ddae357c-3753-4664-8e75-cc146096256b" />
<img width="1920" height="1080" alt="03-ten-band-eq" src="https://github.com/user-attachments/assets/37d642dd-cb5e-4c94-9089-fad8fa190765" />
<img width="1920" height="1080" alt="04-startup-reconnect" src="https://github.com/user-attachments/assets/e37c4d23-773b-47bb-aa69-20b1d0745712" />


## Installation

- **Standalone MSI**: [AirStereo-1.0.7-x64.msi](https://github.com/Flourishze/AirStereo/releases/download/v1.0.7/AirStereo-1.0.7-x64.msi). A Simplified Chinese wizard supports directory selection, upgrades from 1.0.6 and handling a running application.
- **Store edition**: [Microsoft Store](https://apps.microsoft.com/detail/9NMX9H3GBGJ4). Consult the Store page for its available version.
- **MSIX**: The Release asset is an unsigned Store submission package, not a normal double-click installer.
- **Source**: A ZIP of the release tag and `SHA256SUMS.txt` are provided. This release does not include an EXE installer.

Requires Windows 10 22H2 (build 19045) or later on x64, including Windows 11. Packages include .NET 10.0.12 and Windows App SDK; a separate .NET installation is unnecessary.

## Usage

1. Connect the PC and speakers to the same LAN and permit local discovery and traffic.
2. Launch AirStereo and click its taskbar tray icon. Wait for discovery or refresh.
3. Select one speaker or a native pair. For independent stereo, select L first and R second. Playback is disabled with no selection; at most two independent speakers are supported.
4. Click Play PC audio, then play content on the PC. Drag volume and click ✓, or enable live adjustment.
5. Use Settings for language, appearance, startup, reconnection and EQ. Stop the connection before changing encoding, sample rate or latency.

ALAC and PCM preserve the same samples at the same sample rate and bit depth. ALAC usually uses less bandwidth, with the ratio depending on content; PCM uses more bandwidth and remains available as a fallback. Support for 48 kHz depends on receiver negotiation; use the default 44.1 kHz first if connections are unstable. The buffering setting is not a measurement of end-to-end latency.

## Troubleshooting and feedback

If speakers are missing, check the subnet, Windows firewall, VPN/virtual adapters and router AP isolation. A Public Windows network profile may restrict discovery. Successful phone playback does not prove discovery traffic is allowed on the PC.

Report problems through [Issues](https://github.com/Flourishze/AirStereo/issues), including the app/Windows versions, speaker model, encoding/sample rate, steps and relevant diagnostics. Redact device names, IP addresses, device IDs and other private information; never upload credentials.

## Documentation and license

[1.0.7 release notes](docs/releases/v1.0.7.md) · [Changelog](CHANGELOG.md) · [Build](docs/BUILD.md) · [MSIX](docs/MSIX.md) · [Acceptance checklist](docs/TESTING.md)

Licensed under [MIT](LICENSE). Third-party components retain their own licenses and notices. AirStereo is an independent project and is not affiliated with Apple. AirPlay, HomePod and other trademarks belong to their respective owners.
