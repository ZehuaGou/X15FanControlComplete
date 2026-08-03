using System;

namespace X15FanCore.Probe
{
    /// <summary>
    /// X15EcProbe 采集合同（E0 采集合同修正，2026-08-03）。
    ///
    /// - 采样内温度中止（--abort-cpu-temp &lt;T&gt;）：每个样本读取完成后先写入并
    ///   flush 当前样本；若 CPU 温度 &gt;= T，必须在本轮立即退出（独立非零退出码
    ///   3），不得继续 sleep 或读取第二个样本；默认不传参数（abortCpuTempC &lt;= 0）
    ///   保持旧行为。
    /// - RPM 合理性诊断（仅 E0 数据质量检查，不得进入生产风扇算法）：
    ///   RPM &gt; 10000，或 Duty &gt;= 20% 且 RPM &lt; 200 → implausible；
    ///   API 调用成功（status=OK/ERROR）与数值合理性分开记录。
    ///
    /// 本类不接触硬件：采样读取通过委托注入（测试使用 fake telemetry）。
    /// </summary>
    public sealed class EcProbeSample
    {
        public bool ReadOk = true;
        public string ReadError = string.Empty;
        public int CpuTemperatureC;
        public int GpuTemperatureC;
        public int CpuRpm;
        public int GpuRpm;
        public int CpuDutyPercent;
        public int GpuDutyPercent;
    }

    public sealed class RpmPlausibilityResult
    {
        public bool CpuPlausible = true;
        public bool GpuPlausible = true;
        public string CpuNote = string.Empty;
        public string GpuNote = string.Empty;
    }

    public sealed class EcProbeSamplingResult
    {
        public int SamplesWritten;
        public int ExitCode;
        public bool AbortedByCpuTemp;
        public int AbortObservedC;      // 触发中止的 CPU 温度
        public int AbortThresholdC;
    }

    public static class EcProbeContract
    {
        // 采样内温度中止的独立非零退出码。
        public const int ExitCodeAbortCpuTemp = 3;
        // RPM 合理性阈值（仅 E0 数据质量检查，不得进入生产风扇算法）。
        public const int ImplausibleMaxRpm = 10000;
        public const int ImplausibleMinRpmAtDuty = 200;
        public const int PlausibilityDutyPercentFloor = 20;

        /// <summary>中止判定：abortCpuTempC &lt;= 0 表示未启用。</summary>
        public static bool ShouldAbortForCpuTemp(int cpuTemperatureC, int abortCpuTempC)
        {
            return abortCpuTempC > 0 && cpuTemperatureC >= abortCpuTempC;
        }

        /// <summary>
        /// RPM 合理性诊断（不修改原始值；调用成功与数值合理性分开记录）。
        /// 阈值仅用于 E0 数据质量检查：出现 implausible 时 E0 至少判定
        /// INCONCLUSIVE，不得用其计算正常 RPM 分位数后直接通过。
        /// </summary>
        public static RpmPlausibilityResult EvaluateRpmPlausibility(
            int cpuRpm, int gpuRpm, int cpuDutyPercent, int gpuDutyPercent)
        {
            RpmPlausibilityResult result = new RpmPlausibilityResult();
            result.CpuPlausible = IsPlausible(cpuRpm, cpuDutyPercent, out result.CpuNote);
            result.GpuPlausible = IsPlausible(gpuRpm, gpuDutyPercent, out result.GpuNote);
            return result;
        }

        private static bool IsPlausible(int rpm, int dutyPercent, out string note)
        {
            note = string.Empty;
            if (rpm > ImplausibleMaxRpm)
            {
                note = "IMPLAUSIBLE_RAW_RPM_READ(>10000) ROOT_CAUSE_UNKNOWN";
                return false;
            }
            if (dutyPercent >= PlausibilityDutyPercentFloor && rpm < ImplausibleMinRpmAtDuty)
            {
                note = "IMPLAUSIBLE_RAW_RPM_READ(duty>=20% 且 rpm<200) ROOT_CAUSE_UNKNOWN";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 采样循环驱动：每样本 读取 → 写入（onSampleWritten，须含 flush）→
        /// 中止检查 → 下一样本。
        /// - 中止触发时立即返回，不得继续 sleep 或读取第二个样本
        ///   （样本间等待由调用方放在下一次 readSample 之前，中止后不会执行）；
        /// - readSample 返回 ReadOk=false 表示该样本读取失败：写入 ERROR 行后
        ///   继续下一样本（与旧行为一致；E0 程序级门禁另行处理读取失败）。
        /// </summary>
        public static EcProbeSamplingResult RunSamples(
            int seconds,
            int abortCpuTempC,
            Func<int, EcProbeSample> readSample,
            Action<int, EcProbeSample> onSampleWritten)
        {
            EcProbeSamplingResult result = new EcProbeSamplingResult { ExitCode = 0 };
            int count = Math.Max(0, seconds);
            for (int i = 0; i < count; i++)
            {
                EcProbeSample sample = readSample(i);
                result.SamplesWritten = i + 1;
                // 先写入并 flush 当前样本（含读取失败样本的 ERROR 行）。
                onSampleWritten(i, sample);
                // 采样内中止：本轮立即退出。
                if (sample.ReadOk && ShouldAbortForCpuTemp(sample.CpuTemperatureC, abortCpuTempC))
                {
                    result.AbortedByCpuTemp = true;
                    result.AbortObservedC = sample.CpuTemperatureC;
                    result.AbortThresholdC = abortCpuTempC;
                    result.ExitCode = ExitCodeAbortCpuTemp;
                    return result;
                }
            }
            return result;
        }
    }
}
