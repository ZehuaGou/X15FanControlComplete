using System;

namespace X15FanCore.Control
{
    // =====================================================================
    // 跨风扇辅助控制器（架构收束 2026-08-02）。
    //
    // 原则：主风扇优先、辅助风扇延迟介入。
    // - CPU 热源默认由 CPU 风扇负责；GPU 热源默认由 GPU 风扇负责。
    // - 日常状态不得因为一侧短暂升温立即同时提高两侧风扇。
    // - 辅助风扇启动必须同时满足（EngageSustainedSeconds 默认 20s）：
    //   1) 主通道温度接近或超过目标（>= 目标 - EngageTemperatureMarginC）；
    //   2) 主风扇已接近本档软上限（>= 软上限 - EngageFanMarginPercent）；
    //   3) 温度连续 20 秒没有明显下降（持续信用累计）；
    //   4) 不是短暂尖峰（信用不足/中断即衰减，尖峰不会达成 20s 持续）。
    // - 辅助量从低值缓慢增加，先限制为主风扇目标的 AssistRatio（20%~30%
    //   候选值，未硬件标定前不得宣称最终值）。
    // - 辅助退出：温度恢复余量 + 温升率 <= 0 + 连续 ExitStableSeconds
    //   （默认 60s）稳定，使用滞回避免反复开关。
    // - Emergency / 快速温升：本控制器立即输出 EmergencyAssist，由引擎
    //   对双侧风扇设置 emergencyOverride 突破声学软上限共同散热。
    //
    // 输入来自主通道状态（上一控制周期的决策 + 快照，1 秒周期可接受）；
    // 输出为「CPU 风扇辅助 GPU」「GPU 风扇辅助 CPU」两个辅助量，以及
    // EmergencyAssist 标志。本类不接触硬件，全部可离线测试。
    // =====================================================================

    public sealed class CrossFanAssistSettings
    {
        // 主通道温度接近目标的余量（°C）。
        public double EngageTemperatureMarginC = 3.0;
        // 主风扇接近软上限的余量（%）。
        public double EngageFanMarginPercent = 2.0;
        // 温度连续不明显下降的持续时长（秒）才允许辅助介入。
        public double EngageSustainedSeconds = 20.0;
        // 辅助量 = 主风扇目标 × AssistRatio（候选 20%~30%）。
        public double AssistRatio = 0.25;
        // 辅助量爬升速率（%/s，从低值缓慢增加）。
        public double AssistRampRatePercentPerSecond = 1.5;
        // 辅助退出：主通道温度恢复余量（°C）。
        public double ExitTemperatureMarginC = 5.0;
        // 辅助退出：满足恢复条件后的稳定时长（秒）。
        public double ExitStableSeconds = 60.0;
        // 最小辅助量（%）：低于该值视为 0，避免抖动。
        public double MinimumAssistPercent = 3.0;
    }

    /// <summary>主通道状态输入（辅助判定依据）。</summary>
    public sealed class AssistChannelInput
    {
        public double TemperatureC;
        public double FanDutyPercent;
        public double RiseRateCPerSec;
        public double SoftMaximumFanDutyPercent;
        public double TargetTemperatureC;
        public bool Emergency;
        // 遥测有效（GPU 通道）：遥测丢失时不得触发辅助（无法确认热证据）。
        public bool TelemetryValid = true;
    }

    public sealed class CrossFanAssistController
    {
        private readonly CrossFanAssistSettings _settings;

