using System;

namespace X15FanCore.Control
{
    /// <summary>
    /// 共享热预算让出控制器（Shared Thermal Shedding，2026-08-03 纯离线实现）。
    ///
    /// 目标：GPU 已接近热上限、GPU 风扇已接近全速、且 CPU 也很热时，持续一段
    /// 时间后把 CPU 功耗从当前档临时压到 Quiet（安全预设 25/35/28W），为 GPU
    /// 与共享热管让出热预算；冷却稳定后自动恢复。**只在 Auto 模式生效**。
    ///
    /// 明确边界（不重复造轮子）：
    /// - 不引入 GPU 档位、整机瓦数预算或任何 GPU 功耗控制；GPU 后端生产路径
    ///   恒为 TelemetryOnly，本控制器不接收也不产生 GPU 瓦数；
    /// - CPU 温度平坦（如 87°C）即可计入——不要求 CPU 继续升温；
    /// - CPU 功耗可降为 Quiet，但风扇 profile 的保持由调用方（协调器
    ///   CpuFanProfileTier + MainForm 进入档位下限）负责，本控制器不触碰风扇。
    ///
    /// 进入条件（全部成立并连续累计 EnterDwellSeconds = 20s）：
    ///   1) autoMode（仅 Auto 策略）；
    ///   2) CPU/GPU 温度遥测均有效（各自 profile min/max 校验 + 遥测可用）；
    ///   3) GPU 温度 >= 84°C；4) CPU 温度 >= 85°C；5) GPU 实际风扇占空 >= 95%。
    ///
    /// 恢复证据（阈值 RecoveryDwellSeconds = 60s）：
    ///   GPU <= 78°C 且 CPU <= 80°C 时 +1x；GPU <=81°C 且 CPU <=84°C 的
    ///   轻微波动进入滞回带并 -0.25x 慢衰减；更热时 -2x 快衰减。这样短时
    ///   CPU 尖峰不会无限推迟恢复，而持续高温仍会把信用压回 0。
    ///
    /// 时间连续性：
    /// - 使用采样时间戳积分；时间戳倒退不累计；
    /// - gap &gt; MaxContinuousSampleGapSeconds（3s）不得补算墙钟时间，并废弃
    ///   进入/恢复的连续信用（短尖峰、间断脉冲不得拼接）；
    /// - 激活后遥测暂时丢失：保持让出（较低 CPU 功耗），不累计恢复证据，
    ///   不得因数据丢失升功耗。
    ///
    /// 阈值均为候选值（未硬件标定），仅作 E0/实机前离线验证。
    /// </summary>
    public sealed class SharedThermalBudgetSettings
    {
        public double EnterDwellSeconds = 20.0;
        public double RecoveryDwellSeconds = 60.0;
        public int GpuEnterTemperatureC = 84;
        public int CpuEnterTemperatureC = 85;
        public int GpuFanDutyEnterPercent = 95;
        public int GpuRecoveryTemperatureC = 78;
        public int CpuRecoveryTemperatureC = 80;
        public int GpuRecoveryHoldTemperatureC = 81;
        public int CpuRecoveryHoldTemperatureC = 84;
        public double RecoveryHoldDecayPerSecond = 0.25;
        public double RecoveryHotDecayPerSecond = 2.0;
        public double MaxContinuousSampleGapSeconds = 3.0;
    }

    public sealed class SharedThermalBudgetController
    {
        private readonly int _cpuMinValidC;
        private readonly int _cpuMaxValidC;
        private readonly int _gpuMinValidC;
        private readonly int _gpuMaxValidC;
        private readonly SharedThermalBudgetSettings _settings;

        private bool _sheddingActive;
        private double _enterCreditSeconds;
        private double _recoveryCreditSeconds;
        private DateTime _lastSampleUtc = DateTime.MinValue;
        private bool _entryContributingLastSample;
        private bool _recoveryContributingLastSample;
        private string _reason = "共享热预算：未激活";

        public SharedThermalBudgetController(
            int cpuMinValidC,
            int cpuMaxValidC,
            int gpuMinValidC,
            int gpuMaxValidC,
            SharedThermalBudgetSettings settings = null)
        {
            _cpuMinValidC = cpuMinValidC;
            _cpuMaxValidC = cpuMaxValidC;
            _gpuMinValidC = gpuMinValidC;
            _gpuMaxValidC = gpuMaxValidC;
            _settings = settings ?? new SharedThermalBudgetSettings();
        }

