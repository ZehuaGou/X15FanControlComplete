# X15 Fan Control

A safety-first, x86 Windows fan controller for the **COLORFUL X15 AT 23 / Clevo NP50SNE**. It reuses the working `ClevoEcInfo.dll` from the user's Brz installation and replaces the instantaneous fan-curve logic with filtering, hysteresis, asymmetric ramping, stable acoustic zones, calibration tools, logging, and fail-safe Auto restoration.

> ⚠ **Hardware compatibility warning**
> - This software has been developed and tested only on **COLORFUL X15 AT 23 / Clevo NP50SNE**.
> - Other Clevo models may use different EC registers and are **not** guaranteed to work.
> - Incorrect EC writes could cause fan malfunction or system instability.
> - **Always complete ReadOnly and Simulation validation before switching to Active mode.**
> - **Never run two fan control applications simultaneously.**
> - You assume all hardware risk.

## Important limits

This project can control how abruptly the fan command changes. It cannot change the physical sound made by the fan at a given RPM. The final curve and stable-zone values must be verified on the actual notebook.

The native DLL and its underlying NTPort driver are third-party components supplied with the existing working installation. **This repository does not distribute ClevoEcInfo.dll.** You must copy it from your own Brz Clevo fan-control installation. See `vendor\README.md` for instructions.

## Project status

- ✅ Solution compiles on Windows with .NET Framework 4.8 (Release x86 + x64 helper)
- ✅ Basic ReadOnly GUI validation completed on target hardware
- ✅ Simulation mode verified
- ⏳ Active mode testing in progress
- ❌ GPU acoustic calibration sweep — **not yet completed**
- ❌ Full normal-use Active verification — **not yet completed**

## Build

Requirements:

- Windows 10 or 11
- .NET SDK 8.0 or later (install from https://dotnet.microsoft.com/download)
- .NET Framework 4.8 targeting pack (included with Visual Studio or .NET SDK)

Primary build command:

```bat
scripts\build-release-dotnet.bat
```

Or manually:

```bat
dotnet build .\X15FanControl.sln -c Release -p:Platform=x86
```

The combined runnable folder is `dist\`.

**Alternative:** `scripts\build-vs2022.bat` requires Visual Studio 2022 with the .NET desktop development workload and MSBuild. It is a fallback option; the dotnet-based build is preferred.

### Required third-party binary

This repository does **not** include `vendor\ClevoEcInfo.dll`. Before running:

1. Locate `ClevoEcInfo.dll` in your existing Brz / Clevo fan-control installation directory.
2. Copy it to `vendor\ClevoEcInfo.dll`.
3. Re-run the build script or manually copy it to `dist\`.

See `vendor\README.md` for the required SHA-256 and technical details.

## Safe first run

1. Keep the original Brz folder unchanged as a fallback.
2. Disable Brz automatic startup and its `EcWatchDog.exe`.
3. Run `scripts\stop-original-controller.ps1` as administrator.
4. Build and run `dist\X15FanControl.exe` as administrator.
5. Stay in **ReadOnly** for several minutes. Confirm CPU/GPU temperatures, duty percentages, and RPM values are plausible.
6. Switch to **Simulation**. Confirm the controller target moves gradually and emergency thresholds are correct.
7. Only then switch to **Active**. Keep the Restore Auto button visible and start at idle.
8. Test sleep/resume and application exit. Confirm the original Auto controller resumes.
9. Run the calibration sweep only after normal Active operation is stable.

See [docs/SAFE_TEST_PLAN.md](docs/SAFE_TEST_PLAN.md) for the detailed test sequence.

## Data locations

The application writes to:

```text
%LOCALAPPDATA%\X15FanControl\
```

This includes `config.json`, heartbeat state, CSV logs, application logs, and watchdog logs.

## Default profiles

All values are editable in the Profiles & curves tab.

### 静音稳定－平衡 (default)
The default recommended profile for daily use.

- **CPU stable zone:** 50–55%, hold at 53%
- **CPU curve:** 40°C→5%, 50°C→15%, 60°C→35%, 70°C→52%, 80°C→54%, 85°C→66%, 90°C→90%, 93°C→100%
- **Ramp up:** 1.5%/s, **ramp down:** 0.4%/s
- **Down hold:** 15 seconds
- **Hysteresis:** 3°C, **deadband:** 1.5%
- **Filter:** 4-sample window, fast α=0.45, slow α=0.18
- **Emergency:** Stage 1 at 87°C→75%, Stage 2 at 90°C→90%, Stage 3 at 93°C→100%
- **GPU stable zone:** 48–54%, hold at 50%

### 静音稳定－低噪
Lower stable-platform hold point (52%) for users more sensitive to fan noise.

### 极致静音
Prioritises silence over aggressive cooling:
- ≤35% fan duty below 60°C
- Very slow ramp up (1.0%/s) and down (0.3%/s)
- 20-second down-hold delay
- Higher emergency thresholds (Stage 1 at 90°C)
- Suitable for light workloads (browsing, office, video playback)

### 当前 Brz 曲线
Reproduces the original Brz temperature/power curves with added smoothing and safety.

### 性能模式
Earlier cooling and higher steady airflow for sustained heavy load. CPU/GPU cross-fan assistance enabled.

## License

The source code, scripts, and documentation in this repository are licensed under the MIT License. See `LICENSE` for details.

**Not covered by MIT license:**
- `ClevoEcInfo.dll` — third-party binary, not distributed with this repository
- NTPort driver — third-party component
- NVIDIA nvidia-smi — third-party tool
