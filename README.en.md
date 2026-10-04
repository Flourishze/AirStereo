# AirStereo

[中文](README.md)

<a href="https://apps.microsoft.com/detail/9nmx9h3gbgj4?referrer=appbadge&mode=full" target="_blank"  rel="noopener noreferrer">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

Windows x64 AirPlay audio sender. The current version is **1.0.4**. Source and standalone releases are published at [Flourishze/AirStereo](https://github.com/Flourishze/AirStereo). The Store edition is updated by Microsoft Store.

## What Is AirStereo?

**AirStereo sends the system audio playing on a Windows PC to compatible AirPlay / AirPlay 2 speakers over your local network.** Play music, videos, or other application audio on your computer, then choose your speakers from the system-tray interface. There is no need to import audio files into AirStereo.

It is an audio sender, not a music player, and it does not connect over Bluetooth. The PC and speakers must be on the same local network with device discovery and connections permitted. AirStereo captures the system mix from the default audio output device; it does not provide per-application audio selection or isolation.

### Use Cases

- **One speaker**: select one independent speaker to send full stereo audio, without configuring a two-device setup.
- **Two independent speakers**: selection order assigns L / R for left/right output.
- **An existing stereo pair in the Apple Home app**: when discovery information explicitly identifies the pair, connect to it as one logical target and let the receiver assign the physical channels.
- **Multiple available speakers**: choose the targets for the current session. This version allows up to two independent speakers, not arbitrary multi-room playback.

### Main Features

| Feature | Description |
| --- | --- |
| Wireless PC audio | Capture Windows system playback audio and send it to compatible speakers |
| Speaker checklist | Discover targets and choose single- or two-device playback automatically from the selection |
| Left/right output | Two independent speakers share a media timeline and PTP clock, with L / R assigned in selection order |
| Native stereo pairs | Explicitly identified pairs appear as one target, preventing duplicate selection of their members |
| Audio settings | Equalizer, volume, channel test tones, balance with a center reset, and latency settings |
| System-tray control | A compact tray interface with optional launch at startup |
| Troubleshooting | View connection faults and runtime logs, and open the local Diagnostics folder |
| Version checks | Manually check stable releases in this repository and open the Release page for updates |

[Download](https://github.com/Flourishze/AirStereo/releases) · [Report a Problem / Request a Feature](https://github.com/Flourishze/AirStereo/issues) · [Changelog](CHANGELOG.md)

## Interface Preview

![AirStereo speaker list and audio settings overview (Chinese interface)](docs/images/airstereo-overview-zh-cn.png)

Offline preview of the actual interface: speaker names and addresses are demonstration data, not evidence of a live device connection.

## Installation and Usage

Use EXE for the installation wizard or MSI for deployment. Install the Store-signed edition from Microsoft Store; the unsigned MSIX asset is for Store submission. Do not run both editions simultaneously.


Download `AirStereo-Setup-1.0.4-x64.exe` from the repository's Releases page. Exit an older version from the tray menu before installing. The installer includes the .NET Core and Windows Desktop 10.0.12 runtimes and prefers the runtimes in the installation directory. No separate .NET runtime installation is required; the operating system must still be a Windows x64 version supported by .NET 10.

1. Click the AirStereo tray icon in the lower-right corner, scan for speakers, and select the targets you want to use.
2. Playback is disabled when nothing is selected. Selecting one independent speaker sends full stereo audio and disables the left/right balance control.
3. Selecting two independent speakers in order assigns the first speaker to L and the second speaker to R. Both speakers share one media timeline and PTP clock. A maximum of two independent speakers can be selected. To swap L/R, clear the selection and select the speakers in the opposite order.
4. A native stereo pair identified by mDNS is shown as one logical target. The receiver handles the physical left/right assignment while AirStereo sends full stereo audio. Pair members cannot be selected separately, and a relationship inferred only from names is not treated as a confirmed native pair.
5. Click Play to send computer audio. If either speaker in a two-speaker session disconnects, the entire session stops and the affected device is reported instead of silently degrading to mono.

Actual compatibility depends on receiver firmware, access permissions, network conditions, and the service information exposed by the receiver. Not every device advertised as AirPlay-compatible is guaranteed to connect.

Wireless playback includes capture, network, and receiver-buffer delays. The configured latency is not a measurement of end-to-end latency. AirStereo does not promise zero latency and is not a replacement for low-latency gaming audio or real-time monitoring equipment.

## What's New in 1.0.4

- Bounded capture/resampling buffers and discarded-log undo cleanup improve memory stability during extended use.
- Restored click-outside dismissal of the tray panel on first launch.
- Existing routing, EQ, channel tests and high-DPI layouts are retained.

## Settings

- **Audio**: volume, latency, left/right balance, balance reset, left/right test tones, and equalizer. Volume, balance, and test tones remain available during playback. Adjust latency after stopping playback; the change takes effect on the next playback session. Test tones temporarily bypass the user balance value and restore it when testing ends.
- **General**: launch at startup, runtime and current-version information, and manual update checks. Update checks use only the latest stable Release in this repository, not drafts or pre-releases. A timeout is reported as a failed check rather than incorrectly reporting that the application is up to date. The new-version button opens the official Release page and does not download or execute files automatically.
- **Diagnostics**: view issues and open the actual diagnostics folder. The folder can be opened even when no records exist; it is created automatically when necessary. Logs are written to the installation directory's `Diagnostics` folder when possible, with a fallback to the current user's local application-data directory at `AirStereo/Diagnostics` when the installation directory is not writable.
- **Runtime log**: view current runtime information.

Diagnostic logs may contain speaker names, network addresses, and system information. Review them before sharing. AirStereo does not upload diagnostics automatically or install updates automatically.

## Feedback and Feature Requests

Use this repository's [Issues](https://github.com/Flourishze/AirStereo/issues) to report connection failures, missing audio, channel or interface problems, or to request a feature.

For a bug report, include the AirStereo version, Windows version, display scaling (for interface issues), speaker model, route type (single speaker, two independent speakers, or native pair), steps to reproduce, and screenshots or redacted diagnostic logs when available. Search existing issues before opening a duplicate. Do not publish passwords, tokens, or personal and network information without reviewing it first.

## Build and Documentation

See the [build guide](docs/BUILD.md), [acceptance checklist](docs/TESTING.md), [changelog](CHANGELOG.md), and [1.0.4 release notes](docs/releases/v1.0.4.md). The repository includes the application source and packaging scripts. The original source for the existing installer wizard is not included, so packaging still depends on the explicitly documented binary template.

## License

AirStereo's own code and documentation are licensed under the [MIT License](LICENSE), Copyright (c) 2026 Flourishze. The copyright notice and license text must be retained when using, modifying, or distributing the software. The software is provided "as is", without warranty.

Third-party code, dependencies, the .NET runtime, and installer binary templates remain subject to their respective licenses; this repository's MIT License does not change those terms. The .NET runtime distributed with the installer retains its original `LICENSE` and `ThirdPartyNotices` files.

AirStereo is an independent project and is not an official Apple application. AirPlay, HomePod, and other trademarks belong to their respective owners.
