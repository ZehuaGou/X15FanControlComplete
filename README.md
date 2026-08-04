# X15 风扇控制（X15 Fan Control）

面向 **COLORFUL X15 AT 23 / Clevo NP50SNE** 的安全优先 x86 Windows 风扇控制器。具备温度滤波、非对称升降速、声学稳定区、写入回读验证与外部覆盖检测、自适应 CPU 功耗策略、GPU 只读遥测、Control Center 接管与租约恢复，以及独立看门狗。

> ⚠ **硬件兼容性警告**
> - 本软件仅在 **COLORFUL X15 AT 23 / Clevo NP50SNE** 上开发与测试。
> - 其他 Clevo 机型可能使用不同的 EC 寄存器，**不保证可用**。
> - 错误的 EC 写入可能导致风扇异常或系统不稳定。
> - **切换到 Active 模式前，必须完成 ReadOnly 与 Simulation 验证。**
> - **切勿同时运行两个风扇控制程序。**
> - 一切硬件风险自负。

## 功能特性

- **三种运行模式**：ReadOnly（只读传感器）、Simulation（引擎运行、不写 EC）、Active（真实控制）
- **写入验证**：每次 EC 占空写入后以 50 ms / 1000 ms 回读确认；确认连续三次失配即判定**外部覆盖**（其他控制器争抢 EC 寄存器），程序恢复 Auto 并降回 ReadOnly
- **自适应功耗策略**：四档（安静 / 日常 / 代码 / 重负载）由 CPU 负载证据自动选择，也可固定策略钉住某一档
- **编译快车道**：持续 CPU ≥ 80%（编译/构建负载）快速升档但逐档推进；GPU 负载**不会**提升 CPU 功耗档（CPU 请求档只由 CPU 利用率、CPU 温度与持续时间决定——GPU 数据仅作诊断）
- **GPU 热需求**：GPU 利用率 + 功耗遥测 + 温度 + 持续时间产生需求等级（Low / Moderate / High），只作用于 GPU 风扇曲线偏置、跨风扇辅助判定与诊断——**不存在 GPU 瓦数控制路径**（生产 GPU 后端固定 TelemetryOnly，GPU Set 调用数为零）
- **共享热预算让出**（仅 Auto 模式）：GPU 接近热上限、GPU 风扇接近全速（≥ 95%）、CPU 同步高热（≥ 85 °C）持续 20 秒后，CPU 有效功耗档压到安静档（25/35 W），为 GPU 与共享热管让出热预算；冷却稳定（60 秒低于 78/80 °C）后自动恢复。风扇 profile 档与功耗档解耦（风扇曲线保持让出前档位，绝不套用安静档曲线）。当前为**离线实现，实机 A/B 待验证**
- **声学预算治理器**：请求档与有效档解耦。风扇顶住本档声学软上限且温度持续上升时，有效功耗档保持或降低，而不是继续升档——"提高性能档不等于提高散热档"。软上限不是安全上限：紧急档、快速升温与 RPM 保护可立即突破
- **故障安全监督**：独立看门狗进程在主程序退出、挂起（30 秒心跳超时）或被杀死时恢复 EC 自动控制
- **负载监督链**（离线、负载禁用）：`tools/load-test-supervisor.ps1` + `tools/load-test-worker.ps1` 实现固定负载安全合同（温度前置门禁、窗口内 82 °C 中止、EC 失败中止、未来时间戳 fail-closed、taskkill 进程树优先终止），由 17 项离线 fake 测试验证。load-E 事故后旧 `-FixedLoad` 采样路径保持禁用（证据标记 `INVALID_FOR_MECHANISM_CONCLUSION`）
- **Control Center 接管**：程序短暂停止 CLEVO Control Center 服务/FnKey 后写入功耗上限；租约文件 + 第二个看门狗在退出/睡眠/崩溃时恢复原状
- **安全底线**：三级温度紧急风扇档、CPU RPM 安全保护、传感器无效回退（超出量程的传感器字节如 144 °C 不会触发共享紧急路径）、限速升/降速
- **日志**：CSV 遥测（约 1 Hz，含 `cpu_requested_power_tier` / `cpu_effective_power_tier` / `gpu_thermal_demand` / `shared_thermal_shedding_active` / `cpu_fan_profile_tier` 等列）、异步应用日志（节流 UI 更新）与验证日志

## 项目状态

