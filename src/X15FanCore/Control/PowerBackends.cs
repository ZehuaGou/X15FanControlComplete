using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    // =====================================================================
    // CPU/GPU 联合功耗控制后端接口与实现（离线原型）。
    //
    // 状态：仅离线原型。真实 NVML shim、运行时接入、GPU 档位标定均未完成；
    // 任何 GPU Set 路径默认 disabled，必须经过实机验收门禁。
    // =====================================================================

    public enum BackendCapability
    {
        ConfirmedReadOnly = 0,
        ConfirmedWritableFromExistingEvidence = 1,
        WritableButPersistenceUnknown = 2,
        ApiExistsButDeviceSupportUnknown = 3,
        Unsupported = 4,
        UnsafeOrRejected = 5
    }

    public sealed class BackendProbeResult
    {
        public bool Available;
        public BackendCapability Capability;
        public string Detail;
    }

    public enum ApplyOutcome
    {
        FailedNoHardwareChange = 0,           // 拒绝于 Set 之前，未触碰硬件
        FailedSetRejectedConfirmedUnchanged = 1, // Set 被拒绝且读回确认硬件未变
        FailedRestoredConfirmed = 2,          // Set 后失败，恢复基线已读回确认
        FailedRestoreUnconfirmed = 3,         // Set 后失败，恢复无法确认，硬件状态未知
        Applied = 4
    }

    public sealed class BackendApplyResult
    {
        public bool Applied;
        public bool ReadbackMatched;
        // 默认即失败：所有成功路径必须显式设置 Applied；所有失败路径必须
        // 显式设置准确 Outcome。
        public ApplyOutcome Outcome = ApplyOutcome.FailedNoHardwareChange;
        public AutoRestoreResult Recovery;    // Set 后触发自动恢复时的结构化结果
        public string Detail;
    }

    // 自动恢复的结构化结果。
    public sealed class AutoRestoreResult
    {
        public bool RestoreAttempted;
        public bool RestoreSetSucceeded;
        public bool RestoreReadbackSucceeded;
        public bool RestoreConfirmed;              // Set 成功且读回与基线一致
        public int FinalObservedLimitMilliWatts;   // 恢复后观察到的实际值
        public string Detail;
    }

    public static class PowerBackendConstants
    {
        // 持久性检查间隔（供调用方按时间调度；当前未接入运行时）。
        public const int PersistenceCheckIntervalSeconds = 30;
        // 读回容差：±1W（1000 mW）。
        public const int ReadbackToleranceMilliWatts = 1000;
    }

    public interface ICpuPowerBackend
    {
        string Name { get; }
        BackendProbeResult ProbeCapabilities();
        BackendApplyResult CaptureBaseline();
        BackendApplyResult ApplyPreset(AdaptivePowerTier tier);
        BackendApplyResult ReadBack();
        BackendApplyResult CheckCurrentReadback();
        BackendApplyResult RestoreBaseline();
    }

    public interface IGpuPowerBackend
    {
        string Name { get; }
        bool IsEnabledForWrites { get; }        // 实机验收前必须 false
        string LimitType { get; }               // "power-watts" / "clock-cap" / "telemetry-only"
        BackendProbeResult ProbeCapabilities();
        BackendApplyResult CaptureBaseline();
        // 显式瓦数上限（mW）：不得假装四档已标定；未捕获基线/未授权/超
        // 基线/超设备范围一律拒绝。
        BackendApplyResult ApplyLimitWatts(int limitMilliWatts);
        BackendApplyResult ReadBack();
        // 仅当前读回；长期覆盖检测由调用方按 PersistenceCheckIntervalSeconds
        // 调度，未接入运行时前不得声称已实现长期检测。
        BackendApplyResult CheckCurrentReadback();
        BackendApplyResult RestoreBaseline();
    }

    // ---------------------------------------------------------------------
    // CPU DCHU shim：隔离具体 DCHU 反射后端，便于单元测试注入 fake。
    // ---------------------------------------------------------------------
    public interface IDchuPowerShim
    {
        ControlCenterDchuProbeResult ProbePowerLimits();
        ControlCenterDchuPowerApplyResult ApplyCpuPowerLimits(decimal pl1Watts, decimal pl2Watts, uint timeSeconds);
    }

    public sealed class DchuPowerShim : IDchuPowerShim
    {
        private readonly ControlCenterDchuPowerBackend _dchu = new ControlCenterDchuPowerBackend();

        public ControlCenterDchuProbeResult ProbePowerLimits()
        {
            return _dchu.ProbePowerLimits();
        }

        public ControlCenterDchuPowerApplyResult ApplyCpuPowerLimits(decimal pl1Watts, decimal pl2Watts, uint timeSeconds)
        {
            return _dchu.ApplyCpuPowerLimits(pl1Watts, pl2Watts, timeSeconds);
        }
    }

    // ---------------------------------------------------------------------
    // CPU：包装现有 ControlCenterDchuPowerBackend（可注入 shim）。
    // ---------------------------------------------------------------------
    public sealed class CpuDchuPowerBackend : ICpuPowerBackend
    {
        private readonly IDchuPowerShim _shim;
        private int _baselinePl1;
        private int _baselinePl2;
        private float _baselineTime;
        private int _lastExpectedPl1 = -1;
        private int _lastExpectedPl2 = -1;
        private float _lastExpectedTime = -1;
        private bool _hasBaseline;

        public CpuDchuPowerBackend()
            : this(new DchuPowerShim())
        {
        }

        public CpuDchuPowerBackend(IDchuPowerShim shim)
        {
            _shim = shim ?? throw new ArgumentNullException("shim");
        }

        public string Name { get { return "ControlCenter-DCHU-CPU"; } }

        public BackendProbeResult ProbeCapabilities()
        {
            ControlCenterDchuProbeResult probe = _shim.ProbePowerLimits();
            if (!probe.Available)
            {
                return new BackendProbeResult
                {
                    Available = false,
                    Capability = BackendCapability.ConfirmedReadOnly,
                    Detail = probe.Error ?? "DCHU 不可用"
                };
            }
            return new BackendProbeResult
            {
                Available = true,
                Capability = BackendCapability.ConfirmedWritableFromExistingEvidence,
                Detail = string.Format("DCHU 可读写 PL1={0}W PL2={1}W Tau={2}s，写后读回验证已启用",
                    probe.Pl1Watts, probe.Pl2Watts, probe.TimeSeconds)
            };
        }

        public BackendApplyResult CaptureBaseline()
        {
            // 第一步：使旧 baseline 与 expected 立即失效。
            _hasBaseline = false;
            _baselinePl1 = 0;
            _baselinePl2 = 0;
            _baselineTime = 0;
            _lastExpectedPl1 = -1;
            _lastExpectedPl2 = -1;
            _lastExpectedTime = -1;

            ControlCenterDchuProbeResult probe = _shim.ProbePowerLimits();
            if (!probe.Available)
                return Fail("无法读取当前 DCHU 功耗基线：" + probe.Error);
            if (probe.Pl1Watts <= 0)
                return Fail("DCHU 基线非法（PL1 必须 > 0）");
            if (probe.Pl2Watts < probe.Pl1Watts)
                return Fail("DCHU 基线非法（PL2 必须 >= PL1）");
            if (probe.TimeSeconds <= 0)
                return Fail("DCHU 基线非法（Tau 必须 > 0）");

            // 全部校验成功：最后提交新基线并初始化 expected。
            _baselinePl1 = probe.Pl1Watts;
            _baselinePl2 = probe.Pl2Watts;
            _baselineTime = probe.TimeSeconds;
            _lastExpectedPl1 = _baselinePl1;
            _lastExpectedPl2 = _baselinePl2;
            _lastExpectedTime = _baselineTime;
            _hasBaseline = true;
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = true,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("已捕获基线 PL1={0} PL2={1} Tau={2}；lastExpected 已初始化",
                    _baselinePl1, _baselinePl2, _baselineTime)
            };
        }

        public BackendApplyResult ApplyPreset(AdaptivePowerTier tier)
        {
            if (!_hasBaseline)
                return Fail("未捕获基线，拒绝 Apply");
            AdaptivePowerPreset preset = AdaptivePowerPreset.For(tier);
            ControlCenterDchuPowerApplyResult result = _shim.ApplyCpuPowerLimits(
                preset.Pl1Watts, preset.Pl2Watts, preset.TimeSeconds);
            if (!result.Applied)
                return Fail(result.Error ?? "DCHU 应用失败");
            _lastExpectedPl1 = result.AppliedPl1Watts;
            _lastExpectedPl2 = result.AppliedPl2Watts;
            _lastExpectedTime = result.AppliedTimeSeconds;
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = true,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("DCHU 应用并回读确认 PL1={0} PL2={1} Tau={2}；lastExpected 已更新",
                    _lastExpectedPl1, _lastExpectedPl2, _lastExpectedTime)
            };
        }

        public BackendApplyResult ReadBack()
        {
            if (!_hasBaseline || _lastExpectedPl1 < 0)
                return Fail("无 lastExpected 状态，拒绝读回比较");
            ControlCenterDchuProbeResult probe = _shim.ProbePowerLimits();
            if (!probe.Available)
                return Fail("DCHU 读回失败：" + probe.Error);
            bool matched = Math.Abs(probe.Pl1Watts - _lastExpectedPl1) <= 1 &&
                           Math.Abs(probe.Pl2Watts - _lastExpectedPl2) <= 1 &&
                           probe.TimeSeconds == _lastExpectedTime;
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = matched,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("读回 PL1={0} PL2={1} Tau={2}（期望 {3}/{4}/{5}）",
                    probe.Pl1Watts, probe.Pl2Watts, probe.TimeSeconds,
                    _lastExpectedPl1, _lastExpectedPl2, _lastExpectedTime)
            };
        }

        public BackendApplyResult CheckCurrentReadback()
        {
            BackendApplyResult readback = ReadBack();
            if (readback.Detail.StartsWith("无 lastExpected", StringComparison.Ordinal))
                return readback;
            if (!readback.ReadbackMatched)
            {
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Detail = "CPU 限值当前读回与期望不一致（可能被 Control Center/DTT/XTU 覆盖）"
                };
            }
            return readback;
        }

        public BackendApplyResult RestoreBaseline()
        {
            if (!_hasBaseline)
                return Fail("未捕获基线，拒绝恢复");
            ControlCenterDchuPowerApplyResult result = _shim.ApplyCpuPowerLimits(
                _baselinePl1, _baselinePl2, (uint)_baselineTime);
            if (!result.Applied)
                return Fail(result.Error ?? "恢复失败");
            _lastExpectedPl1 = _baselinePl1;
            _lastExpectedPl2 = _baselinePl2;
            _lastExpectedTime = _baselineTime;
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = true,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("已恢复基线 PL1={0} PL2={1} Tau={2}；lastExpected 已更新",
                    _baselinePl1, _baselinePl2, _baselineTime)
            };
        }

        private static BackendApplyResult Fail(string detail)
        {
            return new BackendApplyResult { Applied = false, ReadbackMatched = false, Outcome = ApplyOutcome.FailedNoHardwareChange, Detail = detail };
        }
    }

    // ---------------------------------------------------------------------
    // 生产 GPU 功耗后端工厂（架构收束 2026-08-02）：
    // 生产路径固定返回 TelemetryOnlyGpuPowerBackend（零 Set）。真实 NVML
    // 后端（NvidiaNvmlPowerLimitBackend）与 X15GpuPowerProbe 保留为诊断
    // 代码，但生产 MainForm 不得通过任何途径实例化为可写后端；本工厂是
    // MainForm 获取 GPU 后端的唯一入口。
    // ---------------------------------------------------------------------
    public static class ProductionGpuBackendFactory
    {
        public static IGpuPowerBackend Create()
        {
            // 固定 TelemetryOnly：本机 GPU 生产路径不控制瓦数（审计确认无
            // W 级 setter 证据 + NVML 写入未验收）。任何 Set 调用数为 0。
            return new TelemetryOnlyGpuPowerBackend();
        }
    }

    // ---------------------------------------------------------------------
    // GPU：默认遥测后端。只读，永不调用任何 Set。
    // ---------------------------------------------------------------------
    public sealed class TelemetryOnlyGpuPowerBackend : IGpuPowerBackend
    {
        public string Name { get { return "GPU-Telemetry-Only"; } }
        public bool IsEnabledForWrites { get { return false; } }
        public string LimitType { get { return "telemetry-only"; } }

        public BackendProbeResult ProbeCapabilities()
        {
            return new BackendProbeResult
            {
                Available = true,
                Capability = BackendCapability.ConfirmedReadOnly,
                Detail = "仅遥测：GPU 功耗/频率只读监测，不执行任何写入（安全默认）"
            };
        }

        public BackendApplyResult CaptureBaseline()
        {
            return new BackendApplyResult { Applied = true, ReadbackMatched = true, Outcome = ApplyOutcome.Applied, Detail = "遥测模式无基线需要捕获" };
        }

        public BackendApplyResult ApplyLimitWatts(int limitMilliWatts)
        {
            return new BackendApplyResult
            {
                Applied = false,
                ReadbackMatched = false,
                Detail = "telemetry-only 后端拒绝任何写入（实机验收前默认禁用）"
            };
        }

        public BackendApplyResult ReadBack()
        {
            return new BackendApplyResult { Applied = true, ReadbackMatched = true, Outcome = ApplyOutcome.Applied, Detail = "遥测模式无写入值可读回" };
        }

        public BackendApplyResult CheckCurrentReadback()
        {
            return new BackendApplyResult { Applied = true, ReadbackMatched = true, Outcome = ApplyOutcome.Applied, Detail = "遥测模式无持久性需要检查" };
        }

        public BackendApplyResult RestoreBaseline()
        {
            return new BackendApplyResult { Applied = true, ReadbackMatched = true, Outcome = ApplyOutcome.Applied, Detail = "遥测模式无基线需要恢复" };
        }
    }

    // ---------------------------------------------------------------------
    // NVML shim：隔离 nvmlDeviceGet/SetPowerManagementLimit；单元测试注入
    // fake shim；真实实现 NvmlShim 直接调用本机 nvml.dll（默认写禁用）。
    // 接口 INvmlShim 与返回码常量 NvmlReturnCodes 定义于 NvmlShim.cs
    // （自包含，便于 x64 验收工具直接链接源码，因本机 nvml.dll 仅 64 位）。
    //
    // 返回码契约：所有方法返回原始 nvmlReturn_t（0 = NVML_SUCCESS）。调用
    // 方必须通过 LastNvmlReturnCode 与 NvmlReturnCodes.Describe 原样输出，
    // 不得把 NOT_SUPPORTED / NO_PERMISSION / GPU_IS_LOST / UNKNOWN 混成
    // 普通失败。
    // ---------------------------------------------------------------------

    public sealed class NvidiaNvmlPowerLimitBackend : IGpuPowerBackend
    {
        private readonly INvmlShim _shim;
        private readonly bool _hardwareAcceptanceGranted;
        private bool _writeBlockedByProbeFailure;
        private bool _permanentFault;          // 恢复无法确认后的永久故障闩锁

        // GPU 三类状态（禁止共用一个 baseline）：
        // - OriginalOemBaseline：首次可信验收捕获的 OEM 恢复目标，只能通过
        //   构造参数传入可信快照建立；CaptureBaseline 永不建立/覆盖它，
        //   防止把较低残留值当成新的 OEM 原始基线。
        // - RevalidationBaseline：最近一次成功捕获（重新验证）得到的控制上限；
        //   ApplyLimitWatts 只允许保持或向下。
        // - CurrentExpected：每次事务写入前的已确认硬件状态；ApplyLimitWatts
        //   失败时回滚到事务写入前的 CurrentExpected，而不是 RevalidationBaseline。
        private int _originalOemBaselineLimitMilliWatts;
        private string _originalOemBaselineIdentity;
        private bool _hasOriginalOemBaseline;
        private bool _originalOemSnapshotValidated;   // 快照通过 identity/范围/default 三项验证
        private int _revalidationBaselineLimitMilliWatts;
        private int _minLimitMilliWatts;
        private int _maxLimitMilliWatts;
        private string _gpuIdentity;
        private int _lastAppliedLimitMilliWatts;   // CurrentExpected
        private bool _hasBaseline;

        // 可信原始基线快照：由调用方在验收时提供（值 + identity）。未提供
        // 快照时写保持禁用——重建实例不得把当前残留值自动认作 OEM 基线。
        // 提供快照本身并不立即令其可信：identity 必须非空，且必须等
        // CaptureBaseline 完成 identity/范围/default 三项验证后才置为可信。
        public NvidiaNvmlPowerLimitBackend(
            INvmlShim shim,
            bool hardwareAcceptanceGranted,
            int? originalOemBaselineMilliWatts = null,
            string originalOemIdentity = null)
        {
            _shim = shim ?? throw new ArgumentNullException("shim");
            _hardwareAcceptanceGranted = hardwareAcceptanceGranted;
            if (originalOemBaselineMilliWatts.HasValue &&
                originalOemBaselineMilliWatts.Value > 0 &&
                !string.IsNullOrEmpty(originalOemIdentity))
            {
                _originalOemBaselineLimitMilliWatts = originalOemBaselineMilliWatts.Value;
                _originalOemBaselineIdentity = originalOemIdentity;
                _hasOriginalOemBaseline = true;
            }
            // _originalOemSnapshotValidated 保持 false：快照未经 Capture
            // 验证前一律不可信，写保持禁用（fail-closed）。
        }

        public string Name { get { return "NVML-PowerLimit"; } }
        public bool IsEnabledForWrites
        {
            get
            {
                // 语义与 WriteAuthorized 完全一致：授权 + shim 可用 + 有效
                // 基线 + 可信原始 OEM 快照（已通过 Capture 验证） + 无
                // probe/capture 阻断 + 无永久故障。
                return _hardwareAcceptanceGranted && _shim.IsAvailable() && _hasBaseline &&
                       _hasOriginalOemBaseline && _originalOemSnapshotValidated &&
                       !_writeBlockedByProbeFailure && !_permanentFault;
            }
        }
        public string LimitType { get { return "power-watts"; } }
        public bool HasPermanentFault { get { return _permanentFault; } }
        public int OriginalOemBaselineLimitMilliWatts { get { return _originalOemBaselineLimitMilliWatts; } }
        public bool HasOriginalOemBaseline { get { return _hasOriginalOemBaseline; } }
        public bool OriginalOemSnapshotValidated { get { return _originalOemSnapshotValidated; } }
        public int RevalidationBaselineLimitMilliWatts { get { return _revalidationBaselineLimitMilliWatts; } }

        // 不可绕过的硬件验收门禁：所有 Set 路径的唯一授权入口。
        private bool WriteAuthorized()
        {
            return IsEnabledForWrites;
        }

        // 永久故障后没有任何解除入口：必须销毁本实例并以新的用户授权
        // 构造新实例（原始基线重新捕获）。普通 Probe/Capture 成功不会
        // 自动解除 fault。

        // ==================== 共享只读实时设备状态 ====================
        // 唯一实时读取入口：ProbeCapabilities / CaptureBaseline /
        // ApplyLimitWatts 预检 / RestoreBaseline 全部经此读取，避免四套
        // 验证语义漂移。任一 Get 失败 → 返回 false（调用方各自 fail-closed）。

        // 追加原始 nvmlReturn_t 到失败描述（NOT_SUPPORTED / NO_PERMISSION /
        // GPU_IS_LOST / UNKNOWN 必须原样输出，不得混成普通失败）。
        private string NvmlReturnDetail(string context)
        {
            return string.Format("{0}（nvmlReturn={1}）", context,
                NvmlReturnCodes.Describe(_shim.LastNvmlReturnCode));
        }

        // 实时读取设备状态（零 Set）。所有输出参数在失败时无意义。
        private bool TryReadDeviceState(
            out int current, out int defaultLimit, out int min, out int max, out string identity)
        {
            current = 0; defaultLimit = 0; min = 0; max = 0; identity = null;
            if (!_shim.IsAvailable())
                return false;
            if (_shim.GetPowerManagementLimit(out current) != 0)
                return false;
            if (_shim.GetDefaultPowerManagementLimit(out defaultLimit) != 0)
                return false;
            if (_shim.GetMinPowerManagementLimit(out min) != 0)
                return false;
            if (_shim.GetMaxPowerManagementLimit(out max) != 0)
                return false;
            if (_shim.GetGpuIdentity(out identity) != 0)
                return false;
            return true;
        }

        // OEM 快照可信验证（共享）：identity 完全一致 + OEM ∈ [min,max] +
        // OEM 与 NVML default 在读回容差内一致。无快照时返回 false。
        private bool ValidateOemSnapshot(string currentIdentity, int currentDefaultLimit, int currentMin, int currentMax)
        {
            if (!_hasOriginalOemBaseline)
                return false;
            if (string.IsNullOrEmpty(_originalOemBaselineIdentity))
                return false;
            if (!string.Equals(currentIdentity, _originalOemBaselineIdentity, StringComparison.Ordinal))
                return false;
            if (_originalOemBaselineLimitMilliWatts < currentMin || _originalOemBaselineLimitMilliWatts > currentMax)
                return false;
            return Match(_originalOemBaselineLimitMilliWatts, currentDefaultLimit);
        }

        public BackendProbeResult ProbeCapabilities()
        {
            if (!_shim.IsAvailable())
            {
                return new BackendProbeResult
                {
                    Available = false,
                    Capability = BackendCapability.ApiExistsButDeviceSupportUnknown,
                    Detail = "NVML 不可用：" + _shim.InitDetail + "；实机写入无证据"
                };
            }
            // 任一 Get 失败 → 永久阻断写入；普通 Probe 成功不会自动解除，
            // 永久故障只能通过销毁后端并以新的用户授权重建实例来解除。
            int current, defaultLimit, min, max;
            string identity;
            if (!TryReadDeviceState(out current, out defaultLimit, out min, out max, out identity))
            {
                _writeBlockedByProbeFailure = true;
                return new BackendProbeResult
                {
                    Available = false,
                    Capability = BackendCapability.ApiExistsButDeviceSupportUnknown,
                    Detail = NvmlReturnDetail("NVML 部分只读查询失败，写入已永久阻断（需显式重新验收解除）")
                };
            }
            // 实时快照验证失败时不得报告可写：设备状态在 Capture 后可能
            // 已改变（default/范围/identity），必须基于本次实时读取判断。
            bool writable = IsEnabledForWrites && ValidateOemSnapshot(identity, defaultLimit, min, max);
            string mode = writable ? "已授权写入（实机验收后）" : "默认禁用写入";
            return new BackendProbeResult
            {
                Available = true,
                Capability = writable
                    ? BackendCapability.WritableButPersistenceUnknown
                    : BackendCapability.ConfirmedReadOnly,
                Detail = string.Format("NVML 只读：current={0}W default={1}W min={2}W max={3}W identity={4}；{5}",
                    current / 1000, defaultLimit / 1000, min / 1000, max / 1000, identity, mode)
            };
        }

        // 事务化 CaptureBaseline（重新验证）：先使旧 RevalidationBaseline
        // 与 CurrentExpected 失效，全部读取与校验成功后才提交新基线；任一
        // 步失败 → 无基线 + 写入阻断，绝不继续使用旧基线。
        // 注意：本方法只更新 RevalidationBaseline 与 CurrentExpected，
        // 永不建立/覆盖 OriginalOemBaseline（后者只能来自构造参数的可信快照）。
        // 本方法同时重新验证 OEM 快照：identity 完全一致 + OEM ∈ [min,max]
        // + OEM 与 NVML default 在读回容差内一致。任一失败 → 快照不可信，
        // 写保持禁用（fail-closed）。Capture 的只读结果仍可返回。
        public BackendApplyResult CaptureBaseline()
        {
            // 第一步：使旧基线立即失效并阻断写入；OEM 快照验证状态同时失效，
            // 必须基于本次读取的新设备状态重新验证。
            _hasBaseline = false;
            _revalidationBaselineLimitMilliWatts = 0;
            _lastAppliedLimitMilliWatts = 0;
            _originalOemSnapshotValidated = false;

            if (!_shim.IsAvailable())
            {
                _writeBlockedByProbeFailure = true;
                return Fail("NVML 不可用，基线未提交，写入已阻断");
            }
            int current, defaultLimit, min, max;
            string identity;
            if (!TryReadDeviceState(out current, out defaultLimit, out min, out max, out identity))
                return CaptureFailed(NvmlReturnDetail("实时读取设备状态失败"));
            if (string.IsNullOrEmpty(identity))
                return CaptureFailed("GPU identity 为空");
            if (min <= 0)
                return CaptureFailed("设备 min 非法（<= 0）");
            if (current < min || current > max)
                return CaptureFailed(string.Format("基线超出设备范围：{0}W 不在 [{1}W, {2}W]", current / 1000, min / 1000, max / 1000));
            if (defaultLimit <= 0)
                return CaptureFailed("设备 default 非法（<= 0）");

            // 全部成功：最后一步提交新 RevalidationBaseline 并初始化
            // CurrentExpected。OriginalOemBaseline 保持不变（若构造时未
            // 提供可信快照，则本实例永远没有 OEM 恢复目标，写保持禁用）。
            _revalidationBaselineLimitMilliWatts = current;
            _minLimitMilliWatts = min;
            _maxLimitMilliWatts = max;
            _gpuIdentity = identity;
            _lastAppliedLimitMilliWatts = current;
            _hasBaseline = true;

            // 重新验证 OEM 快照（基于本次读取的设备状态）。
            _originalOemSnapshotValidated = ValidateOemSnapshot(identity, defaultLimit, min, max);
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = true,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("已提交重新验证基线 {0}W（范围 {1}-{2}W，identity={3}；可信 OEM 原始基线 {4}W，快照验证 {5}）",
                    current / 1000, min / 1000, max / 1000, identity,
                    _hasOriginalOemBaseline ? _originalOemBaselineLimitMilliWatts / 1000 : 0,
                    _originalOemSnapshotValidated ? "通过" : "未通过（写禁用）")
            };
        }

        private BackendApplyResult CaptureFailed(string detail)
        {
            _writeBlockedByProbeFailure = true;
            return new BackendApplyResult
            {
                Applied = false,
                ReadbackMatched = false,
                Outcome = ApplyOutcome.FailedNoHardwareChange,
                Detail = "基线捕获失败（旧基线已失效，写入已阻断）：" + detail
            };
        }

        // 显式瓦数上限（mW）完整事务：授权 → 统一的 Set 前实时预检（零 Set）
        // → Set → 立即 Get → 比较 → 不一致自动回滚到事务写入前的
        // CurrentExpected → 再次读回确认。
        public BackendApplyResult ApplyLimitWatts(int limitMilliWatts)
        {
            if (!WriteAuthorized())
            {
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedNoHardwareChange,
                    Detail = "NVML 写入未获硬件验收授权、无可信 OEM 基线、基线未捕获或已故障闩锁，拒绝 Set"
                };
            }

            // 统一的 Set 前实时预检（零 Set）：基于本次实时读取的设备状态
            // 而非 Capture 时缓存；任一失败 → 零 Set 拒绝，要求重新
            // Capture/Revalidation，不得静默更新 CurrentExpected。
            PreflightState preflight = PreflightBeforeSet(limitMilliWatts);
            if (!preflight.Passed)
            {
                return FailNoChange("Set 前实时预检失败：" + preflight.FailureDetail +
                    "；零 Set 拒绝，请重新 Capture/Revalidation");
            }

            if (_shim.SetPowerManagementLimit(limitMilliWatts) != 0)
            {
                // Set 被拒绝：立即读取当前值判断硬件是否变化。
                int observed;
                if (_shim.GetPowerManagementLimit(out observed) == 0)
                {
                    if (Match(observed, _lastAppliedLimitMilliWatts))
                    {
                        // 硬件未变（与写入前 expected 一致）：明确拒绝结果。
                        return new BackendApplyResult
                        {
                            Applied = false,
                            ReadbackMatched = true,
                            Outcome = ApplyOutcome.FailedSetRejectedConfirmedUnchanged,
                            Detail = NvmlReturnDetail(string.Format(
                                "Set 被拒绝且读回确认硬件未变（当前 {0}W）", observed / 1000))
                        };
                    }
                    // 硬件已变化：回滚到事务写入前的 CurrentExpected。
                    AutoRestoreResult restore = TryRollbackToCurrentExpected();
                    return RestoreOutcome("Set 被拒绝且硬件已变化，回滚到事务前状态", restore);
                }
                // 无法读取：回滚无法确认，置永久故障。
                _permanentFault = true;
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedRestoreUnconfirmed,
                    Detail = NvmlReturnDetail("Set 被拒绝且读回失败，硬件状态未知（已置永久故障闩锁）")
                };
            }

            int readback;
            if (_shim.GetPowerManagementLimit(out readback) != 0)
            {
                AutoRestoreResult restore = TryRollbackToCurrentExpected();
                return RestoreOutcome(NvmlReturnDetail("Set 后立即读回失败"), restore);
            }
            if (!Match(readback, limitMilliWatts))
            {
                AutoRestoreResult restore = TryRollbackToCurrentExpected();
                return RestoreOutcome(string.Format("读回 {0}W 与请求 {1}W 不一致", readback / 1000, limitMilliWatts / 1000), restore);
            }

            _lastAppliedLimitMilliWatts = readback;
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = true,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("GPU 功耗上限应用并读回确认 {0}W", readback / 1000)
            };
        }

        // Set 前实时预检结果。
        private sealed class PreflightState
        {
            public bool Passed;
            public int Current;          // 实时 current
            public int DefaultLimit;     // 实时 default
            public int Min;              // 实时 min
            public int Max;              // 实时 max
            public string Identity;      // 实时 identity
            public string FailureDetail;
        }

        // 统一的 Set 前实时预检（零 Set，仅读取）：所有 Get 成功，且
        // identity / OEM 快照范围 / OEM 与 default / 请求范围 / 请求上限 /
        // current 与 CurrentExpected 一致性 全部满足。任一失败 → Passed=false
        // 且 FailureDetail 说明原因；不修改任何状态。
        private PreflightState PreflightBeforeSet(int requestedMilliWatts)
        {
            PreflightState state = new PreflightState { Passed = false };
            int current, defaultLimit, min, max;
            string identity;
            if (!TryReadDeviceState(out current, out defaultLimit, out min, out max, out identity))
            {
                state.FailureDetail = NvmlReturnDetail("实时读取设备状态失败");
                return state;
            }
            state.Current = current;
            state.DefaultLimit = defaultLimit;
            state.Min = min;
            state.Max = max;
            state.Identity = identity;

            // 当前 identity 与 OriginalOemBaseline identity、RevalidationBaseline identity 一致。
            if (!string.Equals(identity, _gpuIdentity, StringComparison.Ordinal))
            {
                state.FailureDetail = "当前 identity 与 RevalidationBaseline identity 不一致";
                return state;
            }
            if (!string.Equals(identity, _originalOemBaselineIdentity, StringComparison.Ordinal))
            {
                state.FailureDetail = "当前 identity 与 OriginalOemBaseline identity 不一致";
                return state;
            }
            // OEM snapshot 仍位于实时 [min,max] 且与实时 NVML default 容差一致。
            if (!ValidateOemSnapshot(identity, defaultLimit, min, max))
            {
                state.FailureDetail = "OEM 快照实时验证失败（范围或 default 已改变）";
                return state;
            }
            // 请求值位于实时 [min,max]。
            if (requestedMilliWatts < min || requestedMilliWatts > max)
            {
                state.FailureDetail = string.Format("请求 {0}W 超出实时设备范围 [{1}W, {2}W]",
                    requestedMilliWatts / 1000, min / 1000, max / 1000);
                return state;
            }
            // 请求值不高于 RevalidationBaseline。
            if (requestedMilliWatts > _revalidationBaselineLimitMilliWatts)
            {
                state.FailureDetail = string.Format("请求 {0}W 高于最近验证基线 {1}W，只允许保持或向下限制",
                    requestedMilliWatts / 1000, _revalidationBaselineLimitMilliWatts / 1000);
                return state;
            }
            // Set 前读取的 current 与 CurrentExpected 容差一致（外部覆盖检测）。
            if (!Match(current, _lastAppliedLimitMilliWatts))
            {
                state.FailureDetail = string.Format("Set 前 current {0}W 与 CurrentExpected {1}W 不一致（可能被外部覆盖）",
                    current / 1000, _lastAppliedLimitMilliWatts / 1000);
                return state;
            }

            state.Passed = true;
            return state;
        }

        // 回滚到事务写入前的 CurrentExpected（结构化结果）：Set 回滚目标 →
        // 立即 Get → 与回滚目标比较确认。
        private AutoRestoreResult TryRollbackToCurrentExpected()
        {
            AutoRestoreResult result = new AutoRestoreResult { RestoreAttempted = true, Detail = "回滚到事务前状态" };
            if (_lastAppliedLimitMilliWatts <= 0)
            {
                result.Detail = "回滚失败：无有效事务前状态";
                _permanentFault = true;
                return result;
            }
            int rollbackTarget = _lastAppliedLimitMilliWatts;

            if (_shim.SetPowerManagementLimit(rollbackTarget) != 0)
            {
                result.Detail = NvmlReturnDetail("回滚 Set 失败，硬件状态未知");
                _permanentFault = true;
                return result;
            }
            result.RestoreSetSucceeded = true;

            int readback;
            if (_shim.GetPowerManagementLimit(out readback) != 0)
            {
                result.Detail = NvmlReturnDetail("回滚读回失败，硬件状态未知");
                _permanentFault = true;
                return result;
            }
            result.RestoreReadbackSucceeded = true;
            result.FinalObservedLimitMilliWatts = readback;

            if (Match(readback, rollbackTarget))
            {
                result.RestoreConfirmed = true;
                result.Detail = string.Format("回滚已确认：{0}W（事务前状态）", readback / 1000);
                _lastAppliedLimitMilliWatts = readback;
            }
            else
            {
                result.Detail = string.Format("回滚读回 {0}W 与事务前状态 {1}W 不一致，硬件状态未知",
                    readback / 1000, rollbackTarget / 1000);
                _permanentFault = true;
            }
            return result;
        }

        // 把失败 + 自动恢复结果组合成 BackendApplyResult，并正确区分
        // outcome；完整恢复结果通过 Recovery 字段传播给调用者。
        private BackendApplyResult RestoreOutcome(string failureDetail, AutoRestoreResult restore)
        {
            if (restore.RestoreConfirmed)
            {
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedRestoredConfirmed,
                    Recovery = restore,
                    Detail = failureDetail + "；" + restore.Detail
                };
            }
            return new BackendApplyResult
            {
                Applied = false,
                ReadbackMatched = false,
                Outcome = ApplyOutcome.FailedRestoreUnconfirmed,
                Recovery = restore,
                Detail = failureDetail + "；" + restore.Detail + "（已置永久故障闩锁，后续 Set 拒绝）"
            };
        }

        private static BackendApplyResult FailNoChange(string detail)
        {
            return new BackendApplyResult
            {
                Applied = false,
                ReadbackMatched = false,
                Outcome = ApplyOutcome.FailedNoHardwareChange,
                Detail = detail
            };
        }

        public BackendApplyResult ReadBack()
        {
            if (!_hasBaseline)
                return Fail("未捕获基线");
            int current;
            if (_shim.GetPowerManagementLimit(out current) != 0)
                return Fail(NvmlReturnDetail("读回 GPU 功耗上限失败"));
            bool matched = Match(current, _lastAppliedLimitMilliWatts);
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = matched,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("读回 GPU 功耗上限 {0}W（期望 {1}W）{2}",
                    current / 1000, _lastAppliedLimitMilliWatts / 1000, matched ? "" : "——不一致")
            };
        }

        // 仅当前读回；长期覆盖检测由调用方按 PersistenceCheckIntervalSeconds
        // 调度（未接入运行时）。
        public BackendApplyResult CheckCurrentReadback()
        {
            BackendApplyResult readback = ReadBack();
            if (!readback.ReadbackMatched)
            {
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Detail = "GPU 功耗上限当前读回与期望不一致（可能被 Dynamic Boost/驱动重置覆盖）"
                };
            }
            return readback;
        }

        // 显式退出/恢复事务：必须恢复可信的 OriginalOemBaseline（而不是
        // 最近一次重新验证的 RevalidationBaseline）。每次 Set 前实时重新
        // 验证 identity/min/max/default（不依赖 Capture 时缓存的设备状态），
        // 任何不一致均零 Set 拒绝（fail-closed）。
        // 恢复无法确认时置永久故障闩锁。
        public BackendApplyResult RestoreBaseline()
        {
            if (!WriteAuthorized())
            {
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedNoHardwareChange,
                    Detail = "NVML 写入未获硬件验收授权、OEM 快照未验证、基线未捕获或已故障闩锁，拒绝恢复"
                };
            }
            if (!_hasOriginalOemBaseline)
                return FailNoChange("无可信原始 OEM 基线快照，拒绝恢复");

            // 实时重新验证（Set 前）：共享 TryReadDeviceState 读取
            // identity / default / min / max / current，与快照不一致 →
            // 零 Set 拒绝（fail-closed）。
            int liveCurrent, liveDefault, liveMin, liveMax;
            string liveIdentity;
            if (!TryReadDeviceState(out liveCurrent, out liveDefault, out liveMin, out liveMax, out liveIdentity))
                return FailNoChange(NvmlReturnDetail("恢复前实时读取设备状态失败，拒绝恢复"));
            if (!ValidateOemSnapshot(liveIdentity, liveDefault, liveMin, liveMax))
                return FailNoChange("恢复前重新验证失败：identity/范围/default 与 OEM 快照不一致，拒绝恢复");

            if (_shim.SetPowerManagementLimit(_originalOemBaselineLimitMilliWatts) != 0)
            {
                _permanentFault = true;
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedRestoreUnconfirmed,
                    Detail = NvmlReturnDetail("恢复 Set 失败，硬件状态未知（已置永久故障闩锁）")
                };
            }

            int readback;
            if (_shim.GetPowerManagementLimit(out readback) != 0)
            {
                _permanentFault = true;
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedRestoreUnconfirmed,
                    Detail = NvmlReturnDetail("恢复后读回失败，硬件状态未知（已置永久故障闩锁）")
                };
            }
            if (!Match(readback, _originalOemBaselineLimitMilliWatts))
            {
                _permanentFault = true;
                return new BackendApplyResult
                {
                    Applied = false,
                    ReadbackMatched = false,
                    Outcome = ApplyOutcome.FailedRestoreUnconfirmed,
                    Detail = string.Format("恢复读回 {0}W 与原始 OEM 基线 {1}W 不一致（已置永久故障闩锁）",
                        readback / 1000, _originalOemBaselineLimitMilliWatts / 1000)
                };
            }

            // 恢复成功后：CurrentExpected 回到 OEM 原始值；RevalidationBaseline
            // 也随显式恢复更新（硬件已确认为 OEM 状态）。
            _lastAppliedLimitMilliWatts = readback;
            _revalidationBaselineLimitMilliWatts = readback;
            return new BackendApplyResult
            {
                Applied = true,
                ReadbackMatched = true,
                Outcome = ApplyOutcome.Applied,
                Detail = string.Format("已恢复原始 OEM 功耗上限 {0}W 并读回确认", readback / 1000)
            };
        }

        private static bool Match(int readbackMilliWatts, int expectedMilliWatts)
        {
            return Math.Abs(readbackMilliWatts - expectedMilliWatts) <= PowerBackendConstants.ReadbackToleranceMilliWatts;
        }

        private static BackendApplyResult Fail(string detail)
        {
            return new BackendApplyResult { Applied = false, ReadbackMatched = false, Outcome = ApplyOutcome.FailedNoHardwareChange, Detail = detail };
        }

        // 只读实时诊断（零 Set，不修改任何状态）：供 UI/日志输出 GPU
        // current/default/min/max/identity。任一 Get 失败返回 false。
        public bool ProbeLimitsForDiagnostics(
            out int currentMilliWatts,
            out int defaultLimitMilliWatts,
            out int minLimitMilliWatts,
            out int maxLimitMilliWatts,
            out string identity)
        {
            currentMilliWatts = 0;
            defaultLimitMilliWatts = 0;
            minLimitMilliWatts = 0;
            maxLimitMilliWatts = 0;
            identity = null;
            if (_shim == null)
                return false;
            int current, defaultLimit, min, max;
            if (!TryReadDeviceState(out current, out defaultLimit, out min, out max, out identity))
                return false;
            currentMilliWatts = current;
            defaultLimitMilliWatts = defaultLimit;
            minLimitMilliWatts = min;
            maxLimitMilliWatts = max;
            return true;
        }
    }
}
