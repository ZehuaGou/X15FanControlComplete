# ClevoEcInfo.dll — required native EC library

**This repository does NOT include or distribute ClevoEcInfo.dll.**  
The DLL is a third-party binary whose license, origin, and redistribution terms are unknown.

## How to obtain

1. Locate `ClevoEcInfo.dll` in your existing Brz / Clevo fan-control installation directory.
2. Copy it to `vendor\ClevoEcInfo.dll` in this project.
3. The build script `scripts\copy-release.bat` will copy it into `dist\` automatically.

## Requirements

| Attribute | Value |
|-----------|-------|
| Architecture | x86 / PE32 (32-bit) |
| Known exports | `InitIo`, `GetTempFanDuty`, `SetFanDuty`, `SetFanDutyAuto`, `GetFanCount`, `GetCpuFanRpm`, `GetGpuFanRpm` |
| SHA-256 (reference) | `f1fa68742b86022ce436d9998c3a7de34d64866eefc95e40c12f6439328ba656` |

## Legal notice

- The source code in this repository does **not** reimplement, modify, or reverse-engineer ClevoEcInfo.dll or its NTPort driver.
- The DLL must only be used on the machine from which it was obtained.
- Do **not** download replacement copies from unverified sources.
- If you do not have a legal copy of this DLL, the project cannot be built or run.
