# X15 Fan Control

A safety-first, x86 Windows fan controller for the **COLORFUL X15 AT 23 / Clevo NP50SNE**. Features temperature filtering, asymmetric ramping, acoustic stable zones, write verification with external-override detection, adaptive CPU power strategies, GPU read-only telemetry, Control Center takeover with lease-based recovery, and an independent watchdog.

> ⚠ **Hardware compatibility warning**
> - This software has been developed and tested only on **COLORFUL X15 AT 23 / Clevo NP50SNE**.
> - Other Clevo models may use different EC registers and are **not** guaranteed to work.
> - Incorrect EC writes could cause fan malfunction or system instability.
> - **Always complete ReadOnly and Simulation validation before switching to Active mode.**
> - **Never run two fan control applications simultaneously.**
> - You assume all hardware risk.

## Features

- **Three run modes**: ReadOnly (sensor read only), Simulation (engine runs, no EC writes), Active (real control)
- **Write verification**: every EC duty write is read back at 50 ms and 1000 ms; a confirmed external override (three consecutive mismatches) restores Auto and drops to ReadOnly
- **Adaptive power strategies**: four tiers (Quiet / Daily / Code / Heavy) selected automatically from CPU load evidence, or pinned via fixed strategies
- **Compile fast-track**: sustained CPU ≥ 80% (build/compile load) upshifts quickly but one tier at a time; GPU load never raises the CPU power tier (CPU requested tier is driven by CPU utilization, CPU temperature and duration only — GPU data is diagnostics only)
- **GPU thermal demand**: GPU utilization + power telemetry + temperature + duration produce a demand level (Low/Moderate/High) that only affects the GPU fan curve bias, cross-fan assist eligibility and diagnostics — there is no GPU wattage control path (production GPU backend is fixed TelemetryOnly, zero GPU Set calls)
- **Shared thermal shedding** (Auto mode only): when the GPU is near its thermal ceiling, the GPU fan is near full speed (≥ 95%) and the CPU is also hot (≥ 85 °C) for 20 continuous seconds, the CPU effective power tier is capped at Quiet (25/35 W) to give the shared thermal budget back to the GPU — recovered after 60 continuous seconds below 78/80 °C. The fan profile tier is decoupled from the power tier (the fan curve keeps the pre-shedding tier, never the Quiet curve). Offline implementation only; hardware A/B pending
- **Acoustic budget governor**: requested performance tier is decoupled from the effective power tier. If the fans sit at the tier's acoustic soft maximum while temperature keeps rising, the effective power tier is held or lowered instead of raised — "raising the performance tier is not the same as raising the cooling tier". Soft maximums are not safety limits: emergency stages, fast temperature rise, and the RPM guard break through immediately
- **Fail-safe supervision**: an independent watchdog process restores the EC to Auto if the main program exits, hangs (30 s heartbeat timeout), or is killed
- **Load-test supervision chain** (offline, load disabled): `tools/load-test-supervisor.ps1` + `tools/load-test-worker.ps1` implement the fixed-load safety contract (temperature pre-gate, in-window 82 °C abort, EC-failure abort, future-timestamp fail-closed, taskkill tree-first termination) validated by 17 offline fake tests. The old `-FixedLoad` sampler path stays disabled after the load-E incident (evidence marked `INVALID_FOR_MECHANISM_CONCLUSION`)
- **Control Center takeover**: the app briefly stops CLEVO Control Center's services/FnKey so its power limits can be written; a lease file plus a second watchdog restore them on exit or crash
- **Safety floors**: three-stage emergency fan ramps by temperature, CPU RPM safety guard, sensor-invalid fallback (out-of-range sensor bytes such as 144 °C never trigger the shared emergency path), and rate-limited up/down ramping
- **Logging**: CSV telemetry (~1 Hz, includes `cpu_requested_power_tier` / `cpu_effective_power_tier` / `gpu_thermal_demand` / `shared_thermal_shedding_active` / `cpu_fan_profile_tier` columns), async application log with throttled UI updates, and verification logs

## Project status

