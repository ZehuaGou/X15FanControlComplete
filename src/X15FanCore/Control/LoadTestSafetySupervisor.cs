using System;

namespace X15FanCore.Control
{
    // =====================================================================
    // load-test 安全合同（load-E 事故修正，2026-08-02）。
    //
    // 背景：旧 oem-mode-sampler.ps1 -FixedLoad 从未实现「CPU 温度 >= 85C
    // 立即终止」的独立监督逻辑（首样本 86C 仍继续约 276s），且采样器自身
    // 产生显著负载（名义单线程 50% 占空下 _Total util 平均 57.1%）。
    //
    // 本类把安全合同实现为可离线测试的状态机（fake telemetry 注入），
    // PowerShell 监督器脚本（tools/load-test-supervisor.ps1）与负载 worker
    // 脚本（tools/load-test-worker.ps1）按同一合同实现真实双进程版本。
    //
    // 合同要点：
    // - 负载只有在模式检测、稳定、温度前置门禁全部通过后才启动；
    // - 温度前置门禁：CPU <= 70C 连续 5 分钟且无上升趋势；
    // - 运行期每秒读取 EC：CPU >= 82C 立即杀死负载；
    // - EC 连续 2 次读取失败立即杀死负载；
    // - 监督器与负载必须是不同进程（本类中 worker 通过接口隔离）；
    // - try/finally、进程退出、Ctrl+C、异常路径全部终止负载；
    // - 不依赖主采样循环结束后才检查温度；
    // - 监测到总 CPU 利用率明显超过预期时判定测试工具污染并停止。
    //
    // 本类不接触硬件：所有 IO 均通过接口注入，测试使用 fake。
    // =====================================================================

    public enum LoadTestPhase
    {
        Idle = 0,
        ModeWait = 1,
        Stabilize = 2,
        TemperaturePreflight = 3,
        Running = 4,
        Completed = 5,
        Aborted = 6,
        Invalidated = 7
    }

    public enum LoadTestAbortReason
    {
        None = 0,
        TemperatureAbort = 1,
        EcReadFailure = 2,
        WorkerExited = 3,
        SupervisorStopped = 4,
        UtilizationPollution = 5,
        UserAbort = 6,
        Error = 7
    }

    /// <summary>安全合同参数（候选值；恢复 -FixedLoad 前必须逐项评审）。</summary>
    public sealed class LoadTestSafetySettings
    {
        // 温度前置门禁：CPU 必须持续 <= 该值。
        public double PreGateMaxCpuC = 70.0;
        // 温度前置门禁：持续满足时长。
        public double PreGateContinuousSeconds = 300.0;
        // 温度前置门禁：窗口内温升不得超过该值（无上升趋势）。
        public double PreGateMaxRiseC = 1.0;
        // 运行期立即终止温度。
        public double AbortTemperatureC = 82.0;
        // EC 连续读取失败达到该次数立即终止。
        public int MaxConsecutiveEcFailures = 2;
        // 总 CPU 利用率超过该值判定测试工具污染（单线程 50% 占空在 16 线程
        // 上理论约 3%，40% 之上必为采集器/外部污染）。
        public double ExpectedMaxTotalUtilizationPercent = 40.0;
    }

    /// <summary>EC 只读温度源（真实实现=监督器脚本的子进程持续采集；测试=fake）。</summary>
    public interface ILoadTestEcSource
    {
        // 成功读取返回 true 并设置 CPU 温度；读取失败返回 false。
        bool TryReadCpuTemperature(out int cpuTemperatureC);
    }

    /// <summary>负载 worker（真实实现=独立进程；测试=fake）。</summary>
    public interface ILoadTestWorker
    {
        bool IsRunning { get; }
        void Start();
        void Kill();
    }

    /// <summary>系统总 CPU 利用率源（真实实现=持续性能计数器；测试=fake）。</summary>
    public interface ILoadTestUtilizationSource
    {
        // 返回 [0,100] 的总 CPU 利用率；不可用返回 -1。
        double ReadTotalUtilizationPercent();
    }

    public sealed class LoadTestSafetySupervisor : IDisposable
    {
        private readonly ILoadTestEcSource _ec;
        private readonly ILoadTestWorker _worker;
        private readonly ILoadTestUtilizationSource _util;
        private readonly LoadTestSafetySettings _settings;
        private readonly Func<DateTime> _clock;

        private LoadTestPhase _phase = LoadTestPhase.Idle;
        private LoadTestAbortReason _abortReason = LoadTestAbortReason.None;
        private string _detail = string.Empty;
        private int _consecutiveEcFailures;
        private bool _alive = true;
        private bool _workerStartedBySupervisor;

        // 温度前置门禁窗口状态。
        private DateTime? _preflightWindowStartUtc;
        private double? _preflightWindowStartTempC;

