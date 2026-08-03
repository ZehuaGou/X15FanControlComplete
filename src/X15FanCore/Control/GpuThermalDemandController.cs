using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    /// <summary>
    /// GPU 热需求等级（架构收束 2026-08-02）。
    ///
    /// 取代旧 GpuTierController（GPU 功耗档位语义已被删除）：GPU 没有
    /// 功率档位控制——生产路径 GPU 功耗后端固定为 TelemetryOnly，MainForm
    /// 不得实例化可写 NVML 后端，也不存在 GPU 瓦数预设映射。
    ///
    /// 需求等级只根据：GPU 利用率、GPU 实际功耗遥测、GPU 温度、持续
    /// 时间和滞回。输出只作用于：
    ///   - GPU 风扇曲线偏置（DemandBiasPercent，候选值未标定）；
    ///   - 跨风扇辅助判定（由 CrossFanAssistController 消费）；
    ///   - 日志/UI 诊断（CSV gpu_thermal_demand 列）。
    /// 不得映射到虚构的 GPU 瓦数预设。
    ///
    /// 安全规则（与旧实现一致的安全语义）：
    /// - 升档使用独立 upshift credit（起始帧计 0，后续 +elapsed）。
    /// - 降档使用独立 downshift credit。
    /// - 采样间隔 > 3s 视为中断，不补算墙钟时间，证据衰减。
    /// - 时间戳倒退不累计证据。
    /// - 遥测持续丢失超过 TTL 后只能保持或降级到 Low（不能无限保持 High）。
    /// </summary>
    public enum GpuThermalDemand
    {
        Low = 0,
        Moderate = 1,
        High = 2
    }

    public sealed class GpuThermalDemandController
    {
        // 候选阈值（未硬件标定）：高需求 = 利用率 + 功耗 + 温度组合。
        public const double HighUtilizationPercent = 60;
        public const double HighPowerWatts = 60;
        public const double HighMinimumTemperatureC = 55;
        public const double ModerateUtilizationPercent = 35;
        public const double ModeratePowerWatts = 35;
        public const double HighCancelUtilizationPercent = 45;
        public const double HighCancelPowerWatts = 45;
        // 驻留时长（候选）：升档需持续；降档需稳定回落。
        public const double ModerateDwellSeconds = 30;
        public const double HighDwellSeconds = 30;
        public const double HighToModerateDwellSeconds = 60;
        public const double ModerateToLowDwellSeconds = 120;
        // 连续采样最大间隔（秒）：超过视为中断。
        public const double MaxContinuousSampleGapSeconds = 3.0;
        // 遥测丢失 TTL（秒）：持续丢失超过该时长后禁止继续升档/保持 High。
        public const double TelemetryLossTtlSeconds = 30.0;

        // 需求 → GPU 风扇曲线偏置（候选值，未标定；不得宣称最终值）。
        public const double ModerateFanBiasPercent = 5;
        public const double HighFanBiasPercent = 10;

        private GpuThermalDemand _currentDemand = GpuThermalDemand.Low;
        private double _upshiftCreditSeconds;
        private double _strongCreditSeconds;
        private double _downshiftCreditSeconds;
        private bool _lastStrongEnter;
        private bool _lastModerateEnter;
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private DateTime _lastLossSampleUtc = DateTime.MinValue;
        private double _telemetryLostSeconds;
        private string _lastReason = "GPU 初始为低需求";

        public GpuThermalDemand CurrentDemand { get { return _currentDemand; } }
        public string LastReason { get { return _lastReason ?? string.Empty; } }
        public bool TelemetryAvailable { get; private set; }

        /// <summary>需求 → GPU 风扇曲线偏置（候选值；供 FanControlEngine 加到
        /// GPU 通道目标，受声学软上限约束，Emergency 可突破）。</summary>
        public double CurrentFanBiasPercent
        {
            get
            {
                switch (_currentDemand)
                {
                    case GpuThermalDemand.High: return HighFanBiasPercent;
                    case GpuThermalDemand.Moderate: return ModerateFanBiasPercent;
                    default: return 0;
                }
            }
        }

        public void ForceDemand(GpuThermalDemand demand, string reason)
        {
            _currentDemand = demand;
            _upshiftCreditSeconds = 0;
            _strongCreditSeconds = 0;
            _downshiftCreditSeconds = 0;
            _lastStrongEnter = false;
            _lastModerateEnter = false;
            _lastSampleUtc = DateTime.MinValue;
            _lastLossSampleUtc = DateTime.MinValue;
            _telemetryLostSeconds = 0;
            _lastReason = reason ?? "GPU 固定需求";
        }

        public GpuThermalDemand Update(
            double gpuUtilizationPercent,
            double gpuPowerWatts,
            double gpuTemperatureC,
            bool gpuTelemetryAvailable,
            DateTime timestampUtc)
        {
            if (!gpuTelemetryAvailable || gpuTemperatureC <= 0)
            {
                // 遥测丢失/无效：
                // - 第一个 unavailable 样本只建立丢失游标，积分为 0；
                // - 后续间隔 <= MaxContinuousSampleGapSeconds 才累计实际 gap；
                // - 间隔 > 3s 不补算整段墙钟时间（重置游标到当前）；
                // - 时间戳未前进/倒退不累计。
                DateTime lostNow = timestampUtc == default(DateTime) ? DateTime.UtcNow : timestampUtc;
                if (_lastLossSampleUtc == DateTime.MinValue)
                {
                    _lastLossSampleUtc = lostNow;
                }
                else if (lostNow > _lastLossSampleUtc)
                {
                    double gap = (lostNow - _lastLossSampleUtc).TotalSeconds;
                    if (gap <= MaxContinuousSampleGapSeconds)
                    {
                        _telemetryLostSeconds += gap;
                        _lastLossSampleUtc = lostNow;
                    }
                    else
                    {
                        _telemetryLostSeconds = 0;
                        _lastLossSampleUtc = lostNow;
                    }
                }
                if (_telemetryLostSeconds >= TelemetryLossTtlSeconds && _currentDemand != GpuThermalDemand.Low)
                {
                    _currentDemand = GpuThermalDemand.Low;
                    _upshiftCreditSeconds = 0;
                    _strongCreditSeconds = 0;
                    _downshiftCreditSeconds = 0;
                    _lastReason = "GPU 遥测持续丢失超过 TTL，需求降级至 Low";
                }
                else
                {
                    _lastReason = "GPU 遥测不可用，保持当前需求";
                }
                TelemetryAvailable = false;
                return _currentDemand;
            }
            TelemetryAvailable = true;

            // 正常样本：只有通过时间连续性验证（时间戳严格前进）才清零
            // 丢失积分；时间戳倒退的 available 样本不得清除 loss credit。
            DateTime availableNow = timestampUtc == default(DateTime) ? DateTime.UtcNow : timestampUtc;
            if (_lastSampleUtc == DateTime.MinValue || availableNow > _lastSampleUtc)
            {
                _telemetryLostSeconds = 0;
                _lastLossSampleUtc = DateTime.MinValue;
            }

            DateTime now = availableNow;
            double gapSeconds;
            if (_lastSampleUtc == DateTime.MinValue)
            {
                gapSeconds = 1.0;
            }
            else if (now <= _lastSampleUtc)
            {
                _lastReason = "GPU 时间戳未前进，不累计证据";
                return _currentDemand;
            }
            else
            {
                gapSeconds = (now - _lastSampleUtc).TotalSeconds;
            }

            bool gapInterrupted = gapSeconds > MaxContinuousSampleGapSeconds;
            double elapsedSeconds;
            if (gapInterrupted)
            {
                _upshiftCreditSeconds = Math.Max(0, _upshiftCreditSeconds - 2.0 * gapSeconds);
                _strongCreditSeconds = Math.Max(0, _strongCreditSeconds - 2.0 * gapSeconds);
                _downshiftCreditSeconds = Math.Max(0, _downshiftCreditSeconds - 2.0 * gapSeconds);
                elapsedSeconds = 1.0;
            }
            else
            {
                elapsedSeconds = Math.Max(0.05, Math.Min(10.0, gapSeconds));
            }
            _lastSampleUtc = now;

            double util = Math.Max(0, Math.Min(100, gpuUtilizationPercent));
            double power = Math.Max(0, gpuPowerWatts);

            bool highEnter = util >= HighUtilizationPercent && power >= HighPowerWatts &&
                             gpuTemperatureC >= HighMinimumTemperatureC;
            bool highCancel = util >= HighCancelUtilizationPercent && power >= HighCancelPowerWatts;
            bool moderateEnter = util >= ModerateUtilizationPercent && power >= ModeratePowerWatts;

            // 强证据（高需求）积分：起始帧计 0，后续 +elapsed；中断按滞回衰减。
            if (highEnter)
            {
                if (_lastStrongEnter)
                    _strongCreditSeconds += elapsedSeconds;
                else
                    _strongCreditSeconds = 0;
                _lastStrongEnter = true;
            }
            else
            {
                _strongCreditSeconds = Math.Max(0,
                    _strongCreditSeconds - (highCancel ? 0.5 : 2.0) * elapsedSeconds);
                _lastStrongEnter = false;
            }

            if (moderateEnter)
            {
                if (_lastModerateEnter)
                    _upshiftCreditSeconds += elapsedSeconds;
                else
                    _upshiftCreditSeconds = 0;
                _lastModerateEnter = true;
            }
            else
            {
                _upshiftCreditSeconds = Math.Max(0,
                    _upshiftCreditSeconds - (highCancel ? 0.5 : 2.0) * elapsedSeconds);
                _lastModerateEnter = false;
            }

            if (_currentDemand == GpuThermalDemand.Low)
            {
                if (moderateEnter && _upshiftCreditSeconds >= ModerateDwellSeconds)
                {
                    _currentDemand = GpuThermalDemand.Moderate;
                    _upshiftCreditSeconds = 0;
                    // 逐级升档：强证据积分在进入 Moderate 后重新累计（同一
                    // 证据窗口不得从 Low 直接跳到 High）。
                    _strongCreditSeconds = 0;
                    _lastStrongEnter = false;
                    _lastReason = "GPU 中等负载持续，需求升为 Moderate";
                }
                else
                {
                    _lastReason = "GPU 保持 Low 需求";
                }
                return _currentDemand;
            }

            if (_currentDemand == GpuThermalDemand.Moderate)
            {
                if (highEnter)
                {
                    if (_strongCreditSeconds >= HighDwellSeconds)
                    {
                        _currentDemand = GpuThermalDemand.High;
                        _strongCreditSeconds = 0;
                        _upshiftCreditSeconds = 0;
                        _lastStrongEnter = false;
                        _lastReason = "GPU 游戏证据持续，需求升为 High";
                    }
                    else
                    {
                        _lastReason = "GPU 高需求证据等待驻留";
                    }
                    return _currentDemand;
                }
                if (!moderateEnter)
                {
                    _downshiftCreditSeconds += elapsedSeconds;
                    if (_downshiftCreditSeconds >= ModerateToLowDwellSeconds)
                    {
                        _currentDemand = GpuThermalDemand.Low;
                        _downshiftCreditSeconds = 0;
                        _upshiftCreditSeconds = 0;
                        _lastReason = "GPU 负载回落，需求降为 Low";
                        return _currentDemand;
                    }
                    _lastReason = "GPU 等待需求降级驻留";
                }
                else
                {
                    _downshiftCreditSeconds = Math.Max(0, _downshiftCreditSeconds - 2.0 * elapsedSeconds);
                    _lastReason = "GPU 保持 Moderate 需求";
                }
                return _currentDemand;
            }

            if (_currentDemand == GpuThermalDemand.High)
            {
                bool highToModerate = !highEnter && !highCancel;
                if (highToModerate)
                {
                    _downshiftCreditSeconds += elapsedSeconds;
                    if (_downshiftCreditSeconds >= HighToModerateDwellSeconds)
                    {
                        _currentDemand = GpuThermalDemand.Moderate;
                        _downshiftCreditSeconds = 0;
                        _strongCreditSeconds = 0;
                        _lastReason = "GPU 游戏证据中断，需求降为 Moderate";
                        return _currentDemand;
                    }
                    _lastReason = "GPU 等待高需求中断驻留";
                }
                else
                {
                    _downshiftCreditSeconds = Math.Max(0, _downshiftCreditSeconds - 2.0 * elapsedSeconds);
                    _lastReason = "GPU 保持 High 需求";
                }
                return _currentDemand;
            }

            return _currentDemand;
        }
    }
}