- ✅ Solution compiles on Windows with .NET Framework 4.8 (Release x86 + x64 helper)
- ✅ ReadOnly / Simulation / Active verified on target hardware (fan control, watchdog, sleep/resume, tray exit)
- ✅ CPU power control, dual-fan Active control, watchdog recovery, and final restoration verified on target hardware
- ✅ GPU capability audited: temperature/utilization/power/P-State telemetry works; this device does not expose a supported watt-limit setter, so production remains TelemetryOnly
- ✅ Final desktop surface contains only **Overview / Strategy / Logs**; the hardware-unvalidated fixed-duty acoustic calibration page and placeholder report were removed
- ✅ Shared-thermal CPU shedding entry and 25/35 W DCHU readback were observed live; this test did **not** demonstrate a temperature or noise improvement in the GPU-limited game scene
- ✅ Shared thermal shedding implemented offline (Auto-only state machine, fan-profile/power decoupling) with 13 unit tests and a real-CSV offline replay (`tools/SharedThermalReplay`): on the 2026-08-03 game log, one activation episode would cover 767 samples, all pressing CPU effective power from Daily to Quiet — replay is descriptive, not a claimed improvement
- ✅ E0 collection contract implemented: `X15EcProbe --abort-cpu-temp <C>` (in-sample abort, exit 3) and RPM plausibility columns (`IMPLAUSIBLE_RAW_RPM_READ`); 7 offline fake-telemetry tests
- ✅ Load-test supervision chain offline tests 17/17 (`tools/load-test-supervisor.tests.ps1`); load path remains disabled
- ⏳ Hardware acceptance (E0) is **gated**: the first E0 attempt failed the temperature gate (first CPU sample 82 °C; `TEMPERATURE_GATE_FAILED / IMMEDIATE_ABORT_NOT_IMPLEMENTED` was corrected — the batch sampler now aborts in-sample). E1+ await user approval after the machine cools
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