- ✅ 解决方案在 Windows 上以 .NET Framework 4.8 编译（Release x86 + x64 辅助进程）
- ✅ ReadOnly / Simulation / Active 在目标机上验证（风扇控制、看门狗、睡眠/恢复、托盘退出）
- ✅ CPU 功耗控制、双风扇 Active 控制、看门狗恢复与最终还原在目标机上验证
- ✅ GPU 能力审计完成：温度/利用率/功耗/P-State 遥测可用；本机无受支持的瓦数上限 setter，生产保持 TelemetryOnly
- ✅ 最终界面仅含 **概览 / 策略 / 日志** 三个页面；未经硬件验证的固定占空声学校准页与占位报告已移除
- ✅ 共享热让出进入与 25/35 W DCHU 回读曾实测观察到；该测试**未**证明 GPU 受限游戏场景的温度或噪声改善
- ✅ 共享热预算让出离线实现（仅 Auto 状态机、风扇/功耗解耦）含 13 项单元测试与真实 CSV 离线回放（`tools/SharedThermalReplay`）：2026-08-03 游戏日志中一个激活时段覆盖 767 个样本，全部把 CPU 有效功耗从日常压到安静——回放仅为描述性，不构成改善声明
- ✅ E0 采集合同实现：`X15EcProbe --abort-cpu-temp <C>`（采样内中止，exit 3）与 RPM 合理性列（`IMPLAUSIBLE_RAW_RPM_READ`）；7 项离线 fake telemetry 测试
- ✅ 负载监督链离线测试 17/17（`tools/load-test-supervisor.tests.ps1`）；负载路径保持禁用
- ✅ 夜间档位校准：`代码→日常` 降档阈值 15% → **17%**（2026-08-04 整晚实测空闲基线 CPU 利用率稳定 15.0–15.7%，原 15% 阈值零裕量导致空闲机器整晚卡在代码档）
- ⏳ 硬件验收（E0）**被门禁拦住**：首次 E0 首样本 CPU 82 °C 判 FAIL（`TEMPERATURE_GATE_FAILED / IMMEDIATE_ABORT_NOT_IMPLEMENTED` 已修正——批量采样器现支持采样内中止）。E1+ 等待机器降温后用户批准
- ✅ 桌面体验：最小化到任务栏、关闭进托盘、开机自启、异步日志

## 构建

环境要求：

- Windows 10 或 11
- .NET SDK 8.0 或更高（https://dotnet.microsoft.com/download）
- .NET Framework 4.8 目标包

主要构建命令：

```bat
scripts\build-release-dotnet.bat
```

或手动构建：

```bat
dotnet build .\X15FanControl.sln -c Release -p:Platform=x86
```