        // 每方向辅助状态。
        private double _cpuFanAssistPercent;      // CPU 风扇辅助 GPU（GPU 为主通道）
        private double _gpuFanAssistPercent;      // GPU 风扇辅助 CPU（CPU 为主通道）
        private double _gpuSustainedCredit;       // GPU 主通道持续不降信用
        private double _cpuSustainedCredit;       // CPU 主通道持续不降信用
        private DateTime? _cpuStableSinceUtc;     // CPU 主通道恢复稳定计时
        private DateTime? _gpuStableSinceUtc;
        private bool _gpuAssistEngaged;
        private bool _cpuAssistEngaged;
        private bool _emergencyAssist;
        private string _reason = string.Empty;
        // 时间连续性状态：相邻有效采样时间戳、采样中断标志。
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private bool _sampleInterrupted;
        // 采样中断阈值（秒）：超过视为采样中断（睡眠/控制循环停顿），
        // 不补算介入积分，恢复连续计时重置。
        public const double MaxContinuousSampleGapSeconds = 3.0;
        // 每通道声学参数（软上限/目标温度）：由 MainForm 在档位变化时注入
        //（AssistChannelInput 未携带时使用）。
        private double _cpuSoftMaximumPercent = 100;
        private double _cpuTargetTemperatureC = 88;
        private double _gpuSoftMaximumPercent = 100;
        private double _gpuTargetTemperatureC = 88;

        /// <summary>注入每通道声学参数（来自当前档位曲线）；档位切换时调用。</summary>
        public void SetChannelLimits(ChannelAcousticLimits cpuLimits, ChannelAcousticLimits gpuLimits)
        {
            if (cpuLimits != null)
            {
                if (cpuLimits.SoftMaximumFanDutyPercent > 0) _cpuSoftMaximumPercent = cpuLimits.SoftMaximumFanDutyPercent;
                if (cpuLimits.TargetTemperatureC > 0) _cpuTargetTemperatureC = cpuLimits.TargetTemperatureC;
            }
            if (gpuLimits != null)
            {
                if (gpuLimits.SoftMaximumFanDutyPercent > 0) _gpuSoftMaximumPercent = gpuLimits.SoftMaximumFanDutyPercent;
                if (gpuLimits.TargetTemperatureC > 0) _gpuTargetTemperatureC = gpuLimits.TargetTemperatureC;
            }
        }

        public CrossFanAssistController(CrossFanAssistSettings settings = null)
        {
            _settings = settings ?? new CrossFanAssistSettings();
        }

        /// <summary>CPU 风扇对 GPU 主通道的辅助量（%）。</summary>
        public double CpuFanAssistPercent { get { return _cpuFanAssistPercent; } }
        /// <summary>GPU 风扇对 CPU 主通道的辅助量（%）。</summary>
        public double GpuFanAssistPercent { get { return _gpuFanAssistPercent; } }
        public bool EmergencyAssistActive { get { return _emergencyAssist; } }
        public bool GpuAssistEngaged { get { return _gpuAssistEngaged; } }
        public bool CpuAssistEngaged { get { return _cpuAssistEngaged; } }
        public string Reason { get { return _reason ?? string.Empty; } }

        public void Reset()
        {
            _cpuFanAssistPercent = 0;
            _gpuFanAssistPercent = 0;
            _gpuSustainedCredit = 0;
            _cpuSustainedCredit = 0;
            _cpuStableSinceUtc = null;
            _gpuStableSinceUtc = null;
            _gpuAssistEngaged = false;
            _cpuAssistEngaged = false;
            _emergencyAssist = false;
            _lastSampleUtc = DateTime.MinValue;
            _sampleInterrupted = false;
            _reason = string.Empty;
        }

        // 由相邻有效采样时间戳计算本帧 elapsed（秒）：
        // - 首帧：1.0（无历史）；
        // - 时间戳倒退/未前进：0（不累计任何积分，也不推进采样游标）；
        // - gap > MaxContinuousSampleGapSeconds：采样中断——介入信用按
        //   2×gap 快速衰减（有界），恢复计时由调用方重置，本帧按单个
        //   采样计 1.0 秒（不补算墙钟空档）；
        // - 正常：clamp(0.05, 10, gap)。
        private double ComputeElapsed(DateTime nowUtc)
        {
            if (_lastSampleUtc == DateTime.MinValue)
            {
                _lastSampleUtc = nowUtc;
                _sampleInterrupted = false;
                return 1.0;
            }
            if (nowUtc <= _lastSampleUtc)
            {
                // 时间戳倒退或未前进：不累计；不推进游标（下一有效帧仍以
                // 最后一次有效采样为基准）。
                _sampleInterrupted = false;
                return 0;
            }

            double gap = (nowUtc - _lastSampleUtc).TotalSeconds;
            _lastSampleUtc = nowUtc;
            if (gap > MaxContinuousSampleGapSeconds)
            {
                // 采样中断：介入信用快速衰减（有界），不得把中断前后的
                // 证据拼接成持续积分；恢复连续计时由 Update 重置。
                double decay = 2.0 * Math.Min(gap, 60.0);
                _cpuSustainedCredit = Math.Max(0, _cpuSustainedCredit - decay);
                _gpuSustainedCredit = Math.Max(0, _gpuSustainedCredit - decay);
                _sampleInterrupted = true;
                return 1.0;   // 本帧按单采样计
            }

            _sampleInterrupted = false;
            return Math.Max(0.05, Math.Min(10.0, gap));
        }

