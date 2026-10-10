# LibALAC Magic Cookie Capacity Fix

Upstream: https://github.com/GiteKat/LibALAC
Commit: bc03e0d311a61d5a14ae2a63a188bde845ec6aa3
License: Apache-2.0; see LICENSE and original source notices.
Modified: 2026-10-10.

LibALAC.cpp GetMagicCookie used an uninitialized ioNumBytes. The underlying
ALACEncoder::GetMagicCookie reads this input as buffer capacity, and returns zero
if it is smaller than the required cookie size. Initialize it using
GetMagicCookieSize(encoder). No codec or packet format changes.

Directory.Build.targets selects a static release CRT for standalone deployment.
It also supplies the correctly cased LIBALAC_EXPORTS definition and disables
release PDB generation; LibALAC.cpp explicitly includes stdlib.h for modern MSVC.
Run installer/build-alac.ps1 from the application source root to rebuild the DLL
before running build.ps1. Only x64 is supported by this build script.
