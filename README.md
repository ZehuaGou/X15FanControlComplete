# X15 风扇控制（X15 Fan Control）

面向 **COLORFUL X15 AT 23 / Clevo NP50SNE** 的安全优先 x86 Windows 风扇控制器：温度滤波、非对称升降速、声学稳定区、写入回读验证与外部覆盖检测、自适应 CPU 功耗策略、GPU 只读遥测、Control Center 接管与租约恢复，以及独立看门狗。

> ⚠ **警告**
> - 仅在本机型开发与测试，其他 Clevo 机型不保证可用。
> - 切换 Active 前必须完成 ReadOnly 与 Simulation 验证；切勿同时运行两个风扇控制程序；硬件风险自负。

## 功能

- **三种模式**：ReadOnly（只读）/ Simulation（引擎运行不写 EC）/ Active（真实控制）
- **写入验证**：每次占空写入后回读确认，连续三次失配判定外部覆盖并自动降回 ReadOnly
- **自适应功耗策略**：四档（安静/日常/代码/重负载）由 CPU 负载自动选择，也可固定；GPU 负载不参与 CPU 档位
- **共享热预算让出**（Auto）：GPU 接近热上限且 CPU 同步高热时，CPU 有效功耗临时压到安静档，冷却后恢复（离线实现，实机待验证）
- **声学治理**：风扇顶住本档软上限且温度持续上升时保持或降低有效功耗档，而非继续升档
- **故障安全**：独立看门狗在主程序退出/挂起/被杀时恢复 EC 自动控制
- **Control Center 接管**：短暂停止其服务后写功耗上限，租约 + 看门狗在退出/睡眠/崩溃时恢复
- **GPU 只读**：GPU 功耗后端恒为 TelemetryOnly，不写 GPU 功耗/OC/频率/VF
- **安全底线**：三级温度紧急档、RPM 保护、传感器无效回退、限速升降速
- **日志**：CSV 遥测（约 1 Hz）与应用日志，保留 7 天

## 项目状态

- ✅ .NET Framework 4.8 编译（Release x86 + x64 辅助进程）；ReadOnly/Simulation/Active、功耗控制、双风扇、看门狗恢复均已在目标机验证
- ✅ GPU 能力审计完成：生产保持 TelemetryOnly
- ✅ 共享热让出、E0 采集合同均为离线实现并通过离线测试
- ⏳ 硬件验收（E0–E5）尚未通过（E0 因 CPU 温度门禁判 FAIL，E1+ 等待批准）
- ✅ 桌面体验：最小化进任务栏、关闭进托盘、开机自启

## 构建

- 环境：Windows 10/11、.NET SDK 8.0+、.NET Framework 4.8 目标包
- 命令：`dotnet build .\X15FanControl.sln -c Release -p:Platform=x86`
- 产物合并到 `dist\`
- `ClevoEcInfo.dll` 为第三方二进制，不随仓库分发：从现有 Clevo 风扇控制安装目录复制到 `vendor\` 后重新构建（SHA-256 见 `vendor\README.md`）

## 安全首次运行

1. 以管理员运行 `dist\X15FanControl.exe`
2. 先 **ReadOnly** 观察读数合理，再 **Simulation** 确认目标平缓移动，最后才 **Active** 从空闲开始
3. 测试睡眠/恢复与退出，确认原厂自动控制恢复

## 使用

- **模式**：ReadOnly / Simulation / Active（默认 ReadOnly）
- **策略**：Auto + 固定策略（安静 25/35 W、日常 30/45 W、代码 38/55 W、重负载 55/69 W）
- **窗口**：最小化进任务栏、关闭进托盘、托盘"退出"完整清理
- **开机自启**：托盘菜单创建计划任务，登录时 `--autostart --minimized` 启动
- **数据位置**：`%LOCALAPPDATA%\X15FanControl\`（config.json、CSV 日志、看门狗日志、租约文件）

## 架构

- **X15FanControl** — WinForms GUI（x86）：概览、策略、日志、托盘
- **X15FanCore** — 核心库：EC 封装、控制引擎、功耗策略、声学治理、心跳、租约
- **X15FanWatchdog** — 独立进程：主程序退出/心跳停止时恢复风扇 Auto 与 Control Center
- **X15GpuTelemetry** — x64 辅助：nvidia-smi 遥测（JSON stdout）
- **X15XtuBridge** — 功耗桥：DCHU 白名单写入 + 回读验证
- **X15EcProbe / X15DchuProbe / X15GpuPowerProbe** — 只读探针（采样、OEM 字节、NVML 诊断）

## 文档

- `docs/ALGORITHM.md` — 控制算法

## 许可证

MIT（详见 `LICENSE`）。**不覆盖**：`ClevoEcInfo.dll`、NTPort 驱动、nvidia-smi（第三方组件）。