        /// <summary>每控制周期调用一次（真实实现 1 秒周期；测试按需）。
        /// 时间连续性：elapsed 由相邻有效采样时间戳计算；时间戳倒退不累计；
        /// gap &gt; MaxContinuousSampleGapSeconds 视为采样中断——中断不补算介入
        /// 积分（信用快速衰减），且重置恢复连续计时（不得跨墙钟空档拼接）。</summary>
        public void Update(AssistChannelInput cpu, AssistChannelInput gpu, DateTime nowUtc)
        {
            AssistChannelInput c = cpu ?? new AssistChannelInput();
            AssistChannelInput g = gpu ?? new AssistChannelInput();
            // 未显式提供的声学参数使用注入值。
            if (c.SoftMaximumFanDutyPercent <= 0) c.SoftMaximumFanDutyPercent = _cpuSoftMaximumPercent;
            if (c.TargetTemperatureC <= 0) c.TargetTemperatureC = _cpuTargetTemperatureC;
            if (g.SoftMaximumFanDutyPercent <= 0) g.SoftMaximumFanDutyPercent = _gpuSoftMaximumPercent;
            if (g.TargetTemperatureC <= 0) g.TargetTemperatureC = _gpuTargetTemperatureC;

            double elapsed = ComputeElapsed(nowUtc);
            bool interrupted = _sampleInterrupted;
            if (interrupted)
            {
                // 采样中断：恢复连续计时重置（不得把中断前后的稳定时间拼接）。
                _cpuStableSinceUtc = null;
                _gpuStableSinceUtc = null;
            }

            // 紧急：无条件双侧共同散热（引擎同时置 emergencyOverride 突破软上限）。
            _emergencyAssist = c.Emergency || g.Emergency;

            // ---- CPU 风扇辅助 GPU（GPU 为主通道）----
            _cpuFanAssistPercent = UpdateDirection(
                g,                       // 主通道 = GPU
                _cpuFanAssistPercent,
                ref _gpuSustainedCredit,
                ref _gpuStableSinceUtc,
                ref _gpuAssistEngaged,
                elapsed,
                nowUtc,
                "GPU");

            // ---- GPU 风扇辅助 CPU（CPU 为主通道）----
            _gpuFanAssistPercent = UpdateDirection(
                c,                       // 主通道 = CPU
                _gpuFanAssistPercent,
                ref _cpuSustainedCredit,
                ref _cpuStableSinceUtc,
                ref _cpuAssistEngaged,
                elapsed,
                nowUtc,
                "CPU");
        }