The combined runnable folder is `dist\`. Run the regression suite with:

```bat
tests\X15FanCore.Tests\bin\x86\Release\net48\X15FanCore.Tests.exe
```

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
- **Tray "退出"** → full cleanup and exit (restores Auto and Control Center, stops watchdogs, disposes EC)

### Auto-start

Tray menu "开机自动启动" creates a scheduled task (`schtasks /SC ONLOGON /RL HIGHEST`) to launch the program at login with `--autostart --minimized`. The saved Active preference is restored automatically once EC, telemetry, and the Control Center lease all pass their gates.

### Power strategies

The strategy dropdown contains **Auto** plus four fixed strategies. In Auto mode the tier moves automatically; the fixed modes pin one tier and never drift.

| Strategy | PL1/PL2 (W) | Window (s) | CPU Max Performance | Notes |
|----------|-------------|------------|---------------------|-------|
| Auto | Adaptive | 28 | Adaptive | Automatic tier selection |
| Quiet | 25 / 35 | 28 | 75% | Lowest fan curve, thermal-safe gate |
| Daily | 30 / 45 | 28 | 85% | Default neutral tier |
| Code | 38 / 55 | 28 | 95% | Interactive/compile workloads |
| Heavy | 55 / 69 | 28 | 100% | Full performance |

### Adaptive tier logic

The four tiers (Quiet / Daily / Code / Heavy) are selected from 8-second CPU load evidence windows; downshifts use 30-second averages plus 15-second peak gates, so upshifts are fast and downshifts are deliberately slow to avoid flapping. **CPU requested tier is decided only by CPU utilization, CPU temperature and duration** (架构收束 2026-08-02): GPU utilization/power no longer gates the CPU tier.

| Transition | Gate |
|------------|------|
| Quiet → Daily | CPU ≥ 12% |
| Daily → Code | CPU ≥ 25% |
| Code → Heavy | CPU ≥ 50% |
| Heavy → Code | avg CPU ≤ 25%, peak < 60%, 60 s dwell |
| Code → Daily | avg CPU ≤ 17%, peak < 40%, 120 s dwell |
| Daily → Quiet | avg CPU ≤ 8%, peak < 30%, 120 s dwell, plus thermal gate (CPU < 85 °C, rise ≤ 0.5 °C/s) |

**Compile fast-track**: sustained CPU ≥ 80% (build/compile load) pierces the 20 s minimum tier hold, but still requires the 12-second strong-evidence dwell for each adjacent upshift. Downshift dwells are never compressed. GPU high load does not raise the CPU power tier; in a GPU-heavy game with low CPU load the CPU tier stays Daily while the GPU fan responds via its own thermal demand path.

> The thresholds were calibrated against a 23-hour usage trace on the target machine (84,139 one-second samples). See `src/X15FanCore/Control/AdaptivePowerTier.cs` for the data notes.

### Acoustic budget (soft limits)

Each tier has per-channel acoustic parameters (comfort duty, soft maximum fan duty, target temperature — candidate values, not hardware-calibrated):

| Tier | Soft max fan duty (candidate) | Target temp |
|------|------------------------------|-------------|
| Quiet | ~62% | 85 °C |
| Daily | ~71% | 88 °C |
| Code | ~80% | 88 °C |
| Heavy | ~88% | 88 °C |

These are **soft** limits, not safety caps:

- Emergency stages, fast temperature rise (> 1 °C/s) and the RPM guard break through immediately.
- If the fans sit at the soft maximum while temperature keeps rising for the saturation dwell (default 20 s), the effective power tier is held or lowered — the machine cools by reducing PL1/PL2, not by raising the fan ceiling.
- Recovery requires stable/falling temperature and fan duty below the soft maximum by a margin for 60–120 s, so power and acoustics do not flap.
- Quiet and performance are a physical trade-off: a quieter fan curve at the same power limit means higher temperatures; a lower effective power tier is how this trade-off is enforced.
- Power tiers always come from the fixed safe presets (Quiet 25/35 W, Daily 30/45 W, Code 38/55 W, Heavy 55/69 W) shared with the X15XtuBridge whitelist — arbitrary power values cannot be written.

> Acoustic parameters are initial candidates for offline validation only; they have **not** been hardware-calibrated yet.

### Profiles

Each strategy has a matching fan profile; the active profile follows the strategy tier automatically (fixed strategies pin their profile):

- **自动** — Auto entry point; fan behavior follows the current tier
- **安静** — Quiet: gentle curve, no stable zone, long down-hold (20 s)
- **日常** — Daily: acoustic stable zone 45–50% (hold 48%)
- **代码** — Code: stable zone 50–55% (hold 53%), slow ramp
- **重负载** — Heavy: aggressive curve, earlier cooling, CPU/GPU cross-fan coupling

### Cross-fan assist (主扇优先、辅助延迟介入)

CPU 热源默认由 CPU 风扇负责，GPU 热源默认由 GPU 风扇负责。另一侧风扇只在满足全部条件后提供辅助（架构收束 2026-08-02）：

- 主通道温度接近或超过目标温度；
- 主风扇已接近本档声学软上限；
- 温度连续 20 秒没有明显下降（不是短暂尖峰）；
- 辅助量从低值缓慢增加，先限制为主风扇目标的 20%~30% 等效辅助量（候选值，未硬件标定前不宣称最终值）；
- 辅助退出需要温度恢复余量、温升率 ≤ 0、连续 60 秒稳定（滞回避免反复开关）；
- Emergency 与快速温升保护可无条件突破软上限，让两个风扇共同散热；
- GPU 高、CPU 低的游戏场景：GPU 风扇快速响应，CPU 功耗保持自己的 requested tier，CPU 风扇仅在 GPU 持续压不住时辅助；CPU 高、GPU 低时完全对称。

The final UI intentionally does not expose arbitrary curve, power, OC, VF, lock-frequency, or fixed-duty calibration editors. Production behavior comes from the versioned built-in profiles and safety contracts.

### Shared thermal shedding (GPU 过热时 CPU 临时让出热预算)

**Auto 模式专属**。当 GPU 接近热上限、GPU 风扇接近全速且 CPU 同步高热时，持续一段时间后把 CPU 有效功耗临时压到 Quiet（25/35/28 W），为 GPU 与共享热管让出热预算；冷却稳定后自动恢复。

- **进入条件**（全部成立并连续累计 20 s）：Auto 模式；CPU/GPU 温度遥测有效（各自 profile min/max 校验）；GPU ≥ 84 °C；CPU ≥ 85 °C（**平坦 87 °C 也计入**，不要求继续升温）；GPU 实际风扇占空 ≥ 95%。GPU 后端是否可写不参与判断（生产恒 TelemetryOnly）。
- **激活后**：CPU effective power tier 至多 Quiet；已是 Quiet 保持；不允许任何升档；Emergency 仍可双风扇突破到 100%。
- **风扇/功耗解耦**：CPU 功耗可降为 Quiet，但风扇 profile 保持进入前档位（至少不低，见 CSV `cpu_fan_profile_tier`）——绝不因 CPU 降到 Quiet 而套用 Quiet 风扇曲线。
- **恢复**（连续满足 60 s，任一条件失效即清零）：GPU ≤ 78 °C 且 CPU ≤ 80 °C 且遥测有效；恢复后回到正常治理链输出，不直接强升功耗。
- **时间连续性**：采样时间戳积分；gap > 3 s 不补算并废弃进入/恢复信用；时间戳倒退不累计；短尖峰/间断脉冲不拼接；激活后遥测丢失保持较低 CPU 功耗等待有效恢复证据。
- 阈值均为候选值（未硬件标定）；实现见 `src/X15FanCore/Control/SharedThermalBudgetController.cs`，离线测试 13 项。**尚未实机 A/B 验证，不构成任何温降/噪声/帧率改善声明。**

### E0 collection contract（X15EcProbe 采样内中止与 RPM 合理性）

`src/X15EcProbe`（只读 EC 采样器，白名单调用）支持：

- `--abort-cpu-temp <C>`：每样本读取后先写入并 flush 当前样本，CPU 温度 ≥ C 时**本轮立即退出**（exit 3，输出 `ABORT_CPU_TEMP observed=… threshold=…`），不继续 sleep 或读下一个样本；默认不传保持旧行为。E0 硬性门禁使用 `--abort-cpu-temp 70`；
- CSV 增加 `cpu_rpm_plausible / gpu_rpm_plausible` 列（RPM > 10000，或 Duty ≥ 20% 且 RPM < 200 → `IMPLAUSIBLE_RAW_RPM_READ / ROOT_CAUSE_UNKNOWN`；仅 E0 数据质量检查，不进入生产风扇算法）；
- 逻辑与测试共享 `src/X15FanCore/Probe/EcProbeContract.cs`（fake telemetry 测试 7 项）。

实机验收流程（E0–E5，分门禁、每阶段停止等待批准）见 `docs/HARDWARE_ACCEPTANCE_PLAN.md`；离线架构收束与安全合同总结见 `docs/ARCHITECTURE_CONSOLIDATION_20260802.md`。

### Load-test supervision chain（负载监督链，离线）

固定负载路径当前**禁用**（load-E 事故后 `-FixedLoad` 拒绝执行，证据标记
`INVALID_FOR_MECHANISM_CONCLUSION`）。监督链本身已完成并离线验证：

- `tools/load-test-supervisor.ps1`：独立监督器（状态文件驱动、mode flag 带 nonce/UTC 校验、心跳先于 worker、taskkill 进程树优先 + Stop-Process 回退 + 终止验证、未来时间戳 fail-closed、预热门禁 >70 °C 彻底清零重新累计）；
- `tools/load-test-worker.ps1`：独立 worker（监督器心跳过期自退；`-IdleMode` 供离线测试）；
- `tools/load-test-supervisor.tests.ps1`：17 项离线集成测试（fake 进程/临时目录，无负载、无硬件）。

### Write verification & external override detection

Every EC duty write in Active mode is verified asynchronously: a 50 ms readback confirms the write took effect, and a 1000 ms readback confirms it stuck. Three consecutive mismatched readbacks against the last written duty confirm an **external override** (another controller fighting the EC register); the program then restores Auto, drops to ReadOnly, and logs the event. Readback values and override state are shown on the dashboard and recorded in the CSV log.

### Control Center takeover

In Active mode the program briefly stops `CCDCHUService` / `XTU3SERVICE` and the FnKey process, then writes CPU power limits through the installed CLEVO Control Center SDK (with readback confirmation and rollback). The takeover is recorded in a lease file; on exit, sleep, or crash a dedicated lease watchdog restores the original service and FnKey state.

## Data locations

The application writes to:

```text
%LOCALAPPDATA%\X15FanControl\
```

This includes `config.json`, heartbeat state, CSV logs, application logs, watchdog logs, and the Control Center lease file. CSV logs are retained for 7 days by default.

## Architecture

- **X15FanControl** — WinForms GUI (x86): overview, built-in strategy descriptions, logs, tray, and control loop
- **X15FanCore** — Core library: EC wrapper, fan curves, temperature filter, control engine, adaptive power tiers, cross-fan assist, shared thermal shedding, acoustic governor, heartbeat, lease, telemetry client
- **X15FanWatchdog** — Independent process that restores fans to Auto (and Control Center, in lease-only mode) if the main program exits or stops beating
- **X15GpuTelemetry** — x64 helper that runs nvidia-smi and outputs JSON telemetry via stdout (process-tree cleanup via `taskkill /T`)
- **X15XtuBridge** — Short-lived bridge for Control Center / Intel XTU power writes, with range validation and readback verification
- **X15EcProbe** — Read-only EC sampler (temp/RPM/duty; `--abort-cpu-temp` in-sample abort, `--status-file` for the supervision chain)
- **X15DchuProbe** — Read-only DCHU AppSettings probe (OEM mode byte, CPU PL1/PL2/Tau, GPU OC stored values)
- **X15GpuPowerProbe** — Read-only NVML diagnostic probe (production never writes GPU power)
- **tools/** — `static_sanity_check.py` guardrails, load-test supervision chain (offline), `SharedThermalReplay` CSV replay tool

## Documentation

- `docs/ALGORITHM.md` — control algorithm notes
- `docs/ARCHITECTURE_CONSOLIDATION_20260802.md` — architecture consolidation & offline delivery records
- `docs/CONTROL_CENTER_CPU_GPU_MECHANISM_AUDIT.md` — Control Center mechanism audit (incl. load-E incident record)
- `docs/CPU_GPU_POWER_CAPABILITY_AUDIT.md` — CPU/GPU power capability audit (historical prototype sections marked)
- `docs/HARDWARE_ACCEPTANCE_PLAN.md` — gated E0–E5 hardware acceptance plan (not yet executed)

## License

The source code, scripts, and documentation in this repository are licensed under the MIT License. See `LICENSE` for details.

**Not covered by MIT license:**
- `ClevoEcInfo.dll` — third-party binary, not distributed with this repository
- NTPort driver — third-party component
- NVIDIA nvidia-smi — third-party tool