可运行产物合并到 `dist\`。运行回归测试：

```bat
tests\X15FanCore.Tests\bin\x86\Release\net48\X15FanCore.Tests.exe
```

### 必需的第三方二进制

本仓库**不包含** `vendor\ClevoEcInfo.dll`。运行前：

1. 在你现有的 Clevo 风扇控制安装目录中找到 `ClevoEcInfo.dll`；
2. 复制到 `vendor\ClevoEcInfo.dll`；
3. 重新运行构建脚本，或手动复制到 `dist\`。

SHA-256 与技术细节见 `vendor\README.md`。

## 安全首次运行

1. 构建并以管理员身份运行 `dist\X15FanControl.exe`；
2. 先停留在 **ReadOnly** 几分钟，确认 CPU/GPU 温度、占空百分比与 RPM 读数合理；
3. 切换到 **Simulation**，确认控制器目标平缓移动、紧急阈值正确；
4. 然后才切换 **Active**，保持"恢复自动"按钮可见，从空闲开始；
5. 测试睡眠/恢复与应用退出，确认原厂自动控制恢复。

详细测试序列见 [docs/SAFE_TEST_PLAN.md](docs/SAFE_TEST_PLAN.md)。

## 使用方法

### 三种模式

| 模式 | 说明 |
|------|------|
| **ReadOnly** | 只读传感器，不写 EC。默认启动模式。 |
| **Simulation** | 控制引擎运行，显示目标值，但不写 EC。 |
| **Active** | 真实 EC 风扇控制，含看门狗、心跳与故障安全 Auto 恢复。 |

### 窗口行为

- **最小化按钮** → 最小化到 Windows 任务栏（不进托盘）
- **关闭 ×** → 隐藏到系统托盘，控制循环继续运行
- **托盘双击 / "打开主窗口"** → 恢复窗口
- **托盘"退出"** → 完整清理并退出（恢复 Auto 与 Control Center、停止看门狗、释放 EC）

### 开机自启

托盘菜单"开机自动启动"创建计划任务（`schtasks /SC ONLOGON /RL HIGHEST`），登录时以 `--autostart --minimized` 启动。保存的 Active 偏好会在 EC、遥测与 Control Center 租约全部通过门禁后自动恢复。

### 功耗策略

策略下拉框包含 **Auto** 与四个固定策略。Auto 模式自动换档；固定模式钉住一档、永不漂移。

| 策略 | PL1/PL2 (W) | 窗口 (s) | CPU 性能上限 | 说明 |
|----------|-------------|------------|---------------------|-------|
| Auto | 自适应 | 28 | 自适应 | 自动档位选择 |
| 安静 | 25 / 35 | 28 | 75% | 最低风扇曲线、热安全门禁 |
| 日常 | 30 / 45 | 28 | 85% | 默认中性档 |
| 代码 | 38 / 55 | 28 | 95% | 交互/编译负载 |
| 重负载 | 55 / 69 | 28 | 100% | 全性能 |

### 自适应档位逻辑

四档（安静 / 日常 / 代码 / 重负载）由 8 秒 CPU 负载证据窗口选择；降档使用 30 秒均值 + 15 秒峰值门禁，升档快、降档刻意放慢以避免抖振。**CPU 请求档只由 CPU 利用率、CPU 温度与持续时间决定**（架构收束 2026-08-02）：GPU 利用率/功耗不再参与 CPU 档位门禁。

| 转换 | 门禁 |
|------------|------|
| 安静 → 日常 | CPU ≥ 12% |
| 日常 → 代码 | CPU ≥ 25% |
| 代码 → 重负载 | CPU ≥ 50% |
| 重负载 → 代码 | 均值 CPU ≤ 25%、峰值 < 60%，驻留 60 s |
| 代码 → 日常 | 均值 CPU ≤ 17%、峰值 < 40%，驻留 120 s |
| 日常 → 安静 | 均值 CPU ≤ 8%、峰值 < 30%，驻留 120 s，加温度门禁（CPU < 85 °C、温升 ≤ 0.5 °C/s） |

**编译快车道**：持续 CPU ≥ 80%（编译/构建负载）可穿透 20 秒最短保持，但每次相邻升档仍需 12 秒强证据驻留；降档驻留从不压缩。GPU 高负载不会提升 CPU 功耗档；GPU 重的游戏场景 CPU 档保持日常，GPU 风扇由自身热需求路径响应。

> 阈值依据目标机 23 小时真实使用轨迹标定（84,139 个 1 秒样本）；2026-08-04 整晚轨迹补充校准了 `代码→日常` 阈值。数据说明见 `src/X15FanCore/Control/AdaptivePowerTier.cs`。

### 声学预算（软上限）

各档每通道有声学参数（舒适占空、软上限风扇占空、目标温度——候选值，未硬件标定）：

| 档位 | 软上限风扇占空（候选） | 目标温度 |
|------|------------------------------|-------------|
| 安静 | ~62% | 85 °C |
| 日常 | ~71% | 88 °C |
| 代码 | ~80% | 88 °C |
| 重负载 | ~88% | 88 °C |

这些是**软**上限，不是安全上限：

- 紧急档、快速升温（> 1 °C/s）与 RPM 保护可立即突破；
- 风扇顶住软上限且温度持续上升达到饱和驻留（默认 20 s）时，有效功耗档保持或降低——机器通过降低 PL1/PL2 降温，而不是抬高风扇天花板；
- 恢复要求温度稳定/下降且风扇低于软上限一定余量并持续 60–120 s，功耗与声学不抖振；
- 安静与性能是物理权衡：相同功耗上限下更安静的风扇曲线意味着更高温度；降低有效功耗档是该权衡的执行方式；
- 功耗档始终来自固定安全预设（安静 25/35 W、日常 30/45 W、代码 38/55 W、重负载 55/69 W），与 X15XtuBridge 白名单共享——不允许写入任意功耗值。

> 声学参数仅为离线验证用初始候选值，**尚未硬件标定**。

### 风扇配置（Profile）

每个策略有对应风扇配置；活动配置随策略档位自动切换（固定策略钉住其配置）：

- **自动** — Auto 入口；风扇行为跟随当前档位
- **安静** — 安静档：平缓曲线、无稳定区、长降速保持（20 s）
- **日常** — 日常档：声学稳定区 45–50%（保持 48%）
- **代码** — 代码档：稳定区 50–55%（保持 53%）、慢速爬升
- **重负载** — 重负载档：激进曲线、提前散热、CPU/GPU 跨风扇协同

### 跨风扇辅助（主扇优先、辅助延迟介入）

CPU 热源默认由 CPU 风扇负责，GPU 热源默认由 GPU 风扇负责。另一侧风扇只在满足全部条件后提供辅助（架构收束 2026-08-02）：

- 主通道温度接近或超过目标温度；
- 主风扇已接近本档声学软上限；
- 温度连续 20 秒没有明显下降（不是短暂尖峰）；
- 辅助量从低值缓慢增加，先限制为主风扇目标的 20%~30% 等效辅助量（候选值，未硬件标定前不宣称最终值）；
- 辅助退出需要温度恢复余量、温升率 ≤ 0、连续 60 秒稳定（滞回避免反复开关）；
- Emergency 与快速温升保护可无条件突破软上限，让两个风扇共同散热；
- GPU 高、CPU 低的游戏场景：GPU 风扇快速响应，CPU 功耗保持自己的请求档，CPU 风扇仅在 GPU 持续压不住时辅助；CPU 高、GPU 低时完全对称。

最终界面有意不暴露任意曲线/功耗/OC/VF/锁频/固定占空校准编辑器。生产行为来自版本化的内置配置与安全合同。

### 共享热预算让出（GPU 过热时 CPU 临时让出热预算）

**Auto 模式专属**。当 GPU 接近热上限、GPU 风扇接近全速且 CPU 同步高热时，持续一段时间后把 CPU 有效功耗临时压到安静档（25/35/28 W），为 GPU 与共享热管让出热预算；冷却稳定后自动恢复。

- **进入条件**（全部成立并连续累计 20 s）：Auto 模式；CPU/GPU 温度遥测有效（各自 profile min/max 校验）；GPU ≥ 84 °C；CPU ≥ 85 °C（**平坦 87 °C 也计入**，不要求继续升温）；GPU 实际风扇占空 ≥ 95%。GPU 后端是否可写不参与判断（生产恒 TelemetryOnly）。
- **激活后**：CPU 有效功耗档至多安静档；已是安静档保持；不允许任何升档；Emergency 仍可双风扇突破到 100%。
- **风扇/功耗解耦**：CPU 功耗可降为安静档，但风扇 profile 保持进入前档位（至少不低，见 CSV `cpu_fan_profile_tier`）——绝不因 CPU 降到安静档而套用安静档风扇曲线。
- **恢复**（连续满足 60 s，任一条件失效即清零）：GPU ≤ 78 °C 且 CPU ≤ 80 °C 且遥测有效；恢复后回到正常治理链输出，不直接强升功耗。
- **时间连续性**：采样时间戳积分；gap > 3 s 不补算并废弃进入/恢复信用；时间戳倒退不累计；短尖峰/间断脉冲不拼接；激活后遥测丢失保持较低 CPU 功耗等待有效恢复证据。
- 阈值均为候选值（未硬件标定）；实现见 `src/X15FanCore/Control/SharedThermalBudgetController.cs`，离线测试 13 项。**尚未实机 A/B 验证，不构成任何温降/噪声/帧率改善声明。**

### E0 采集合同（X15EcProbe 采样内中止与 RPM 合理性）

`src/X15EcProbe`（只读 EC 采样器，白名单调用）支持：

- `--abort-cpu-temp <C>`：每样本读取后先写入并 flush 当前样本，CPU 温度 ≥ C 时**本轮立即退出**（exit 3，输出 `ABORT_CPU_TEMP observed=… threshold=…`），不继续 sleep 或读下一个样本；默认不传保持旧行为。E0 硬性门禁使用 `--abort-cpu-temp 70`；
- CSV 增加 `cpu_rpm_plausible / gpu_rpm_plausible` 列（RPM > 10000，或 Duty ≥ 20% 且 RPM < 200 → `IMPLAUSIBLE_RAW_RPM_READ / ROOT_CAUSE_UNKNOWN`；仅 E0 数据质量检查，不进入生产风扇算法）；
- 逻辑与测试共享 `src/X15FanCore/Probe/EcProbeContract.cs`（fake telemetry 测试 7 项）。

实机验收流程（E0–E5，分门禁、每阶段停止等待批准）见 `docs/HARDWARE_ACCEPTANCE_PLAN.md`；离线架构收束与安全合同总结见 `docs/ARCHITECTURE_CONSOLIDATION_20260802.md`。

### 负载监督链（离线）

固定负载路径当前**禁用**（load-E 事故后 `-FixedLoad` 拒绝执行，证据标记 `INVALID_FOR_MECHANISM_CONCLUSION`）。监督链本身已完成并离线验证：

- `tools/load-test-supervisor.ps1`：独立监督器（状态文件驱动、mode flag 带 nonce/UTC 校验、心跳先于 worker、taskkill 进程树优先 + Stop-Process 回退 + 终止验证、未来时间戳 fail-closed、预热门禁 >70 °C 彻底清零重新累计）；
- `tools/load-test-worker.ps1`：独立 worker（监督器心跳过期自退；`-IdleMode` 供离线测试）；
- `tools/load-test-supervisor.tests.ps1`：17 项离线集成测试（fake 进程/临时目录，无负载、无硬件）。

### 写入验证与外部覆盖检测

Active 模式每次 EC 占空写入后异步回读验证：50 ms 回读确认写入生效，1000 ms 回读确认持续生效。与最后写入占空连续三次失配即确认**外部覆盖**（其他控制器争抢 EC 寄存器）；程序恢复 Auto、降回 ReadOnly 并记录事件。回读值与覆盖状态显示在仪表盘并写入 CSV 日志。

### Control Center 接管

Active 模式下程序短暂停止 `CCDCHUService` / `XTU3SERVICE` 与 FnKey 进程，然后通过已安装的 CLEVO Control Center SDK 写入 CPU 功耗上限（回读确认 + 回滚）。接管记录在租约文件中；退出、睡眠或崩溃时由专用租约看门狗恢复原服务与 FnKey 状态。

## 数据位置

应用写入：

```text
%LOCALAPPDATA%\X15FanControl\
```

包含 `config.json`、心跳状态、CSV 日志、应用日志、看门狗日志与 Control Center 租约文件。CSV 日志默认保留 7 天。

## 架构

- **X15FanControl** — WinForms GUI（x86）：概览、内置策略说明、日志、托盘与控制循环
- **X15FanCore** — 核心库：EC 封装、风扇曲线、温度滤波、控制引擎、自适应功耗档、跨风扇辅助、共享热预算让出、声学治理器、心跳、租约、遥测客户端
- **X15FanWatchdog** — 独立进程：主程序退出或停止心跳时恢复风扇 Auto（租约模式下同时恢复 Control Center）
- **X15GpuTelemetry** — x64 辅助进程：运行 nvidia-smi 并经 stdout 输出 JSON 遥测（`taskkill /T` 进程树清理）
- **X15XtuBridge** — 短生命周期桥：Control Center / Intel XTU 功耗写入，带范围校验与回读验证
- **X15EcProbe** — 只读 EC 采样器（温度/RPM/占空；`--abort-cpu-temp` 采样内中止、`--status-file` 供监督链使用）
- **X15DchuProbe** — 只读 DCHU AppSettings 探针（OEM 模式字节、CPU PL1/PL2/Tau、GPU OC 存储值）
- **X15GpuPowerProbe** — 只读 NVML 诊断探针（生产路径从不写 GPU 功耗）
- **tools/** — `static_sanity_check.py` 守卫、负载监督链（离线）、`SharedThermalReplay` CSV 回放工具

## 文档

- `docs/ALGORITHM.md` — 控制算法说明
- `docs/ARCHITECTURE_CONSOLIDATION_20260802.md` — 架构收束与离线交付记录
- `docs/CONTROL_CENTER_CPU_GPU_MECHANISM_AUDIT.md` — Control Center 机制审计（含 load-E 事故记录）
- `docs/CPU_GPU_POWER_CAPABILITY_AUDIT.md` — CPU/GPU 功耗能力审计（历史原型章节已标注）
- `docs/HARDWARE_ACCEPTANCE_PLAN.md` — 分门禁的 E0–E5 实机验收计划（尚未执行）

## 许可证

本仓库的源代码、脚本与文档以 MIT 许可证发布，详见 `LICENSE`。

**MIT 许可证不覆盖：**
- `ClevoEcInfo.dll` — 第三方二进制，不随本仓库分发
- NTPort 驱动 — 第三方组件
- NVIDIA nvidia-smi — 第三方工具