        private double UpdateDirection(
            AssistChannelInput main,
            double currentAssist,
            ref double sustainedCredit,
            ref DateTime? stableSinceUtc,
            ref bool engaged,
            double elapsedSeconds,
            DateTime nowUtc,
            string channelName)
        {
            // 主通道遥测无效（如 GPU 遥测丢失）：不得判定辅助（无热证据），
            // 信用不累计，已介入的辅助按退出逻辑衰减。
            bool evidenceValid = main.TelemetryValid && main.SoftMaximumFanDutyPercent > 0 &&
                                 main.TargetTemperatureC > 0;

            // Emergency：立即介入（量 = 主风扇目标 × AssistRatio，由引擎
            // emergencyOverride 突破软上限，双侧共同散热）。
            if (main.Emergency)
            {
                engaged = true;
                sustainedCredit = Math.Max(sustainedCredit, _settings.EngageSustainedSeconds);
                stableSinceUtc = null;
                double emergencyAssist = Clamp(
                    main.FanDutyPercent * _settings.AssistRatio, 0, 100);
                _reason = channelName + " 紧急：辅助立即介入";
                return Math.Max(currentAssist, emergencyAssist);
            }

            if (!evidenceValid)
            {
                sustainedCredit = Math.Max(0, sustainedCredit - 2.0 * elapsedSeconds);
                _reason = channelName + " 主通道遥测无效/证据不足，辅助衰减";
                return DecayAssist(currentAssist, elapsedSeconds);
            }

            bool nearLimit = main.FanDutyPercent >= main.SoftMaximumFanDutyPercent - _settings.EngageFanMarginPercent;
            bool nearTarget = main.TemperatureC >= main.TargetTemperatureC - _settings.EngageTemperatureMarginC;
            bool notClearlyFalling = main.RiseRateCPerSec > -0.1;

            if (nearLimit && nearTarget && notClearlyFalling)
            {
                sustainedCredit += elapsedSeconds;
                stableSinceUtc = null;   // 重新满足介入条件：退出稳定计时取消
            }
            else if (nearLimit && nearTarget)
            {
                // 温度在下降但未明显：慢衰减（接近但未满足 20s 持续）。
                sustainedCredit = Math.Max(0, sustainedCredit - 0.5 * elapsedSeconds);
            }
            else
            {
                // 任一介入条件不满足（短暂尖峰/风扇回落/温度不足）：快衰减。
                sustainedCredit = Math.Max(0, sustainedCredit - 2.0 * elapsedSeconds);
            }

            if (!engaged && sustainedCredit >= _settings.EngageSustainedSeconds)
            {
                engaged = true;
                sustainedCredit = 0;
                _reason = channelName + " 主通道持续 20s 接近上限，辅助介入";
            }

            if (engaged)
            {
                // 退出判定：温度恢复余量 + 温升率 <= 0 + 连续稳定时长。
                // 稳定计时只基于有效采样推进（elapsed > 0）；时间戳倒退帧
                // （elapsed == 0）不得启动/推进恢复连续计时。
                bool recovered = main.TemperatureC <= main.TargetTemperatureC - _settings.ExitTemperatureMarginC &&
                                 main.RiseRateCPerSec <= 0;
                if (recovered && elapsedSeconds > 0)
                {
                    if (!stableSinceUtc.HasValue)
                        stableSinceUtc = nowUtc;
                    if ((nowUtc - stableSinceUtc.Value).TotalSeconds >= _settings.ExitStableSeconds)
                    {
                        engaged = false;
                        stableSinceUtc = null;
                        sustainedCredit = 0;
                        _reason = channelName + " 主通道已恢复（温度回落+稳定 " +
                            _settings.ExitStableSeconds + "s），辅助退出";
                        return 0;
                    }
                }
                else
                {
                    stableSinceUtc = null;
                }

                double targetAssist = Clamp(main.FanDutyPercent * _settings.AssistRatio, 0, 100);
                return RampAssist(currentAssist, targetAssist, elapsedSeconds);
            }

            _reason = channelName + " 未达辅助介入条件";
            return DecayAssist(currentAssist, elapsedSeconds);
        }

        // 未介入时向 0 衰减（保留滞回：刚退出后不立即重新介入，需要完整
        // EngageSustainedSeconds 持续信用）。
        private double DecayAssist(double currentAssist, double elapsedSeconds)
        {
            double next = Math.Max(0, currentAssist - 4.0 * elapsedSeconds);
            if (next < _settings.MinimumAssistPercent)
                next = 0;
            return next;
        }

        // 辅助量从当前值向目标缓慢爬升（从低值开始，避免突响）。
        private double RampAssist(double currentAssist, double targetAssist, double elapsedSeconds)
        {
            if (targetAssist > currentAssist)
            {
                double step = _settings.AssistRampRatePercentPerSecond * elapsedSeconds;
                return Math.Min(targetAssist, currentAssist + step);
            }
            return Math.Max(targetAssist, currentAssist - 4.0 * elapsedSeconds);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
