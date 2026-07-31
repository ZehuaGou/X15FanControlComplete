# X15 Fan Control

A safety-first, x86 Windows fan controller for the **COLORFUL X15 AT 23 / Clevo NP50SNE**. Features temperature filtering, hysteresis, asymmetric ramping, acoustic stable zones, calibration tools, logging, adaptive power strategies, and fail-safe Auto restoration.

> ⚠ **Hardware compatibility warning**
> - This software has been developed and tested only on **COLORFUL X15 AT 23 / Clevo NP50SNE**.
> - Other Clevo models may use different EC registers and are **not** guaranteed to work.
> - Incorrect EC writes could cause fan malfunction or system instability.
> - **Always complete ReadOnly and Simulation validation before switching to Active mode.**
> - **Never run two fan control applications simultaneously.**
> - You assume all hardware risk.

## Project status

- ✅ Solution compiles on Windows with .NET Framework 4.8 (Release x86 + x64 helper)
- ✅ Basic ReadOnly GUI validation completed on target hardware
- ✅ Simulation mode verified
- ✅ Active mode tested (fan control, watchdog, sleep/resume, tray exit)
- ⏳ GPU acoustic calibration sweep — **not yet completed**
- ✅ Adaptive power strategies (Auto/Quiet/Code/Heavy)
- ✅ Desktop experience: minimize to taskbar, close to tray, auto-start, async logging

## Build

Requirements:

- Windows 10 or 11
- .NET SDK 8.0 or later (install from https://dotnet.microsoft.com/download)
- .NET Framework 4.8 targeting pack

Primary build command:

```bat
scripts\build-release-dotnet.bat
```

Or manually:

```bat
dotnet build .\X15FanControl.sln -c Release -p:Platform=x86
```

The combined runnable folder is `dist\`.

### Required third-party binary

This repository does **not** include `vendor\ClevoEcInfo.dll`. Before running:

1. Locate `ClevoEcInfo.dll` in your existing Clevo fan-control installation directory.
2. Copy it to `vendor\ClevoEcInfo.dll`.
3. Re-run the build script or manually copy it to `dist\`.

See `vendor\README.md` for the required SHA-256 and technical details.

## Safe first run

1. Build and run `dist\X15FanControl.exe` as administrator.
2. Stay in **ReadOnly** for several minutes. Confirm CPU/GPU temperatures, duty percentages, and RPM values are plausible.
3. Switch to **Simulation**. Confirm the controller target moves gradually and emergency thresholds are correct.
4. Only then switch to **Active**. Keep the Restore Auto button visible and start at idle.
5. Test sleep/resume and application exit. Confirm the original Auto controller resumes.
6. Run the calibration sweep only after normal Active operation is stable.

See [docs/SAFE_TEST_PLAN.md](docs/SAFE_TEST_PLAN.md) for the detailed test sequence.

## Usage

### Three modes

| Mode | Description |
|------|-------------|
| **ReadOnly** | Only reads sensors, no EC writes. Default startup mode. |
| **Simulation** | Control engine runs, targets are displayed, but no EC writes. |
| **Active** | Actual EC fan control with watchdog, heartbeat, and fail-safe Auto restoration. |

### Window behavior

- **Minimize button** → minimizes to Windows taskbar (not tray)
- **Close ×** → hides to system tray, control loop continues running
- **Tray double-click / "打开主窗口"** → restores window
- **Tray "退出"** → full cleanup and exit (restores Auto, stops watchdog, disposes EC)

### Auto-start

Tray menu "开机自动启动" creates a scheduled task (`schtasks /SC ONLOGON /RL HIGHEST`) to launch the program at login with `--autostart --minimized`.

### Profiles

Five built-in profiles, selectable from the dropdown:

- **静音稳定－平衡** (default): CPU stable zone 50–55%, hold at 53%, slow ramp
- **静音稳定－低噪**: Lower hold point (52%) for noise-sensitive users
- **极致静音**: ≤35% fan duty below 60°C, very slow ramp, for light workloads
- **当前 Brz 曲线**: Reproduces original Brz temperature/power curves
- **性能模式**: Earlier cooling, higher airflow, CPU/GPU cross-fan assistance

All values are editable in the Profiles & curves tab. Fan curves, temperature thresholds, and safety logic are not modified by profile changes.

### Fixed power strategies

The strategy dropdown contains four built-in choices: **Auto**, **Quiet**, **Code**, and **Heavy**. Power limits, dwell times, and safety fan curves are fixed in the program.

| Strategy | PL1/PL2 (W) | Window | CPU Ceiling |
|----------|-------------|--------|-------------|
| Auto (default) | Adaptive | 30s up / 120s down | Adaptive |
| Quiet | 25 / 35 | 28s | 75% |
| Code | 38 / 55 | 28s | 95% |
| Heavy | 55 / 69 | 28s | 100% |

## Data locations

The application writes to:

```text
%LOCALAPPDATA%\X15FanControl\
```

This includes `config.json`, heartbeat state, CSV logs, application logs, and watchdog logs.

## Architecture

- **X15FanControl** — WinForms GUI (x86), dashboard, profiles, calibration, tray
- **X15FanCore** — Core library: EC wrapper, fan curves, filters, control engine, power strategies
- **X15FanWatchdog** — Independent process that restores fans to Auto if the main program exits
- **X15GpuTelemetry** — x64 helper that runs nvidia-smi and outputs JSON telemetry via stdout (uses Windows Job Object for cleanup)
- **X15XtuBridge** — Safe bridge for Intel XTU / Control Center power writes

## License

The source code, scripts, and documentation in this repository are licensed under the MIT License. See `LICENSE` for details.

**Not covered by MIT license:**
- `ClevoEcInfo.dll` — third-party binary, not distributed with this repository
- NTPort driver — third-party component
- NVIDIA nvidia-smi — third-party tool