        public LoadTestSafetySupervisor(
            ILoadTestEcSource ec,
            ILoadTestWorker worker,
            ILoadTestUtilizationSource util,
            LoadTestSafetySettings settings = null,
            Func<DateTime> clock = null)
        {
            _ec = ec ?? throw new ArgumentNullException("ec");
            _worker = worker ?? throw new ArgumentNullException("worker");
            _util = util;
            _settings = settings ?? new LoadTestSafetySettings();
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public LoadTestPhase Phase { get { return _phase; } }
        public LoadTestAbortReason AbortReason { get { return _abortReason; } }
        public string Detail { get { return _detail ?? string.Empty; } }
        public int ConsecutiveEcFailures { get { return _consecutiveEcFailures; } }
        /// <summary>监督器存活标志：Dispose（进程退出/崩溃）后为 false，
        /// worker 侧租约检查据此停止自身。</summary>
        public bool IsSupervisorAlive { get { return _alive; } }

        /// <summary>进入 ModeWait（等待模式检测与稳定信号）。</summary>
        public void Begin()
        {
            if (_phase != LoadTestPhase.Idle)
                throw new InvalidOperationException("Begin 只能在 Idle 状态调用");
            _phase = LoadTestPhase.ModeWait;
            _detail = "等待模式检测与稳定信号";
        }

        /// <summary>模式已检测（由外部只读检测器确认）：ModeWait -> Stabilize。</summary>
        public void SignalModeDetected()
        {
            if (_phase == LoadTestPhase.ModeWait)
            {
                _phase = LoadTestPhase.Stabilize;
                _detail = "模式已检测，进入稳定阶段";
                return;
            }
            throw new InvalidOperationException("信号顺序错误：当前阶段 " + _phase);
        }

        /// <summary>稳定阶段完成：Stabilize -> TemperaturePreflight。
        /// 任何阶段 worker 都不得运行（由 Tick 保证）。</summary>
        public void CompleteStabilization()
        {
            if (_phase != LoadTestPhase.Stabilize)
                throw new InvalidOperationException("稳定未开始：当前阶段 " + _phase);
            _phase = LoadTestPhase.TemperaturePreflight;
            _detail = "稳定完成，进入温度前置门禁";
        }

        /// <summary>驱动状态机（真实脚本每秒调用一次；测试按需调用）。
        /// 只在 TemperaturePreflight 通过后启动 worker；Running 期内任何
        /// 违反合同的事件在同一 Tick 内终止 worker。</summary>
        public void Tick()
        {
            Tick(_clock());
        }

        public void Tick(DateTime nowUtc)
        {
            switch (_phase)
            {
                case LoadTestPhase.Idle:
                case LoadTestPhase.ModeWait:
                case LoadTestPhase.Stabilize:
                    // 未通过前置门禁：worker 绝不启动（合同测试点）。
                    return;

                case LoadTestPhase.TemperaturePreflight:
                    TickTemperaturePreflight(nowUtc);
                    return;

                case LoadTestPhase.Running:
                    TickRunning(nowUtc);
                    return;

                default:
                    // Completed / Aborted / Invalidated：静止。
                    return;
            }
        }

        private void TickTemperaturePreflight(DateTime nowUtc)
        {
            int temp;
            if (!ReadEc(out temp))
            {
                // EC 失败计数连续 2 次即中止（负载尚未启动，无需 Kill）。
                if (_consecutiveEcFailures >= _settings.MaxConsecutiveEcFailures)
                {
                    Abort(LoadTestAbortReason.EcReadFailure,
                        "温度前置门禁：EC 连续 " + _consecutiveEcFailures + " 次读取失败，中止（负载未启动）");
                }
                return;
            }

            if (temp > _settings.PreGateMaxCpuC)
            {
                ResetPreflightWindow(nowUtc, temp);
                _detail = "温度前置门禁：CPU " + temp + "C > " + _settings.PreGateMaxCpuC + "C，窗口重置";
                return;
            }

            if (!_preflightWindowStartUtc.HasValue)
            {
                ResetPreflightWindow(nowUtc, temp);
                _detail = "温度前置门禁：开始 5 分钟连续窗口（CPU " + temp + "C）";
                return;
            }

            // 无上升趋势检查：窗口内温升不得超过 PreGateMaxRiseC，否则重置。
            if (temp > _preflightWindowStartTempC.Value + _settings.PreGateMaxRiseC)
            {
                ResetPreflightWindow(nowUtc, temp);
                _detail = "温度前置门禁：CPU 上升至 " + temp + "C（起点 " +
                    _preflightWindowStartTempC.Value + "C），窗口重置";
                return;
            }

            double elapsed = (nowUtc - _preflightWindowStartUtc.Value).TotalSeconds;
            if (elapsed >= _settings.PreGateContinuousSeconds)
            {
                // 全部门禁通过：启动负载 worker（独立进程由调用方保证）。
                _worker.Start();
                _workerStartedBySupervisor = true;
                _phase = LoadTestPhase.Running;
                _consecutiveEcFailures = 0;
                _detail = "温度前置门禁通过（CPU <= " + _settings.PreGateMaxCpuC +
                    "C 持续 " + _settings.PreGateContinuousSeconds + "s，无上升趋势），负载已启动";
            }
            else
            {
                _detail = "温度前置门禁：已持续 " + Math.Round(elapsed) + "s / " +
                    _settings.PreGateContinuousSeconds + "s（CPU " + temp + "C）";
            }
        }

        private void TickRunning(DateTime nowUtc)
        {
            // 1) worker 意外退出：任何 Tick 内发现即中止。
            if (!_worker.IsRunning)
            {
                Abort(LoadTestAbortReason.WorkerExited,
                    "worker 意外退出（监督器存活但 worker 停止）");
                return;
            }

            int temp;
            if (!ReadEc(out temp))
            {
                if (_consecutiveEcFailures >= _settings.MaxConsecutiveEcFailures)
                {
                    KillWorker(LoadTestAbortReason.EcReadFailure,
                        "EC 连续 " + _consecutiveEcFailures + " 次读取失败，立即杀死负载");
                }
                return;
            }

            // 2) 温度超限：同一 Tick 内立即终止（不依赖采样循环结束）。
            if (temp >= _settings.AbortTemperatureC)
            {
                KillWorker(LoadTestAbortReason.TemperatureAbort,
                    "CPU " + temp + "C >= " + _settings.AbortTemperatureC + "C，立即杀死负载");
                return;
            }

            // 3) 工具污染检测：总 CPU 利用率明显超过预期。
            if (_util != null)
            {
                double util = _util.ReadTotalUtilizationPercent();
                if (util >= 0 && util > _settings.ExpectedMaxTotalUtilizationPercent)
                {
                    KillWorker(LoadTestAbortReason.UtilizationPollution,
                        "总 CPU 利用率 " + Math.Round(util, 1) + "% 超过预期上限 " +
                        _settings.ExpectedMaxTotalUtilizationPercent + "%，判定测试工具污染，停止",
                        LoadTestPhase.Invalidated);
                    return;
                }
            }

            _detail = "监督运行中（CPU " + temp + "C）";
        }

        private bool ReadEc(out int temp)
        {
            if (_ec.TryReadCpuTemperature(out temp))
            {
                _consecutiveEcFailures = 0;
                return true;
            }
            _consecutiveEcFailures++;
            return false;
        }

        private void ResetPreflightWindow(DateTime nowUtc, int temp)
        {
            _preflightWindowStartUtc = nowUtc;
            _preflightWindowStartTempC = temp;
        }

        /// <summary>正常结束/Ctrl+C：终止负载并进入 Completed。
        /// 若运行中出现异常，调用方应使用 AbortOnError。</summary>
        public void RequestStop(string reason)
        {
            _detail = "停止请求：" + (reason ?? "用户中止");
            if (_phase == LoadTestPhase.Running && _workerStartedBySupervisor)
            {
                KillWorkerInternal();
                _phase = LoadTestPhase.Completed;
                _abortReason = LoadTestAbortReason.None;
                _detail += "；负载已终止";
            }
            else
            {
                // 非 Running 阶段（含 preflight）：无 worker，直接完成。
                _phase = LoadTestPhase.Completed;
            }
        }

        /// <summary>异常路径（try/catch）：任何阶段都终止负载。</summary>
        public void AbortOnError(string detail)
        {
            if (_phase == LoadTestPhase.Running && _workerStartedBySupervisor)
                KillWorkerInternal();
            _phase = LoadTestPhase.Aborted;
            _abortReason = LoadTestAbortReason.Error;
            _detail = "异常中止：" + (detail ?? string.Empty);
        }

        /// <summary>进程退出/崩溃（模拟监督器死亡）：worker 不得继续运行。
        /// 真实实现中 worker 侧租约检查 IsSupervisorAlive/心跳文件。</summary>
        public void Dispose()
        {
            if (!_alive)
                return;
            _alive = false;
            if (_phase == LoadTestPhase.Running && _workerStartedBySupervisor)
                KillWorkerInternal();
            if (_phase == LoadTestPhase.Running || _phase == LoadTestPhase.TemperaturePreflight)
            {
                _phase = LoadTestPhase.Aborted;
                _abortReason = LoadTestAbortReason.SupervisorStopped;
                _detail = "监督器退出：worker 已终止，不得继续运行";
            }
        }

        private void Abort(LoadTestAbortReason reason, string detail)
        {
            _phase = LoadTestPhase.Aborted;
            _abortReason = reason;
            _detail = detail;
        }

        private void KillWorker(LoadTestAbortReason reason, string detail, LoadTestPhase phase = LoadTestPhase.Aborted)
        {
            KillWorkerInternal();
            _phase = phase;
            _abortReason = reason;
            _detail = detail;
        }

        private void KillWorkerInternal()
        {
            try
            {
                if (_worker != null && _worker.IsRunning)
                    _worker.Kill();
            }
            catch
            {
                // Kill 失败由 worker 侧租约兜底（supervisor 心跳消失后 worker 自退）。
            }
        }
    }
}