        public bool SheddingActive { get { return _sheddingActive; } }
        public double EnterCreditSeconds { get { return _enterCreditSeconds; } }
        public double RecoveryCreditSeconds { get { return _recoveryCreditSeconds; } }
        public string Reason { get { return _reason ?? string.Empty; } }

        public void Reset()
        {
            _sheddingActive = false;
            _enterCreditSeconds = 0;
            _recoveryCreditSeconds = 0;
            _lastSampleUtc = DateTime.MinValue;
            _entryContributingLastSample = false;
            _recoveryContributingLastSample = false;
            _reason = "共享热预算：已重置";
        }

        /// <summary>每控制周期调用一次。autoMode=false（固定策略）时立即复位，
        /// 功能不启用（但既有 Emergency 语义由协调器独立保持）。</summary>
        public void Update(
            bool autoMode,
            bool cpuTelemetryValid,
            int cpuTemperatureC,
            bool gpuTelemetryValid,
            int gpuTemperatureC,
            int gpuFanDutyPercent,
            DateTime timestampUtc)
        {
            if (!autoMode)
            {
                Reset();
                return;
            }

            double elapsed = ComputeElapsed(timestampUtc);
            bool gapDiscarded = elapsed < 0;
            if (gapDiscarded)
            {
                // gap > 3s：废弃进入/恢复的连续信用。当前帧只能重新建立
                // 连续区间的起点，不能凭空把无采样间隔算作 1 秒证据。
                _enterCreditSeconds = 0;
                _recoveryCreditSeconds = 0;
                _entryContributingLastSample = false;
                _recoveryContributingLastSample = false;
                elapsed = 0;
            }

            bool cpuValid = cpuTelemetryValid &&
                cpuTemperatureC >= _cpuMinValidC && cpuTemperatureC <= _cpuMaxValidC;
            bool gpuValid = gpuTelemetryValid &&
                gpuTemperatureC >= _gpuMinValidC && gpuTemperatureC <= _gpuMaxValidC;

            if (!_sheddingActive)
            {
                bool entry = cpuValid && gpuValid &&
                    cpuTemperatureC >= _settings.CpuEnterTemperatureC &&
                    gpuTemperatureC >= _settings.GpuEnterTemperatureC &&
                    gpuFanDutyPercent >= _settings.GpuFanDutyEnterPercent;
                if (entry)
                {
                    // 只有相邻两个有效样本都满足进入条件时，中间的时间区间
                    // 才能作为连续证据。首个热样本只建立起点，不预支 1 秒。
                    if (_entryContributingLastSample)
                        _enterCreditSeconds += elapsed;
                    else
                        _enterCreditSeconds = 0;
                    _entryContributingLastSample = true;
                    _reason = "共享热预算让出进入中：" + Math.Round(_enterCreditSeconds) + "s/" +
                        _settings.EnterDwellSeconds + "s（GPU " + gpuTemperatureC + "C ≥ " +
                        _settings.GpuEnterTemperatureC + "C、CPU " + cpuTemperatureC + "C ≥ " +
                        _settings.CpuEnterTemperatureC + "C、GPU 风扇 " + gpuFanDutyPercent + "% ≥ " +
                        _settings.GpuFanDutyEnterPercent + "%）";
                    if (_enterCreditSeconds >= _settings.EnterDwellSeconds)
                    {
                        _sheddingActive = true;
                        _enterCreditSeconds = 0;
                        _recoveryCreditSeconds = 0;
                        _entryContributingLastSample = false;
                        _recoveryContributingLastSample = false;
                        _reason = "共享热预算让出激活：CPU 有效功耗至多 Quiet（25/35W），风扇 profile 保持进入前档位";
                    }
                }
                else
                {
                    // 进入条件要求连续成立；任何有效中断都必须清零，不能把
                    // 两段热脉冲通过缓慢衰减拼接成 20 秒。
                    _enterCreditSeconds = 0;
                    _entryContributingLastSample = false;
                    _reason = "共享热预算：等待进入条件（" +
                        (cpuValid ? "CPU " + cpuTemperatureC + "C" : "CPU 遥测无效") + "；" +
                        (gpuValid ? "GPU " + gpuTemperatureC + "C" : "GPU 遥测无效") + "；GPU 风扇 " +
                        gpuFanDutyPercent + "%）";
                }
                return;
            }

            // ---- 已激活 ----
            if (!cpuValid || !gpuValid)
            {
                // 遥测丢失/无效：保持让出（较低 CPU 功耗），不累计恢复证据，
                // 不得因数据丢失升功耗。
                _recoveryCreditSeconds = 0;
                _recoveryContributingLastSample = false;
                _reason = "共享热预算让出中：遥测丢失，保持 Quiet 等待有效恢复证据";
                return;
            }

            bool recovered = gpuTemperatureC <= _settings.GpuRecoveryTemperatureC &&
                             cpuTemperatureC <= _settings.CpuRecoveryTemperatureC;
            bool recoveryHold = gpuTemperatureC <= _settings.GpuRecoveryHoldTemperatureC &&
                                cpuTemperatureC <= _settings.CpuRecoveryHoldTemperatureC;
            if (recovered)
            {
                // 与进入证据相同：首个冷却样本只建立恢复区间起点。
                if (_recoveryContributingLastSample)
                    _recoveryCreditSeconds += elapsed;
                // 从滞回/高温区重新进入强恢复区时，首帧只重新建立连续
                // 区间，不增加时间，但必须保留已经按规则衰减后的信用。
                _recoveryContributingLastSample = true;
                _reason = "共享热预算恢复中：" + Math.Round(_recoveryCreditSeconds) + "s/" +
                    _settings.RecoveryDwellSeconds + "s（GPU " + gpuTemperatureC + "C ≤ " +
                    _settings.GpuRecoveryTemperatureC + "C、CPU " + cpuTemperatureC + "C ≤ " +
                    _settings.CpuRecoveryTemperatureC + "C）";
                if (_recoveryCreditSeconds >= _settings.RecoveryDwellSeconds)
                {
                    _sheddingActive = false;
                    _recoveryCreditSeconds = 0;
                    _enterCreditSeconds = 0;
                    _entryContributingLastSample = false;
                    _recoveryContributingLastSample = false;
                    _reason = "共享热预算已恢复：回到正常治理链输出（不直接强升功耗）";
                }
            }
            else if (recoveryHold)
            {
                // 退出游戏后的真实 CPU 遥测会在 81-84C 间短暂波动。该滞回带
                // 不增加恢复证据，只缓慢衰减，避免单个轻微尖峰把已积累的
                // 冷却证据全部清零；下一次强恢复样本重新建立连续区间起点。
                _recoveryCreditSeconds = Math.Max(0,
                    _recoveryCreditSeconds - _settings.RecoveryHoldDecayPerSecond * elapsed);
                _recoveryContributingLastSample = false;
                _reason = "共享热预算恢复滞回：CPU " + cpuTemperatureC + "C / GPU " +
                    gpuTemperatureC + "C，恢复信用慢衰减至 " +
                    Math.Round(_recoveryCreditSeconds, 1) + "s";
            }
            else
            {
                // 明显重新变热时快速衰减；若温度持续偏高，信用会回到 0，
                // 因而不会在机器仍热时恢复 CPU 功耗。短时尖峰则不会永久
                // 抹掉此前的全部冷却证据。
                _recoveryCreditSeconds = Math.Max(0,
                    _recoveryCreditSeconds - _settings.RecoveryHotDecayPerSecond * elapsed);
                _recoveryContributingLastSample = false;
                _reason = "共享热预算恢复受阻：CPU " + cpuTemperatureC + "C / GPU " +
                    gpuTemperatureC + "C，恢复信用快衰减至 " +
                    Math.Round(_recoveryCreditSeconds, 1) + "s，保持 Quiet";
            }
        }

        // 由采样时间戳计算本帧 elapsed（秒）：
        // - 首帧 0（只建立连续区间起点）；时间戳倒退/未前进 → 0（不累计、不推进游标）；
        // - gap > MaxContinuousSampleGapSeconds → 返回 -1（调用方废弃信用）；
        // - 正常 → clamp(0.05, 10, gap)。
        private double ComputeElapsed(DateTime nowUtc)
        {
            if (_lastSampleUtc == DateTime.MinValue)
            {
                _lastSampleUtc = nowUtc;
                return 0;
            }
            if (nowUtc <= _lastSampleUtc)
            {
                return 0;
            }
            double gap = (nowUtc - _lastSampleUtc).TotalSeconds;
            _lastSampleUtc = nowUtc;
            if (gap > _settings.MaxContinuousSampleGapSeconds)
            {
                return -1;
            }
            return Math.Max(0.05, Math.Min(10.0, gap));
        }
    }
}
