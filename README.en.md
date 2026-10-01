# AirStereo

[中文](README.md)

Windows x64 AirPlay audio sender. The current version is **1.0.2**. The official source, release, and update-check source is [Flourishze/AirStereo](https://github.com/Flourishze/AirStereo).

## Installation and Usage

Download `AirStereo-Setup-1.0.2-x64.exe` from the repository's Releases page. Exit an older version from the tray menu before installing. The installer includes the .NET Core and Windows Desktop 10.0.12 runtimes and prefers the runtimes in the installation directory. No separate .NET runtime installation is required; the operating system must still be a Windows x64 version supported by .NET 10.

1. Click the AirStereo tray icon in the lower-right corner, scan for speakers, and select the targets you want to use.
2. Playback is disabled when nothing is selected. Selecting one independent speaker sends full stereo audio and disables the left/right balance control.
3. Selecting two independent speakers in order assigns the first speaker to L and the second speaker to R. Both speakers share one media timeline and PTP clock. A maximum of two independent speakers can be selected. To swap L/R, clear the selection and select the speakers in the opposite order.
4. A native stereo pair identified by mDNS is shown as one logical target. The receiver handles the physical left/right assignment while AirStereo sends full stereo audio. Pair members cannot be selected separately, and a relationship inferred only from names is not treated as a confirmed native pair.
5. Click Play to send computer audio. If either speaker in a two-speaker session disconnects, the entire session stops and the affected device is reported instead of silently degrading to mono.

Actual compatibility depends on receiver firmware, access permissions, network conditions, and the service information exposed by the receiver. Not every device advertised as AirPlay-compatible is guaranteed to connect.

## Settings

- **Audio**: volume, latency, left/right balance, balance reset, left/right test tones, and equalizer. Playback-time adjustment remains available; test tones temporarily bypass the user balance value and restore it when testing ends.
- **General**: launch at startup, runtime and current-version information, and manual update checks. Update checks use only the latest stable Release in this repository, not drafts or pre-releases. A timeout is reported as a failed check rather than incorrectly reporting that the application is up to date. The new-version button opens the official Release page and does not download or execute files automatically.
- **Diagnostics**: view issues and open the actual diagnostics folder. The folder can be opened even when no records exist; it is created automatically when necessary. Logs are written to the installation directory's `Diagnostics` folder when possible, with a fallback to the current user's local application-data directory at `AirStereo/Diagnostics` when the installation directory is not writable.
- **Runtime log**: view current runtime information.

Diagnostic logs may contain speaker names, network addresses, and system information. Review them before sharing. AirStereo does not upload diagnostics automatically or install updates automatically.

## Build and Documentation

See the [build guide](docs/BUILD.md), [acceptance checklist](docs/TESTING.md), [changelog](CHANGELOG.md), and [1.0.2 release notes](docs/releases/v1.0.2.md). The repository includes the application source and packaging scripts. The original source for the existing installer wizard is not included, so packaging still depends on the explicitly documented binary template.

## License

AirStereo's own code and documentation are licensed under the [MIT License](LICENSE), Copyright (c) 2026 Flourishze. The copyright notice and license text must be retained when using, modifying, or distributing the software. The software is provided "as is", without warranty.

Third-party code, dependencies, the .NET runtime, and installer binary templates remain subject to their respective licenses; this repository's MIT License does not change those terms. The .NET runtime distributed with the installer retains its original `LICENSE` and `ThirdPartyNotices` files.
