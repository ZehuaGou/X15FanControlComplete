using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    /// <summary>
    /// 声学/热治理器：把"负载请求档位"与"声学/热预算允许档位"解耦。
    ///
    /// 原则：
    /// - 负载要求升档，但风扇已接近声学软上限且温度仍上升时，不得仅因为
    ///   热或风扇高而提高 EffectivePowerTier；应保持或降低，并标记
    ///   "声学/热余量限制"。
    /// - 紧急散热（EmergencyStage、快速升温、RPM 保护）可以立即突破风扇
    ///   软上限（由 ChannelController 独立执行），但功耗档位不得因此升高。
    /// - 功耗档位始终来自 AdaptivePowerPresets 安全白名单。
    /// - 热饱和证据使用积分（credit），不跨墙钟间隔累计：只有每个采样
    ///   真正处于饱和贡献状态才累计，中间的非饱和间隔会快速衰减，恢复
    ///   到 Normal 时清零。
    /// </summary>
    public sealed class AcousticGovernor
    {
        // 热饱和判定通过后，再持续该时长仍饱和才主动降一个有效功耗档。
        private const int SaturationDowngradeExtraSeconds = 30;
        // 连续采样最大间隔：超过视为采样中断（睡眠/控制循环停顿），
        // 不得把间隔时间补算进饱和积分（覆盖正常最大 2 秒轮询）。
        private const double MaxContinuousSampleGapSeconds = 3.0;

        private readonly AdaptivePowerSettings _settings;
        private CoolingState _state = CoolingState.Normal;
        // 饱和证据积分（秒），CPU/GPU 通道独立：只有同一通道"风扇接近软上限
        // + 温度接近目标 + 未明显下降"且该通道实际升温才累计；一个通道的
        // 风扇到顶不能与另一个通道的高温/升温拼接，闲置通道的下降/低风扇
        // 也不能清除另一通道的真实证据。
        private double _cpuSaturationCreditSeconds;
        private double _gpuSaturationCreditSeconds;
        private DateTime? _recoverySinceUtc;
        private AdaptivePowerTier _lastEffective = AdaptivePowerTier.Daily;
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private string _reason = string.Empty;
        // 受限期间已明确接受过更低请求后，Heavy 不得在同一热饱和
        // 周期内立即回弹；必须完成冷却恢复后才重新开放性能逃生。
        private bool _heavyEscapeBlockedUntilRecovery;

        public AcousticGovernor(AdaptivePowerSettings settings)
        {
            _settings = settings ?? new AdaptivePowerSettings();
        }

        public CoolingState State { get { return _state; } }
        public string Reason { get { return _reason ?? string.Empty; } }
        public AdaptivePowerTier EffectiveTier { get { return _lastEffective; } }
        public double SaturationCreditSeconds
        {
            get { return Math.Max(_cpuSaturationCreditSeconds, _gpuSaturationCreditSeconds); }
        }

        // 逐通道热饱和判定（供 PlatformPowerCoordinator.CoolingStateInput
        // 使用）：CPU/GPU 通道各自独立，不以 max 耦合。
        public bool CpuSaturated
        {
            get { return _cpuSaturationCreditSeconds >= _settings.ThermalSaturationDwellSeconds; }
        }
        public bool GpuSaturated
        {
            get { return _gpuSaturationCreditSeconds >= _settings.ThermalSaturationDwellSeconds; }
        }

        // 每通道冷却状态（架构收束 2026-08-02 命名：cpu_cooling_state /
        // gpu_cooling_state）：紧急时双侧均为 Emergency；否则按各自饱和
        // 积分推导（ThermalSaturation / NearAcousticLimit / Normal）。
        public CoolingState CpuCoolingState
        {
            get
            {
                if (_state == CoolingState.Emergency)
                    return CoolingState.Emergency;
                if (_cpuSaturationCreditSeconds >= _settings.ThermalSaturationDwellSeconds)
                    return CoolingState.ThermalSaturation;
                if (_cpuSaturationCreditSeconds > 0)
                    return CoolingState.NearAcousticLimit;
                return CoolingState.Normal;
            }
        }
        public CoolingState GpuCoolingState
        {
            get
            {
                if (_state == CoolingState.Emergency)
                    return CoolingState.Emergency;
                if (_gpuSaturationCreditSeconds >= _settings.ThermalSaturationDwellSeconds)
                    return CoolingState.ThermalSaturation;
                if (_gpuSaturationCreditSeconds > 0)
                    return CoolingState.NearAcousticLimit;
                return CoolingState.Normal;
            }
        }

        public void Reset(AdaptivePowerTier tier)
        {
            _state = CoolingState.Normal;
            _cpuSaturationCreditSeconds = 0;
            _gpuSaturationCreditSeconds = 0;
            _recoverySinceUtc = null;
            _lastEffective = tier;
            _reason = string.Empty;
            _heavyEscapeBlockedUntilRecovery = false;
        }

        public AdaptivePowerTier Apply(
            AdaptivePowerTier requestedTier,
            double cpuTemperatureC,
            double gpuTemperatureC,
            double cpuFanDutyPercent,
            double gpuFanDutyPercent,
            double cpuRiseRateCPerSec,
            double gpuRiseRateCPerSec,
            ChannelAcousticLimits cpuLimits,
            ChannelAcousticLimits gpuLimits,
            bool emergency,
            DateTime nowUtc)
        {
            // 采样间隔与连续性：超过连续阈值（睡眠/控制循环停顿）不补算
            // 积分，衰减全部饱和证据并重置恢复计时；本帧按单个采样计。
            double gapSeconds = _lastSampleUtc == DateTime.MinValue
                ? 1.0
                : Math.Max(0, (nowUtc - _lastSampleUtc).TotalSeconds);
            bool gapInterrupted = gapSeconds > MaxContinuousSampleGapSeconds;
            double elapsedSeconds;
            if (gapInterrupted)
            {
                _cpuSaturationCreditSeconds = Math.Max(0,
                    _cpuSaturationCreditSeconds - 2.0 * gapSeconds);
                _gpuSaturationCreditSeconds = Math.Max(0,
                    _gpuSaturationCreditSeconds - 2.0 * gapSeconds);
                _recoverySinceUtc = null;
                elapsedSeconds = 1.0;   // 不把无采样间隔补算成真实证据
            }
            else
            {
                elapsedSeconds = Math.Max(0.05, Math.Min(10.0, gapSeconds));
            }
            _lastSampleUtc = nowUtc;

            // 请求档位低于当前有效档：任何状态（含受限/紧急）都立即接受并
            // 同步 _lastEffective。功耗接受降档，但紧急状态诊断必须保留。
            if (requestedTier < _lastEffective)
            {
                if (_state != CoolingState.Normal || SaturationCreditSeconds > 0)
                    _heavyEscapeBlockedUntilRecovery = true;
                _lastEffective = requestedTier;
                if (emergency)
                {
                    _state = CoolingState.Emergency;
                    _reason = "紧急状态：功耗接受降档，冷却状态标记紧急";
                }
                else
                {
                    _reason = "负载回落，有效档跟随降低";
                }
                return requestedTier;
            }

            // 紧急：风扇由紧急路径独立突破；功耗不升档（保持当前有效档）。
            if (emergency)
            {
                _state = CoolingState.Emergency;
                _reason = "紧急状态，有效功耗不升档";
                _cpuSaturationCreditSeconds = 0;
                _gpuSaturationCreditSeconds = 0;
                _recoverySinceUtc = null;
                return _lastEffective;
            }

            ChannelAcousticLimits cpu = cpuLimits ?? DefaultLimits;
            ChannelAcousticLimits gpu = gpuLimits ?? DefaultLimits;

            // 每通道：是否接近软上限、温度是否接近/超过目标、温升方向。
            // 温升率带噪声容忍：轻微平坦/负值（> -0.1°C/s）仍视为上升，
            // 只有明显下降（< -0.3°C/s）视为真正回落。
            bool cpuAtLimit = cpuFanDutyPercent >= cpu.SoftMaximumFanDutyPercent - 2.0;
            bool gpuAtLimit = gpuFanDutyPercent >= gpu.SoftMaximumFanDutyPercent - 2.0;
            bool cpuHot = cpuTemperatureC >= cpu.TargetTemperatureC - 2.0;
            bool gpuHot = gpuTemperatureC >= gpu.TargetTemperatureC - 2.0;
            // 真正上升：> 0.05°C/s 才累计饱和积分；平坦/轻微负值按噪声慢衰减。
            bool cpuActuallyRising = cpuRiseRateCPerSec > 0.05;
            bool gpuActuallyRising = gpuRiseRateCPerSec > 0.05;
            bool cpuClearlyFalling = cpuRiseRateCPerSec < -0.3;
            bool gpuClearlyFalling = gpuRiseRateCPerSec < -0.3;
            bool cpuContributing = cpuAtLimit && cpuHot && !cpuClearlyFalling;
            bool gpuContributing = gpuAtLimit && gpuHot && !gpuClearlyFalling;
            // 明显低于软上限 / 温度有明显余量（通道独立，快速衰减用）。
            bool cpuHeadroom = cpuFanDutyPercent <= cpu.SoftMaximumFanDutyPercent - 5.0;
            bool cpuTempHeadroom = cpuTemperatureC <= cpu.TargetTemperatureC - 5.0;
            bool gpuHeadroom = gpuFanDutyPercent <= gpu.SoftMaximumFanDutyPercent - 5.0;
            bool gpuTempHeadroom = gpuTemperatureC <= gpu.TargetTemperatureC - 5.0;

            // 通道独立饱和证据积分：只有同一通道 contributing 且该通道实际
            // 升温才 +1x；该通道平坦时 -0.5x 慢衰减；该通道明显下降/低风扇/
            // 温度有余量时 -2x 快衰减。跨通道不拼接、不抵消。
            if (cpuContributing)
            {
                if (cpuActuallyRising)
                    _cpuSaturationCreditSeconds += elapsedSeconds;
                else
                    _cpuSaturationCreditSeconds = Math.Max(0,
                        _cpuSaturationCreditSeconds - 0.5 * elapsedSeconds);
            }
            else if (cpuClearlyFalling || cpuHeadroom || cpuTempHeadroom)
            {
                _cpuSaturationCreditSeconds = Math.Max(0,
                    _cpuSaturationCreditSeconds - 2.0 * elapsedSeconds);
            }

            if (gpuContributing)
            {
                if (gpuActuallyRising)
                    _gpuSaturationCreditSeconds += elapsedSeconds;
                else
                    _gpuSaturationCreditSeconds = Math.Max(0,
                        _gpuSaturationCreditSeconds - 0.5 * elapsedSeconds);
            }
            else if (gpuClearlyFalling || gpuHeadroom || gpuTempHeadroom)
            {
                _gpuSaturationCreditSeconds = Math.Max(0,
                    _gpuSaturationCreditSeconds - 2.0 * elapsedSeconds);
            }

            double saturationSeconds = SaturationCreditSeconds;
            bool saturated = saturationSeconds >= _settings.ThermalSaturationDwellSeconds;

            // 恢复条件：温度未明显上升（容忍平坦/轻微波动）、风扇低于软上限
            // 足够余量。明显上升(> 0.3°C/s)才阻止恢复。
            bool cpuClearlyRising = cpuRiseRateCPerSec > 0.3;
            bool gpuClearlyRising = gpuRiseRateCPerSec > 0.3;
            bool cooled = !cpuClearlyRising && !gpuClearlyRising &&
                          cpuFanDutyPercent <= cpu.SoftMaximumFanDutyPercent - _settings.RecoveryMarginPercent &&
                          gpuFanDutyPercent <= gpu.SoftMaximumFanDutyPercent - _settings.RecoveryMarginPercent;

            // Heavy 是经过持续负载证据才能进入的性能逃生通道。日常/代码
            // 的 69% 声学预算不得把真实持续强负载永久锁在低功耗档。
            // Emergency 已在上方先行处理，仍保持“紧急时不升功耗”；
            // SharedThermalShedding 仍由整机协调器在后续覆盖。
            if (requestedTier == AdaptivePowerTier.Heavy && !_heavyEscapeBlockedUntilRecovery)
            {
                _lastEffective = AdaptivePowerTier.Heavy;
                if (saturated)
                {
                    _state = CoolingState.ThermalSaturation;
                    _reason = "持续重负载：性能优先，允许进入 Heavy 并使用其独立声学预算";
                }
                else if (saturationSeconds > 0)
                {
                    _state = CoolingState.NearAcousticLimit;
                    _reason = "持续重负载：性能优先，从日常/代码声学限制中逃生";
                }
                else
                {
                    _state = CoolingState.Normal;
                    _reason = "持续重负载：已进入性能优先档";
                }
                return _lastEffective;
            }

            if (saturated)
            {
                _recoverySinceUtc = null;
                _state = CoolingState.ThermalSaturation;
                if (requestedTier > _lastEffective)
                {
                    _reason = "声学/热余量限制：风扇顶住软上限且温度持续上升，禁止提高有效功耗";
                    return _lastEffective;
                }

                // 热饱和积分持续足够久且有效档高于最低：降低一个有效功耗档
                // 帮助降温（风扇安全响应独立工作，不受影响）。
                if (saturationSeconds >= _settings.ThermalSaturationDwellSeconds + SaturationDowngradeExtraSeconds &&
                    _lastEffective != AdaptivePowerTier.Quiet)
                {
                    AdaptivePowerTier lowered = LowerTier(_lastEffective);
                    _lastEffective = lowered;
                    _reason = "热饱和：主动降低一个有效功耗档至" + TierName(lowered);
                    return lowered;
                }
                _reason = "热饱和：保持当前有效功耗";
                return _lastEffective;
            }

            if (saturationSeconds > 0)
            {
                _state = CoolingState.NearAcousticLimit;
                _reason = "接近声学上限：风扇顶住软上限且温度上升";
                // 受限且冷却中：恢复计时同步累计（不受 credit 衰减期影响）。
                if (UpdateRecovery(nowUtc, cooled))
                {
                    _lastEffective = requestedTier;
                    return requestedTier;
                }
                return requestedTier > _lastEffective ? _lastEffective : requestedTier;
            }

            // 无饱和证据：受限状态必须满足恢复条件并持续 RecoveryDwellSeconds
            // 才恢复；恢复到 Normal 时清零全部饱和证据。
            if (_state == CoolingState.ThermalSaturation ||
                _state == CoolingState.NearAcousticLimit ||
                _state == CoolingState.Emergency)
            {
                if (UpdateRecovery(nowUtc, cooled))
                {
                    _lastEffective = requestedTier;
                    return requestedTier;
                }
                return _lastEffective;
            }

            // 正常：有效档跟随请求档位。
            _state = CoolingState.Normal;
            _reason = string.Empty;
            _lastEffective = requestedTier;
            return requestedTier;
        }

        // 受限状态下的恢复计时：冷却条件满足时累计，满 RecoveryDwellSeconds
        // 后恢复 Normal 并清零全部饱和证据。
        private bool UpdateRecovery(DateTime nowUtc, bool cooled)
        {
            if (!cooled)
            {
                _recoverySinceUtc = null;
                return false;
            }

            if (!_recoverySinceUtc.HasValue)
                _recoverySinceUtc = nowUtc;
            if ((nowUtc - _recoverySinceUtc.Value).TotalSeconds >= _settings.RecoveryDwellSeconds)
            {
                _state = CoolingState.Normal;
                _recoverySinceUtc = null;
                _cpuSaturationCreditSeconds = 0;
                _gpuSaturationCreditSeconds = 0;
                _heavyEscapeBlockedUntilRecovery = false;
                _reason = "声学/热恢复：温度稳定且风扇低于软上限，恢复正常";
                return true;
            }
            _reason = "恢复中：温度稳定下降，等待恢复时长";
            return false;
        }

        public static ChannelAcousticLimits DefaultLimits
        {
            get
            {
                return new ChannelAcousticLimits
                {
                    ComfortFanDutyPercent = 50,
                    SoftMaximumFanDutyPercent = 100,
                    TargetTemperatureC = 88
                };
            }
        }

        private static AdaptivePowerTier LowerTier(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Heavy: return AdaptivePowerTier.Code;
                case AdaptivePowerTier.Code: return AdaptivePowerTier.Daily;
                case AdaptivePowerTier.Daily: return AdaptivePowerTier.Quiet;
                default: return AdaptivePowerTier.Quiet;
            }
        }

        private static string TierName(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Quiet: return "安静";
                case AdaptivePowerTier.Code: return "代码";
                case AdaptivePowerTier.Heavy: return "重负载";
                default: return "日常";
            }
        }
    }

    /// <summary>单通道声学预算参数（软上限，非安全上限）。</summary>
    public sealed class ChannelAcousticLimits
    {
        public double ComfortFanDutyPercent { get; set; }
        public double SoftMaximumFanDutyPercent { get; set; }
        public int TargetTemperatureC { get; set; }
    }
}
