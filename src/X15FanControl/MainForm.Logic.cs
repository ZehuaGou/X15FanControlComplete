using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using X15FanCore.Control;
using X15FanCore.Models;
using X15FanCore.Native;

namespace X15FanControl
{
    public partial class MainForm
    {
        private void InitializeEc()
        {
            try
            {
                DisposeEc();
                Interlocked.Exchange(ref _ecFaulted, 0);
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
                _ecQueue = new EcAccessQueue(dllPath);
                if (!_ecQueue.Ready.Wait(10000) || !_ecQueue.IsReady)
                    throw new TimeoutException("EC worker initialization timed out.");
                _hardwareStatusLabel.Text = "硬件：EC 已初始化；风扇通道数：" + EcGetCount() + "。CPU=1，GPU=2。";
                _hardwareStatusLabel.ForeColor = Color.DarkGreen;
                AppendLog("EC 初始化成功。");
            }
            catch (Exception exception)
            {
                DisposeEc();
                _hardwareStatusLabel.Text = "硬件不可用：" + exception.Message;
                _hardwareStatusLabel.ForeColor = Color.DarkRed;
                AppendLog("EC 初始化失败：" + exception);
            }
        }

        private const int EcOperationTimeoutMilliseconds = 5000;

        private bool IsEcReady()
        {
            return Volatile.Read(ref _ecFaulted) == 0 && _ecQueue != null && _ecQueue.IsReady;
        }

        private void MarkEcQueueFault(string operationName, long elapsedMilliseconds)
        {
            if (Interlocked.Exchange(ref _ecFaulted, 1) == 0)
            {
                try { _ecQueue?.Fault(); } catch { }
                AppendLog("EC队列已熔断：" + operationName + " 超时 " + elapsedMilliseconds + "ms；停止后续EC请求，交由看门狗保护。");
            }
        }

        // The queue owns the native object. Callers have bounded waits, while
        // the native call itself is never aborted from a foreign thread.
        private T ExecuteEc<T>(string name, Func<ClevoEcInfo, T> operation)
        {
            if (!IsEcReady()) throw new InvalidOperationException("EC尚未初始化。");
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool completed = false;
            try
            {
                T result = _ecQueue.Execute(name, operation, EcOperationTimeoutMilliseconds, CancellationToken.None);
                completed = true;
                return result;
            }
            catch (TimeoutException)
            {
                MarkEcQueueFault(name, stopwatch.ElapsedMilliseconds);
                throw;
            }
            finally
            {
                if (completed) RecordEcActivity();
                LogSlowEcOperation(name, stopwatch.ElapsedMilliseconds);
            }
        }

        private async Task<T> ExecuteEcAsync<T>(string name, Func<ClevoEcInfo, T> operation, CancellationToken token, EcAccessPriority priority = EcAccessPriority.Control)
        {
            if (!IsEcReady()) throw new InvalidOperationException("EC尚未初始化。");
            Stopwatch stopwatch = Stopwatch.StartNew();
            Task<T> operationTask = _ecQueue.ExecuteAsync(name, operation, token, priority);
            Task completed = await Task.WhenAny(
                operationTask,
                Task.Delay(EcOperationTimeoutMilliseconds, CancellationToken.None)).ConfigureAwait(false);
            if (completed != operationTask)
            {
                MarkEcQueueFault(name, stopwatch.ElapsedMilliseconds);
                LogSlowEcOperation(name, stopwatch.ElapsedMilliseconds);
                throw new TimeoutException(name + " exceeded " + EcOperationTimeoutMilliseconds + "ms.");
            }

            bool operationCompleted = false;
            try
            {
                T result = await operationTask.ConfigureAwait(false);
                operationCompleted = true;
                return result;
            }
            catch (TimeoutException)
            {
                MarkEcQueueFault(name, stopwatch.ElapsedMilliseconds);
                throw;
            }
            finally
            {
                if (operationCompleted) RecordEcActivity();
                LogSlowEcOperation(name, stopwatch.ElapsedMilliseconds);
            }
        }

        private void LogSlowEcOperation(string name, long elapsedMilliseconds)
        {
            if (elapsedMilliseconds >= 1000)
                AppendLog("EC阶段耗时：" + name + " " + elapsedMilliseconds + "ms");
        }

        private int EcGetCount()
        {
            return ExecuteEc("GetFanCount", ec => ec.GetFanCount());
        }

        private EcData EcReadRaw(int ch)
        {
            return ExecuteEc("ReadRaw ch" + ch, ec => ec.ReadRaw(ch));
        }

        private int EcGetDuty(int ch)
        {
            return ExecuteEc("GetDuty ch" + ch, ec => ec.GetDutyPercent(ch));
        }

        private int EcGetCpuRpmLocked()
        {
            return ExecuteEc("GetCpuRpm", ec => ec.GetCpuRpm());
        }

        private int EcGetGpuRpmLocked()
        {
            return ExecuteEc("GetGpuRpm", ec => ec.GetGpuRpm());
        }

        private void EcSetFanPercent(int ch, int pct)
        {
            ExecuteEc("SetFanPercent ch" + ch, ec =>
            {
                ec.SetFanPercent(ch, pct);
                return true;
            });
        }

        private void EcSetFanAuto(int ch)
        {
            ExecuteEc("SetFanAuto ch" + ch, ec =>
            {
                ec.SetFanAuto(ch);
                return true;
            });
        }

        private void EcRestoreAllAuto()
        {
            ExecuteEc("RestoreAllAuto", ec =>
            {
                ec.RestoreAllAuto();
                return true;
            });
        }

        private void RecordEcActivity()
        {
            Interlocked.Exchange(ref _lastEcActivityUtcTicks, DateTime.UtcNow.Ticks);
        }

        private void EcRestoreCalibrationAuto(int channel)
        {
            ExecuteEc("RestoreCalibrationAuto", ec =>
            {
                ec.SetFanAuto(channel);
                // RestoreAllAuto内部会容错吞掉单通道异常；这里显式写两个通道，
                // 只有对应通道和另一个通道均未抛出时，才允许窗口状态继续变化。
                ec.SetFanAuto(channel == 1 ? 2 : 1);
                return true;
            });
        }

        // 异步EC访问版本（用于初始化路径，不阻塞UI线程）
        private async System.Threading.Tasks.Task<int> EcGetCountAsync()
        {
            return await ExecuteEcAsync("GetFanCount", ec => ec.GetFanCount(), CancellationToken.None);
        }

        private int EcGetTemperatureC(int fanNumber)
        {
            return ExecuteEc("GetTemperature ch" + fanNumber, ec => ec.GetTemperatureC(fanNumber));
        }

        private void PopulateModeCombo()
        {
            _modeCombo.Items.Clear();
            _modeCombo.Items.Add(RunMode.ReadOnly);
            _modeCombo.Items.Add(RunMode.Simulation);
            _modeCombo.Items.Add(RunMode.Active);
        }

        private void ApplyModeButtonClick(object sender, EventArgs e)
        {
            if (_modeCombo.SelectedItem == null)
            {
                return;
            }

            RunMode requestedMode = (RunMode)_modeCombo.SelectedItem;
            SetRunMode(requestedMode, "User request");

            // Only a successfully applied user choice becomes the startup preference.
            // Safety fallbacks use SetRunMode directly, so they never overwrite it.
            if (_runMode == requestedMode)
            {
                _config.StartupMode = requestedMode;
                _config.AutoEnterActiveOnStartup = requestedMode == RunMode.Active;
                if (SaveConfig())
                {
                    AppendLog("已保存启动模式：" + requestedMode + "。");
                }
            }
        }

        private void SetRunMode(
            RunMode requestedMode,
            string reason,
            bool restoreEc = true,
            bool stopWatchdog = true)
        {
            if (requestedMode == RunMode.Active)
            {
                if (!IsEcReady())
                {
                    MessageBox.Show("EC 接口不可用，无法启动活动模式。", "X15 风扇控制", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    requestedMode = RunMode.ReadOnly;
                }
                else
                {
                    if (_controlCenterLease == null || !_controlCenterLease.IsAcquired)
                    {
                        MessageBox.Show(
                            "Control Center 的频率/风扇控制尚未成功让出，Active 模式不会启动。请查看日志并关闭占用它的组件后重试。",
                            "Control Center 接管失败",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        requestedMode = RunMode.ReadOnly;
                    }

                    IList<string> conflicts = ConflictDetector.FindConflicts();
                    if (requestedMode == RunMode.Active && conflicts.Count > 0)
                    {
                        MessageBox.Show(
                            "活动模式被阻止，因为另一个风扇控制进程正在运行：\r\n\r\n" + string.Join("\r\n", conflicts) +
                            "\r\n\r\n请先关闭这些进程。两个控制器绝不能同时写入 EC。",
                            "风扇控制器冲突",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        requestedMode = RunMode.ReadOnly;
                    }
                    else
                    {
                        GpuTelemetryData telemetry = _gpuTelemetry?.Latest;
                        bool telemetryInvalid = telemetry == null || !telemetry.IsAvailable || telemetry.IsStale ||
                                                telemetry.TemperatureC < 10 || telemetry.TemperatureC > 100;
                        if (telemetryInvalid)
                        {
                            string reasonText = telemetry?.ErrorMessage ?? "GPU遥测数据不可用";
                            MessageBox.Show(
                                "GPU 遥测不可用：" + reasonText + "\r\n\r\n活动模式不会启动，风扇保持原厂自动控制。",
                                "GPU传感器异常",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                            requestedMode = RunMode.ReadOnly;
                        }
                        // The explicit Active selection is the user's confirmation.
                        // EC availability, controller conflicts and telemetry validity
                        // above remain mandatory safety gates.
                    }
                }
            }

            // ReadOnly/Simulation 必须先使验证任务失效，再交还EC
            if (requestedMode != RunMode.Active)
            {
                StopHeartbeatMonitor();
                InvalidateVerificationTasks();
                bool preserveWatchdogForEcFault = Volatile.Read(ref _ecFaulted) != 0;
                if (restoreEc && IsEcReady())
                {
                    try { EcRestoreAllAuto(); }
                    catch (Exception exception) { AppendLog("切换只读时恢复自动失败：" + exception.Message); }
                }
                else if (!IsEcReady())
                {
                    AppendLog("EC不可用，跳过同步恢复自动；独立看门狗负责故障保护。");
                }
                if (stopWatchdog && !preserveWatchdogForEcFault)
                {
                    StopWatchdog();
                    _heartbeat?.WriteStop();
                }
                else
                {
                    _controlCts?.Cancel();
                    AppendLog("EC熔断后保留看门狗运行，等待心跳过期并恢复自动。");
                }
            }

            _runMode = requestedMode;
            if (_runMode != RunMode.Active)
                RestoreAdaptivePowerPolicy();
            lock (_engineLock) { _engine?.Reset(); }
            _modeCombo.SelectedItem = _runMode;
            UpdateModeStatus();
            FlashModeBadge();

            if (_runMode == RunMode.Active)
            {
                _heartbeat?.WriteActive(Process.GetCurrentProcess().Id);
                Interlocked.Exchange(ref _watchdogFailureHandling, 0);
                StartHeartbeatMonitor();
                StartWatchdog();
            }

            AppendLog("运行模式切换为 " + _runMode + "。原因：" + reason + "。");
        }

    private void MainTimerTick(object sender, EventArgs e)
        {
            if (_closing) return;

            // 窗口隐藏或最小化时：不刷新UI，但后台控制循环继续
            bool windowActive = Visible && ShowInTaskbar && WindowState != FormWindowState.Minimized;
            if (!windowActive) return;

            // 只负责UI刷新：从后台控制循环取最新快照显示
            FanSnapshot snapshot;
            ControlDecision decision;

            lock (_latestLock)
            {
                snapshot = _latestSnapshot;
                decision = _latestDecision;
            }

            // 校准模式：UI Timer负责驱动校准（从后台快照取数据）
            if (_calibrationActive)
            {
                if (snapshot != null)
                {
                    CalibrationTick(snapshot);
                    UpdateDashboard(snapshot, null);
                }
                return;
            }

            if (snapshot == null) return;

            // 标签刷新
            UpdateDashboard(snapshot, decision);

            // 图表：时间戳间隔采样，不在UpdateDashboard中调用AddHistoryPoint
            TimeSpan chartInterval = TimeSpan.FromMilliseconds(_config.ChartSampleIntervalMs);
            if (decision != null && DateTime.UtcNow - _lastChartSampleUtc >= chartInterval)
            {
                AddHistoryPoint("CPU 温度", snapshot.CpuTemperatureC);
                AddHistoryPoint("GPU 温度", _gpuTelemetryReady ? snapshot.GpuTemperatureC : 0);
                AddHistoryPoint("CPU 设定", decision.Cpu.AppliedPercent);
                AddHistoryPoint("GPU 设定", decision.Gpu.AppliedPercent);
                _lastChartSampleUtc = DateTime.UtcNow;
            }
        }

        // 启动后台控制循环（替换UI线程中的EC读取和写入）
        private void StartBackgroundControl()
        {
            if (_controlCts != null) return;
            _controlCts = new System.Threading.CancellationTokenSource();
            _controlTask = Task.Run(() => ControlLoopAsync(_controlCts.Token));
        }

        private async Task ControlLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 防止重入
                if (Interlocked.Exchange(ref _controlLoopGuard, 1) != 0)
                {
                    try { await Task.Delay(100, token); } catch { break; }
                    continue;
                }

                Stopwatch cycleStopwatch = Stopwatch.StartNew();
                try
                {
                    if (_closing || !IsEcReady())
                    {
                        try { await Task.Delay(500, token); } catch { break; }
                        continue;
                    }

                    DateTime now = DateTime.UtcNow;
                    FanSnapshot snapshot = await ReadSnapshotAsync(now, token);

                    // 退出检查：ReadSnapshot之后必须确认未请求退出
                    if (_closing || token.IsCancellationRequested) break;

                    DetectSensorStalls(snapshot);

                    // Power policy is deliberately independent from the fan
                    // safety controller. It may request a slower CPU state,
                    // but temperature emergency stages below still ramp fans
                    // immediately when needed.
                    if (!_calibrationActive)
                        UpdateAdaptivePowerPolicy(snapshot, now);

                    ControlDecision decision = null;

                    if (_calibrationActive)
                    {
                        // Calibration runs on UI thread via timer; skip here
                    }
                    else
                    {
                        lock (_engineLock)
                        {
                            decision = _engine.Update(snapshot);
                        }

                        // Publish the latest async write-verification outcome.
                        // The readback lags one control cycle by design: the
                        // verification task samples ~1s after a write.
                        decision.Cpu.EcReadbackPercent = Volatile.Read(ref _cpuLastReadbackPercent);
                        decision.Gpu.EcReadbackPercent = Volatile.Read(ref _gpuLastReadbackPercent);
                        decision.Cpu.ExternalOverrideDetected = Volatile.Read(ref _cpuOverrideDetected) != 0;
                        decision.Gpu.ExternalOverrideDetected = Volatile.Read(ref _gpuOverrideDetected) != 0;
                        if (decision.Cpu.ExternalOverrideDetected &&
                            decision.Cpu.State != ControlState.Emergency &&
                            decision.Cpu.State != ControlState.InvalidSensor)
                            decision.Cpu.State = ControlState.ExternalOverride;
                        if (decision.Gpu.ExternalOverrideDetected &&
                            decision.Gpu.State != ControlState.Emergency &&
                            decision.Gpu.State != ControlState.InvalidSensor)
                            decision.Gpu.State = ControlState.ExternalOverride;

                        ApplyCpuRpmSafetyGuard(snapshot, decision, now);

                        if (decision.RequestAutoFallback)
                        {
                            // 必须回到UI线程执行安全操作
                            BeginInvoke(new Action(() =>
                            {
                                RestoreAuto("控制器请求自动故障保护");
                                SetRunMode(RunMode.ReadOnly, "传感器故障保护", false);
                            }));
                            try { await Task.Delay(2000, token); } catch { break; }
                            continue;
                        }

                        if (_runMode == RunMode.Active)
                        {
                            if (HasWatchdogExitedUnexpectedly())
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    if (_runMode != RunMode.Active) return;
                                    RestoreAuto("看门狗异常退出");
                                    SetRunMode(RunMode.ReadOnly, "看门狗故障保护", false);
                                }));
                                try { await Task.Delay(500, token); } catch { break; }
                                continue;
                            }

                            // 执行WriteDecision前再次确认非退出状态
                            if (_closing || token.IsCancellationRequested) break;
                            await WriteDecisionAsync(decision, snapshot, now, token);
                            _heartbeat?.WriteActive(Process.GetCurrentProcess().Id);
                        }

                        _csvLogger?.Write(snapshot, decision);
                    }

                    // 安全发布最新快照给UI线程
                    lock (_latestLock)
                    {
                        _latestSnapshot = snapshot;
                        _latestDecision = decision;
                        _lastSnapshot = snapshot;
                    }
                    _lastTickUtc = now;
                    Interlocked.Exchange(ref _lastControlProgressUtcTicks, now.Ticks);

                    // 控制频率由 PollIntervalMs 决定
                    int delayMs = Math.Max(100, Math.Min(2000, _config.PollIntervalMs));
                    try { await Task.Delay(delayMs, token); } catch { break; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AppendLog("后台控制循环错误：" + ex.Message);
                    bool keepWatchdogForEcFallback = Volatile.Read(ref _ecFaulted) != 0;
                    BeginInvoke(new Action(() =>
                    {
                        if (_runMode != RunMode.Active) return;
                        if (!keepWatchdogForEcFallback)
                        {
                            RestoreAuto("控制循环异常");
                        }
                        SetRunMode(
                            RunMode.ReadOnly,
                            "异常故障保护",
                            false,
                            !keepWatchdogForEcFallback);
                    }));
                    try { await Task.Delay(2000, token); } catch { break; }
                }
                finally
                {
                    if (cycleStopwatch.ElapsedMilliseconds > 5000)
                    {
                        AppendLog("控制循环周期过长：" + cycleStopwatch.ElapsedMilliseconds + "ms；已接近看门狗超时阈值。" );
                    }
                    Interlocked.Exchange(ref _controlLoopGuard, 0);
                }
            }

            Interlocked.Exchange(ref _controlLoopGuard, 0);
        }

        private void ApplyCpuRpmSafetyGuard(FanSnapshot snapshot, ControlDecision decision, DateTime nowUtc)
        {
            if (snapshot == null || decision == null || decision.Cpu == null || _runMode != RunMode.Active)
                return;

            // A high CPU temperature combined with a very low tachometer value
            // must never allow the controller to keep a quiet duty. The RPM
            // read is diagnostic and can be stale, so require two consecutive
            // control samples, then force a safe fan duty and refresh it only
            // every few seconds until the tachometer recovers.
            bool lowRpmAtHighTemperature = snapshot.CpuTemperatureC >= 75 && snapshot.CpuRpm < 1000;
            if (!lowRpmAtHighTemperature)
            {
                if (snapshot.CpuTemperatureC <= 72 || snapshot.CpuRpm >= 1200)
                {
                    _cpuLowRpmSafetySamples = 0;
                    _cpuLowRpmSafetyActive = false;
                }
                return;
            }

            _cpuLowRpmSafetySamples = Math.Min(_cpuLowRpmSafetySamples + 1, 3);
            if (_cpuLowRpmSafetySamples < 2)
                return;

            bool refreshWrite = !_cpuLowRpmSafetyActive ||
                                nowUtc >= _lastCpuLowRpmSafetyWriteUtc.AddSeconds(5);
            _cpuLowRpmSafetyActive = true;
            decision.Cpu.WritePercent = Math.Max(decision.Cpu.WritePercent, 90);
            decision.Cpu.State = ControlState.Emergency;
            decision.Cpu.Reason = DecisionReason.RpmSafety;
            decision.Cpu.Detail = "CPU RPM 回读过低，已启用 90% 安全转速";

            if (refreshWrite)
            {
                decision.Cpu.ShouldWrite = true;
                _lastCpuLowRpmSafetyWriteUtc = nowUtc;
                AppendLog("CPU RPM保护：温度=" + snapshot.CpuTemperatureC +
                          "°C，回读=" + snapshot.CpuRpm +
                          " RPM；已强制 CPU 风扇至少 90%。");
            }
        }

        private void TryAutoActive()
        {
            if (!IsEcReady())
            {
                AppendLog("自动Active失败：EC未初始化");
                return;
            }

            // 与手动 Active 路径一致：Control Center 未让出控制权时拒绝启动。
            // 手动路径 SetRunMode 会弹窗说明，自启路径静默记录后保持只读。
            if (_controlCenterLease == null || !_controlCenterLease.IsAcquired)
            {
                AppendLog("自动Active失败：Control Center 尚未让出控制权");
                return;
            }

            // 检查冲突进程
            var conflicts = ConflictDetector.FindConflicts();
            if (conflicts.Count > 0)
            {
                AppendLog("自动Active失败：检测到冲突进程 " + string.Join(", ", conflicts));
                return;
            }

            // 检查GPU遥测
            var telemetry = _gpuTelemetry?.Latest;
            if (telemetry == null || !telemetry.IsAvailable || telemetry.IsStale)
            {
                AppendLog("自动Active失败：GPU遥测不可用");
                return;
            }

            // 检查温度有效性
            if (telemetry.TemperatureC < 10 || telemetry.TemperatureC > 100)
            {
                AppendLog("自动Active失败：GPU温度读数异常 " + telemetry.TemperatureC + "°C");
                return;
            }

            // 所有检查通过，切换Active（不弹确认框）
            _runMode = RunMode.Active;
            lock (_engineLock) { _engine?.Reset(); }
            _modeCombo.SelectedItem = _runMode;
            UpdateModeStatus();
            FlashModeBadge();
            _heartbeat?.WriteActive(Process.GetCurrentProcess().Id);
            Interlocked.Exchange(ref _watchdogFailureHandling, 0);
            StartHeartbeatMonitor();
            StartWatchdog();
            AppendLog("已恢复用户保存的 Active 启动模式。");
        }

        private void DetectSensorStalls(FanSnapshot snapshot)
        {
            // GPU温度有效性由GpuTelemetryClient的IsAvailable/IsStale管理
            // 这里只做CPU温和的稳定提示（GPU温度不变是正常的）
            if (snapshot.CpuTemperatureC == _lastCpuTemp)
            {
                _cpuTempStallCount++;
                if (_cpuTempStallCount >= 120 && _cpuTempStallCount % 40 == 0)
                {
                    AppendLog("提示：CPU温度读数在 " + snapshot.CpuTemperatureC + "°C 已稳定超过 60 秒（可能正常）。");
                }
            }
            else
            {
                _cpuTempStallCount = 0;
            }
            _lastCpuTemp = snapshot.CpuTemperatureC;
        }

        // 真正的异步写入验证（使用Task.Delay，非阻塞）
        // 每个通道最多一个有效验证任务，新写入会取消旧任务
        private void StartWriteVerification(int channel, int requestedPercent, int beforeDuty, int beforeRpm)
        {
            int seqId = System.Threading.Interlocked.Increment(ref _ecSequenceId);
            bool detailedLog = _config.DetailedVerificationLogging;

            CancellationTokenSource cts;
            CancellationTokenSource previousCts = null;
            lock (_verificationLock)
            {
                if (channel == 1)
                {
                    _latestCpuVerificationSequence = seqId;
                    previousCts = _cpuVerificationCts;
                    _cpuVerificationCts = new CancellationTokenSource();
                    cts = _cpuVerificationCts;
                }
                else
                {
                    _latestGpuVerificationSequence = seqId;
                    previousCts = _gpuVerificationCts;
                    _gpuVerificationCts = new CancellationTokenSource();
                    cts = _gpuVerificationCts;
                }
            }

            // Cancel outside the lock. The queue will skip a request that has
            // not started yet, while an in-flight native call remains owned by
            // its worker thread.
            if (previousCts != null)
            {
                try { previousCts.Cancel(); } catch { }
            }

            CancellationToken token = cts.Token;
            var stopwatch = Stopwatch.StartNew();

            // 在后台线程执行异步验证，不影响控制循环
            Task verificationTask = System.Threading.Tasks.Task.Run(async () =>
            {
                bool verificationGateHeld = false;
                try
                {
                    await _verificationEcGate.WaitAsync(token);
                    verificationGateHeld = true;

                    // 检查是否已被取代
                    if (!IsLatestVerification(channel, seqId, token)) return;

                    // 50ms回读：确认写入立即生效
                    await System.Threading.Tasks.Task.Delay(50, token);
                    if (!IsLatestVerification(channel, seqId, token)) return;
                    int duty50 = 0;
                    duty50 = (await ExecuteEcAsync("Verify ch" + channel + " ReadRaw 50ms", ec => ec.ReadRaw(channel), token, EcAccessPriority.Verification)).FanDuty;
                    double pct50 = duty50 * 100.0 / 255.0;
                    long delay50 = stopwatch.ElapsedMilliseconds;
                    if (detailedLog) LogVerification(seqId, channel, "50ms", requestedPercent, duty50, pct50, beforeDuty, delay50, 0, beforeRpm);

                    // 1000ms回读：写入稳定后是否仍保持，同时检测外部覆盖
                    await System.Threading.Tasks.Task.Delay(950, token); // 50+950=1000
                    if (!IsLatestVerification(channel, seqId, token)) return;
                    int duty1000 = 0;
                    duty1000 = (await ExecuteEcAsync("Verify ch" + channel + " ReadRaw 1000ms", ec => ec.ReadRaw(channel), token, EcAccessPriority.Verification)).FanDuty;
                    double pct1000 = duty1000 * 100.0 / 255.0;
                    long delay1000 = stopwatch.ElapsedMilliseconds;
                    if (detailedLog) LogVerification(seqId, channel, "1000ms", requestedPercent, duty1000, pct1000, beforeDuty, delay1000, 0, beforeRpm);
                    AppendLog($"  [seq={seqId}] [{delay1000}ms] 最终EC占空：{duty1000}({pct1000:F1}%) (目标={requestedPercent}%)");

                    // 外部覆盖检测：50ms匹配但1000ms被改回
                    if (Math.Abs(pct50 - requestedPercent) <= 2.0 && Math.Abs(pct1000 - requestedPercent) > 3.0)
                        AppendLog($"  [seq={seqId}] ⚠ 外部覆盖检测: 50ms差异={Math.Abs(pct50 - requestedPercent):F1}%, 1000ms差异={Math.Abs(pct1000 - requestedPercent):F1}%, 写入被改回");

                    // 将回读结果与覆盖判定发布给控制循环/仪表盘/CSV。
                    // CheckExternalOverride 内部累计连续失配，确认后才置真，
                    // 因此单次瞬态回读不会误报。引擎状态必须与 Update 串行。
                    bool overridden = false;
                    lock (_engineLock)
                    {
                        if (_engine != null)
                        {
                            overridden = channel == 1
                                ? _engine.CheckCpuExternalOverride(pct1000)
                                : _engine.CheckGpuExternalOverride(pct1000);
                        }
                    }
                    if (channel == 1)
                    {
                        Interlocked.Exchange(ref _cpuLastReadbackPercent, (int)Math.Round(pct1000));
                        Interlocked.Exchange(ref _cpuOverrideDetected, overridden ? 1 : 0);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _gpuLastReadbackPercent, (int)Math.Round(pct1000));
                        Interlocked.Exchange(ref _gpuOverrideDetected, overridden ? 1 : 0);
                    }

                    if (overridden && Interlocked.Exchange(ref _overrideFallbackHandling, 1) == 0)
                    {
                        string channelName = channel == 1 ? "CPU" : "GPU";
                        AppendLog($"  [seq={seqId}] ⚠ {channelName} 外部覆盖已确认：EC占空被其他控制器持续改回，切换只读并恢复自动。");
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (_closing || _runMode != RunMode.Active) return;
                                RestoreAuto("外部覆盖");
                                SetRunMode(RunMode.ReadOnly, "外部覆盖故障保护", false);
                            }));
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException)
                {
                    AppendLog($"  [seq={seqId}] 验证被更新写入取代 (Superseded)");
                }
                catch (Exception ex)
                {
                    AppendLog($"  [seq={seqId}] 异步验证异常: {ex.Message}");
                }
                finally
                {
                    if (verificationGateHeld)
                    {
                        _verificationEcGate.Release();
                    }
                }
            });

            _ = verificationTask.ContinueWith(
                _ => cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // 检查当前验证任务是否仍然是最新的
        private bool IsLatestVerification(int channel, int seqId, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            if (_closing) return false;

            lock (_verificationLock)
            {
                int latestSeq = channel == 1 ? _latestCpuVerificationSequence : _latestGpuVerificationSequence;
                if (seqId != latestSeq) return false;
            }
            return true;
        }

        // 使所有验证任务失效（切换模式、退出、恢复Auto时调用）
        private void InvalidateVerificationTasks()
        {
            CancellationTokenSource cpuCts;
            CancellationTokenSource gpuCts;
            lock (_verificationLock)
            {
                _latestCpuVerificationSequence = -1;
                _latestGpuVerificationSequence = -1;
                cpuCts = _cpuVerificationCts;
                gpuCts = _gpuVerificationCts;
                _cpuVerificationCts = null;
                _gpuVerificationCts = null;
            }

            // The verification task disposes its own CTS after it exits.  Do
            // not dispose it while a canceled task may still be in WaitAsync.
            try { cpuCts?.Cancel(); } catch { }
            try { gpuCts?.Cancel(); } catch { }
        }

        private void LogVerification(int seqId, int ch, string label, int target, int duty, double pct, int beforeDuty, long actualDelayMs, int rpm, int beforeRpm)
        {
            double diff = Math.Abs(pct - target);
            AppendLog(string.Format("  [seq={0}] [{1}] Ch{2} 写入前占空={3}({4:F1}%), 目标={5}%, EC回读={6}({7:F1}%), 差异={8:F1}%, 实际延迟={9}ms, RPM={10}(前{11})",
                seqId, label, ch, beforeDuty, beforeDuty * 100.0 / 255.0, target, duty, pct, diff, actualDelayMs,
                rpm > 0 ? rpm.ToString() : "—", beforeRpm > 0 ? beforeRpm.ToString() : "—"));
        }

        private async Task<FanSnapshot> ReadSnapshotAsync(DateTime timestampUtc, CancellationToken token)
        {
            // Keep one snapshot coherent enough for the controller, but yield
            // between every native phase so the queue remains observable and
            // cancellation can stop work that has not started yet.
            EcData cpuRaw = await ExecuteEcAsync("Snapshot ReadRaw CPU", ec => ec.ReadRaw(1), token);
            EcData gpuRaw = await ExecuteEcAsync("Snapshot ReadRaw GPU", ec => ec.ReadRaw(2), token);
            // ReadRaw already contains the duty byte. Reusing it removes two
            // duplicate native EC transactions from every polling cycle.
            int cpuDuty = NormalizeDutyPercent(
                cpuRaw,
                _lastSnapshot != null ? _lastSnapshot.CpuDutyPercent : 0,
                ref _cpuZeroDutyReadCount,
                "CPU");
            int gpuDuty = NormalizeDutyPercent(
                gpuRaw,
                _lastSnapshot != null ? _lastSnapshot.GpuDutyPercent : 0,
                ref _gpuZeroDutyReadCount,
                "GPU");

            // 从NVIDIA遥测获取真实GPU温度
            _lastGpuTelemetry = _gpuTelemetry?.Latest;
            int gpuTemp = -1;
            bool gpuTelemetryOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;

            if (gpuTelemetryOk)
            {
                gpuTemp = _lastGpuTelemetry.TemperatureC;
                _gpuTelemetryValidSamples++;
                if (_gpuTelemetryValidSamples >= 3)
                    _gpuTelemetryReady = true;
            }
            else
            {
                _gpuTelemetryValidSamples = 0;
                _gpuTelemetryReady = false;
            }

            return new FanSnapshot
            {
                // CPU温度继续使用EC通道1 Remote
                CpuTemperatureC = cpuRaw.Remote,
                // GPU温度使用NVIDIA遥测（不再使用EC通道2的固定72°C）
                GpuTemperatureC = gpuTemp,
                CpuTemperatureLocalC = cpuRaw.Local,
                // GPU EC诊断数据放入LocalC字段
                GpuTemperatureLocalC = gpuRaw.Remote,
                CpuDutyPercent = cpuDuty,
                GpuDutyPercent = gpuDuty,
                CpuUtilizationPercent = ReadPerformanceCounterPercent(_cpuUtilizationCounter),
                CpuPerformancePercent = ReadPerformanceCounterPercent(_cpuPerformanceCounter),
                // RPM reads are diagnostic-only and can stall the EC DLL.
                // Active control uses the last startup/probe sample instead
                // of putting RPM transactions in every control cycle.
                CpuRpm = _lastCpuRpm,
                GpuRpm = _lastGpuRpm,
                GpuTelemetryAvailable = gpuTelemetryOk,
                GpuTelemetryUtilization = gpuTelemetryOk ? _lastGpuTelemetry.UtilizationPercent : 0,
                GpuTelemetryPowerWatts = gpuTelemetryOk ? _lastGpuTelemetry.PowerWatts : 0,
                GpuTelemetryPState = gpuTelemetryOk ? (_lastGpuTelemetry.PState ?? "") : "",
                GpuTelemetrySource = gpuTelemetryOk ? (_lastGpuTelemetry.SourceName ?? "") : "",
                TimestampUtc = timestampUtc
            };
        }

        private static double ReadPerformanceCounterPercent(PerformanceCounter counter)
        {
            if (counter == null)
                return 0;
            try
            {
                float value = counter.NextValue();
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return 0;
                return Math.Max(0, Math.Min(100, value));
            }
            catch
            {
                return 0;
            }
        }

        private static int ToDutyPercent(byte rawDuty)
        {
            return (int)Math.Round(rawDuty * 100.0 / 255.0);
        }

        private int NormalizeDutyPercent(EcData raw, int previousDutyPercent, ref int zeroReadCount, string channelName)
        {
            if (raw.FanDuty != 0)
            {
                zeroReadCount = 0;
                return ToDutyPercent(raw.FanDuty);
            }

            // A single zero duty byte has been observed from the native EC read
            // while the fan was demonstrably running. Do not turn that transient
            // into a false external override or a full-speed write. If it persists
            // for a second consecutive valid-temperature snapshot, expose zero to
            // the existing controller/safety logic instead of masking a real fault.
            if (raw.Remote >= 50 && previousDutyPercent > 0)
            {
                zeroReadCount++;
                if (zeroReadCount == 1)
                {
                    AppendLog("EC回读异常：" + channelName + " duty=0，沿用上一帧有效值 " + previousDutyPercent + "%；等待下一次确认");
                    return previousDutyPercent;
                }
            }
            else
            {
                zeroReadCount = 0;
            }

            return ToDutyPercent(raw.FanDuty);
        }

        private async Task WriteDecisionAsync(ControlDecision decision, FanSnapshot snapshot, DateTime now, CancellationToken token)
        {
            if (decision.Cpu.ShouldWrite)
            {
                await ExecuteEcAsync("SetFanPercent CPU", ec =>
                {
                    ec.SetFanPercent(1, decision.Cpu.WritePercent);
                    return true;
                }, token);
                lock (_engineLock) { _engine.MarkCpuWritten(decision.Cpu.WritePercent, now); }
                StartWriteVerification(1, decision.Cpu.WritePercent, snapshot.CpuDutyPercent, snapshot.CpuRpm);
                if (_config.DetailedVerificationLogging)
                    AppendLog("EC写入完成：CPU=" + decision.Cpu.WritePercent + "%，快照前值=" + snapshot.CpuDutyPercent + "%；由下一次快照确认");
            }

            if (decision.Gpu.ShouldWrite)
            {
                await ExecuteEcAsync("SetFanPercent GPU", ec =>
                {
                    ec.SetFanPercent(2, decision.Gpu.WritePercent);
                    return true;
                }, token);
                lock (_engineLock) { _engine.MarkGpuWritten(decision.Gpu.WritePercent, now); }
                StartWriteVerification(2, decision.Gpu.WritePercent, snapshot.GpuDutyPercent, snapshot.GpuRpm);
                if (_config.DetailedVerificationLogging)
                    AppendLog("EC写入完成：GPU=" + decision.Gpu.WritePercent + "%，快照前值=" + snapshot.GpuDutyPercent + "%；由下一次快照确认");
            }
        }

        private void UpdateDashboard(FanSnapshot snapshot, ControlDecision decision)
        {
            UpdateDashboardValues(snapshot, decision);

            // CPU控制状态
            string cpuStateStr = "—";
            if (decision != null && decision.Cpu != null)
            {
                cpuStateStr = GetControlStateText(decision.Cpu.State, decision.Cpu.DownHoldRemainingSeconds);
            }

            // 更新CPU组框标题显示状态
            if (_cpuCardBox != null)
            {
                _cpuCardBox.Text = "CPU  [" + cpuStateStr + "]";
                if (decision?.Cpu?.State == ControlState.Emergency)
                    _cpuCardBox.ForeColor = Color.Red;
                else if (decision?.Cpu?.State == ControlState.ExternalOverride)
                    _cpuCardBox.ForeColor = Color.OrangeRed;
                else if (decision?.Cpu?.State == ControlState.InvalidSensor)
                    _cpuCardBox.ForeColor = Color.Gray;
                else
                    _cpuCardBox.ForeColor = Color.Black;
            }

            // GPU温度使用NVIDIA遥测数据
            string gpuStateStr = "—";
            if (_gpuTelemetryReady && _lastGpuTelemetry != null && !_lastGpuTelemetry.IsStale)
            {
                _gpuNvidiaUtilLabel.Text = _lastGpuTelemetry.UtilizationPercent + "%";
                _gpuNvidiaPowerLabel.Text = _lastGpuTelemetry.PowerWatts.ToString("F1") + " W";
                _gpuNvidiaPStateLabel.Text = _lastGpuTelemetry.PState ?? "N/A";
                _gpuNvidiaSourceLabel.Text = _lastGpuTelemetry.SourceName ?? "nvidia-smi";
                _gpuNvidiaStatusLabel.Text = "正常";
                _gpuNvidiaStatusLabel.ForeColor = Color.DarkGreen;

                if (decision != null && decision.Gpu != null)
                {
                    gpuStateStr = GetControlStateText(decision.Gpu.State, decision.Gpu.DownHoldRemainingSeconds);
                }
            }
            else
            {
                string status = "未就绪";
                Color statusColor = Color.Gray;
                if (_lastGpuTelemetry != null)
                {
                    if (_lastGpuTelemetry.IsStale)
                    {
                        status = "数据过期";
                        statusColor = Color.OrangeRed;
                        gpuStateStr = "遥测过期";
                    }
                    else if (!string.IsNullOrEmpty(_lastGpuTelemetry.ErrorMessage))
                    {
                        status = _lastGpuTelemetry.ErrorMessage.Contains("not found") ? "不可用" : "错误";
                        statusColor = Color.OrangeRed;
                        gpuStateStr = "遥测错误";
                    }
                    else
                    {
                        status = "等待数据…";
                        statusColor = Color.Gray;
                        gpuStateStr = "等待遥测";
                    }
                }
                _gpuNvidiaUtilLabel.Text = "—";
                _gpuNvidiaPowerLabel.Text = "—";
                _gpuNvidiaPStateLabel.Text = "—";
                _gpuNvidiaSourceLabel.Text = _lastGpuTelemetry?.SourceName ?? "—";
                _gpuNvidiaStatusLabel.Text = status;
                _gpuNvidiaStatusLabel.ForeColor = statusColor;
            }

            // 更新GPU组框标题显示状态
            if (_gpuCardBox != null)
            {
                _gpuCardBox.Text = "GPU  [" + gpuStateStr + "]";
                if (_gpuTelemetryReady && _lastGpuTelemetry != null && !_lastGpuTelemetry.IsStale)
                {
                    if (decision?.Gpu?.State == ControlState.Emergency)
                        _gpuCardBox.ForeColor = Color.Red;
                    else if (decision?.Gpu?.State == ControlState.ExternalOverride)
                        _gpuCardBox.ForeColor = Color.OrangeRed;
                    else if (decision?.Gpu?.State == ControlState.InvalidSensor)
                        _gpuCardBox.ForeColor = Color.Gray;
                    else
                        _gpuCardBox.ForeColor = Color.Black;
                }
                else
                {
                    _gpuCardBox.ForeColor = Color.Gray;
                }
            }

            // 硬件状态栏显示EC诊断信息及温升速率
            SetLabelText(_hardwareStatusLabel, "EC CPU ch1: R=");
            // Keep the fixed labels untouched.  Only the small value labels
            // change, so the whole green status row no longer flashes on every
            // dashboard tick.
            SetLabelText(_cpuEcRemoteStatusValueLabel,
                snapshot.CpuTemperatureC > 0 ? snapshot.CpuTemperatureC.ToString() : "—");
            SetLabelText(_cpuEcLocalStatusValueLabel, snapshot.CpuTemperatureLocalC.ToString());
            SetLabelText(_gpuEcRemoteStatusValueLabel, snapshot.GpuTemperatureLocalC.ToString());
            SetLabelText(_gpuEcLocalStatusValueLabel,
                _lastSnapshot != null ? _lastSnapshot.GpuTemperatureLocalC.ToString() : "—");
            SetLabelText(_cpuRpmStatusValueLabel, snapshot.CpuRpm > 0 ? snapshot.CpuRpm.ToString() : "—");
            SetLabelText(_gpuRpmStatusValueLabel, snapshot.GpuRpm > 0 ? snapshot.GpuRpm.ToString() : "—");
            SetLabelText(_temperatureRiseStatusValueLabel,
                decision != null && decision.Cpu != null
                    ? decision.Cpu.TemperatureRiseRateCPerSec.ToString("F2")
                    : "—");

            if (_gpuTelemetryReady && _lastGpuTelemetry != null && !_lastGpuTelemetry.IsStale)
                SetHardwareStatusColor(Color.DarkGreen);
            else
                SetHardwareStatusColor(Color.OrangeRed);

            if (decision == null)
            {
                return;
            }

            // 图表点已由UI Timer独立添加，此处不再调用AddHistoryPoint
        }

        private void UpdateDashboardValues(FanSnapshot snapshot, ControlDecision decision)
        {
            UpdateStrategyStatus(snapshot);
            bool gpuTemperatureAvailable = _gpuTelemetryReady &&
                                           _lastGpuTelemetry != null &&
                                           !_lastGpuTelemetry.IsStale;
            bool decisionAvailable = decision?.Cpu != null && decision?.Gpu != null;

            SetLabelText(_cpuTempLabel,
                snapshot.CpuTemperatureC > 0 ? snapshot.CpuTemperatureC.ToString() : "—");
            SetLabelText(_cpuFilteredLabel,
                decisionAvailable ? decision.Cpu.ControlTemperatureC.ToString("0.0") : "—");
            SetLabelText(_cpuDutyLabel, snapshot.CpuDutyPercent.ToString());
            SetLabelText(_cpuTargetLabel,
                decisionAvailable ? decision.Cpu.AppliedPercent.ToString("0.0") : "—");
            SetLabelText(_cpuRpmLabel,
                snapshot.CpuRpm > 0 ? snapshot.CpuRpm.ToString() : "—");

            SetLabelText(_gpuTempLabel,
                gpuTemperatureAvailable ? snapshot.GpuTemperatureC.ToString() : "—");
            SetLabelText(_gpuFilteredLabel,
                decisionAvailable ? decision.Gpu.ControlTemperatureC.ToString("0.0") : "—");
            SetLabelText(_gpuDutyLabel, snapshot.GpuDutyPercent.ToString());
            SetLabelText(_gpuTargetLabel,
                decisionAvailable ? decision.Gpu.AppliedPercent.ToString("0.0") : "—");
            SetLabelText(_gpuRpmLabel,
                snapshot.GpuRpm > 0 ? snapshot.GpuRpm.ToString() : "—");

            Color gpuTemperatureColor = gpuTemperatureAvailable
                ? _gpuRpmLabel.ForeColor
                : Color.Gray;
            if (_gpuTempLabel.ForeColor != gpuTemperatureColor)
                _gpuTempLabel.ForeColor = gpuTemperatureColor;
        }

        private void UpdateStrategyStatus(FanSnapshot snapshot)
        {
            StrategyMode mode = _config == null ? StrategyMode.Auto : _config.StrategyMode;
            AdaptivePowerTier tier = _adaptiveCurrentTier;
            AdaptivePowerPreset preset = AdaptivePowerPreset.For(tier);
            string power = _adaptiveXtuConfirmed || _adaptiveBackendName.StartsWith("Windows", StringComparison.Ordinal)
                ? "PL1 " + preset.Pl1Watts.ToString("0.#") + "W / PL2 " + preset.Pl2Watts.ToString("0.#") + "W / " + preset.TimeSeconds + "秒"
                : "等待应用（目标 PL1 " + preset.Pl1Watts.ToString("0.#") + "W / PL2 " + preset.Pl2Watts.ToString("0.#") + "W）";
            string selected = StrategyModeInfo.GetName(mode);
            string current = GetCurrentStrategyLevelName(mode, tier);
            string backend = string.IsNullOrEmpty(_adaptiveBackendName) ? "未应用" : _adaptiveBackendName;
            SetLabelText(_strategyModeValueLabel, selected);
            SetLabelText(_strategyTierValueLabel, current);
            SetLabelText(_strategyPowerValueLabel, power);
            SetLabelText(_strategyReasonValueLabel, _adaptiveLastReason ?? "等待硬件");
            SetLabelText(_strategyBackendValueLabel, backend);
            SetLabelText(_strategyCpuValueLabel, snapshot == null ? "—" : snapshot.CpuUtilizationPercent.ToString("0") + "%");
            SetLabelText(_strategyGpuValueLabel, snapshot == null ? "—" : snapshot.GpuTelemetryUtilization.ToString("0") + "%");
            UpdateTrayStrategyStatus();
        }

        private void UpdateTrayStrategyStatus()
        {
            if (_trayStrategyItem == null || _trayTierItem == null || _config == null)
                return;

            Action update = delegate
            {
                if (_trayStrategyItem != null)
                    _trayStrategyItem.Text = "当前策略：" + StrategyModeInfo.GetName(_config.StrategyMode);
                if (_trayTierItem != null)
                    _trayTierItem.Text = "当前档位：" + GetCurrentStrategyLevelName(_config.StrategyMode, _adaptiveCurrentTier);

                foreach (KeyValuePair<StrategyMode, ToolStripMenuItem> entry in _trayStrategyItems)
                    entry.Value.Checked = false;

                ToolStripMenuItem selectedItem;
                if (_config.StrategyMode == StrategyMode.Auto)
                {
                    if (_trayStrategyItems.TryGetValue(StrategyMode.Auto, out selectedItem))
                        selectedItem.Checked = true;

                    StrategyMode activeTierMode = StrategyMode.Daily;
                    if (_adaptiveCurrentTier == AdaptivePowerTier.Quiet)
                        activeTierMode = StrategyMode.Quiet;
                    else if (_adaptiveCurrentTier == AdaptivePowerTier.Code)
                        activeTierMode = StrategyMode.Code;
                    else if (_adaptiveCurrentTier == AdaptivePowerTier.Heavy)
                        activeTierMode = StrategyMode.Heavy;

                    if (_trayStrategyItems.TryGetValue(activeTierMode, out selectedItem))
                        selectedItem.Checked = true;
                }
                else if (_trayStrategyItems.TryGetValue(_config.StrategyMode, out selectedItem))
                {
                    selectedItem.Checked = true;
                }
            };
            try
            {
                if (IsDisposed || Disposing)
                    return;
                if (InvokeRequired)
                    BeginInvoke(update);
                else
                    update();
            }
            catch { }
        }

        private static void SetLabelText(Label label, string text)
        {
            if (label != null && label.Text != text)
            {
                label.Text = text;
            }
        }

        private void SetHardwareStatusColor(Color color)
        {
            if (_hardwareStatusFlow == null)
                return;

            foreach (Control control in _hardwareStatusFlow.Controls)
            {
                if (control.ForeColor != color)
                    control.ForeColor = color;
            }
        }

        private static string GetControlStateText(ControlState state, double downHoldRemaining)
        {
            switch (state)
            {
                case ControlState.Normal: return "正常跟随";
                case ControlState.RampingUp: return "缓慢升速";
                case ControlState.RampingDown: return "缓慢降速";
                case ControlState.DownHold: return "降速等待 " + downHoldRemaining.ToString("F0") + "s";
                case ControlState.StableZone: return "稳定平台";
                case ControlState.Emergency: return "⚡紧急提速";
                case ControlState.InvalidSensor: return "传感器无效";
                case ControlState.WriteFailed: return "EC写入异常";
                case ControlState.ExternalOverride: return "外部覆盖";
                case ControlState.RestoredAuto: return "自动控制";
                default: return state.ToString();
            }
        }

        private async System.Threading.Tasks.Task RunEcProbeAsync()
        {
            AppendLog("===== EC通道诊断探测开始 =====");
            if (!IsEcReady())
            {
                AppendLog("EC未初始化，无法探测。");
                return;
            }

            try
            {
                int fanCount = await EcGetCountAsync();
                AppendLog("EC报告的通道数：" + fanCount);

                for (int ch = 0; ch <= 3; ch++)
                {
                    EcData raw = await EcReadRawAsync(ch);
                    AppendLog(string.Format(
                        "  通道{0}: Remote={1}°C, Local={2}°C, FanDuty={3}({4:F1}%), Reserve={5}",
                        ch, raw.Remote, raw.Local, raw.FanDuty,
                        raw.FanDuty * 100.0 / 255.0, raw.Reserve));
                }

                int cpuRpm = await EcGetCpuRpmAsync();
                int gpuRpm = await EcGetGpuRpmAsync();
                _lastCpuRpm = cpuRpm;
                _lastGpuRpm = gpuRpm;
                AppendLog(string.Format("CPU转速: {0}, GPU转速: {1}",
                    cpuRpm > 0 ? cpuRpm.ToString() : "—",
                    gpuRpm > 0 ? gpuRpm.ToString() : "—"));

                EcData ch1 = await EcReadRawAsync(1);
                EcData ch2 = await EcReadRawAsync(2);

                if (ch2.Remote == ch2.Local && ch2.Remote > 0)
                {
                    AppendLog("注意：通道2的Remote和Local值相同，可能数据不可靠。");
                }

                AppendLog("诊断：若GPU温度不变化，请检查通道2的Local值是否更符合实际GPU温度。");
                AppendLog("===== EC通道诊断探测结束 =====");
            }
            catch (Exception ex)
            {
                AppendLog("EC探测异常：" + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<EcData> EcReadRawAsync(int ch)
        {
            return await ExecuteEcAsync("ReadRaw ch" + ch, ec => ec.ReadRaw(ch), CancellationToken.None);
        }

        private async System.Threading.Tasks.Task<int> EcGetCpuRpmAsync()
        {
            return await ExecuteEcAsync("GetCpuRpm", ec => ec.GetCpuRpm(), CancellationToken.None);
        }

        private async System.Threading.Tasks.Task<int> EcGetGpuRpmAsync()
        {
            return await ExecuteEcAsync("GetGpuRpm", ec => ec.GetGpuRpm(), CancellationToken.None);
        }

        private void AddHistoryPoint(string seriesName, double value)
        {
            var chart = seriesName.StartsWith("CPU ", StringComparison.Ordinal)
                ? _cpuHistoryChart
                : _gpuHistoryChart;
            if (chart == null || chart.Series.IndexOf(seriesName) < 0)
            {
                return;
            }

            var series = chart.Series[seriesName];
            series.Points.AddY(value);
            while (series.Points.Count > 150)
            {
                series.Points.RemoveAt(0);
            }
        }

        private void UpdateModeStatus()
        {
            Color backColor;
            string text;
            switch (_runMode)
            {
                case RunMode.ReadOnly:
                    text = "只读";
                    backColor = Color.FromArgb(0, 100, 0);
                    break;
                case RunMode.Simulation:
                    text = "模拟";
                    backColor = Color.FromArgb(0, 70, 140);
                    break;
                case RunMode.Active:
                    text = "活动";
                    backColor = Color.FromArgb(180, 50, 0);
                    break;
                default:
                    text = "未知";
                    backColor = Color.Gray;
                    break;
            }
            _modeStatusLabel.Text = text;
            _modeStatusPanel.BackColor = backColor;

            // 更新托盘菜单中的模式状态
            if (_trayModeItem != null)
                _trayModeItem.Text = "当前模式：" + text;
        }

        private async void FlashModeBadge()
        {
            if (_modeStatusPanel == null || _closing) return;
            Color original = _modeStatusPanel.BackColor;
            _modeStatusPanel.BackColor = Color.FromArgb(255, 200, 50);
            await System.Threading.Tasks.Task.Delay(400);
            if (!_closing && _modeStatusPanel != null)
                UpdateModeStatus();
        }

        private void RestoreAuto(string reason)
        {
            if (!IsEcReady())
            {
                AppendLog("无法同步恢复自动：EC已不可用。原因：" + reason + "；由独立看门狗负责保护。");
                InvalidateVerificationTasks();
                return;
            }

            try
            {
                EcRestoreAllAuto();
                InvalidateVerificationTasks();
                AppendLog("风扇已恢复自动。原因：" + reason + "。");
            }
            catch (Exception exception)
            {
                AppendLog("恢复自动失败：" + exception.Message);
            }
        }

        private void StartHeartbeatMonitor()
        {
            if (_heartbeatMonitorTimer != null || _config == null || !_config.LaunchWatchdogInActiveMode)
            {
                return;
            }

            _heartbeatMonitorTimer = new System.Threading.Timer(
                HeartbeatMonitorTick,
                null,
                1000,
                1000);
        }

        private void HeartbeatMonitorTick(object state)
        {
            if (_closing || _runMode != RunMode.Active || _heartbeat == null)
            {
                return;
            }

            if (HasWatchdogExitedUnexpectedly())
            {
                if (Interlocked.Exchange(ref _watchdogFailureHandling, 1) == 0)
                {
                    try { BeginInvoke(new Action(HandleWatchdogFailure)); }
                    catch { }
                }
                return;
            }

            long progressTicks = Interlocked.Read(ref _lastControlProgressUtcTicks);
            long ecActivityTicks = Interlocked.Read(ref _lastEcActivityUtcTicks);
            if (ecActivityTicks > progressTicks)
            {
                progressTicks = ecActivityTicks;
            }
            if (progressTicks <= 0)
            {
                return;
            }

            double progressAgeSeconds = (DateTime.UtcNow.Ticks - progressTicks) /
                                        (double)TimeSpan.TicksPerSecond;
            // A slow cycle can contain several successful EC calls. Treat
            // each completed call as progress, but stop publishing if a
            // single EC operation or control-loop wait really stalls.
            if (progressAgeSeconds > 5)
            {
                return;
            }

            try
            {
                _heartbeat.WriteActive(Process.GetCurrentProcess().Id);
            }
            catch (Exception exception)
            {
                AppendLog("心跳发布失败：" + exception.Message);
            }
        }

        private void StopHeartbeatMonitor()
        {
            System.Threading.Timer timer = _heartbeatMonitorTimer;
            _heartbeatMonitorTimer = null;
            if (timer != null)
            {
                try { timer.Dispose(); } catch { }
            }
        }

        private void HandleWatchdogFailure()
        {
            if (_closing || _runMode != RunMode.Active)
            {
                return;
            }

            // The independent watchdog has already restored EC Auto. Do not
            // wait for the EC worker here: the control loop may be inside a
            // stalled native call. Make the UI and future writes safe now.
            _runMode = RunMode.ReadOnly;
            StopHeartbeatMonitor();
            InvalidateVerificationTasks();
            _controlCts?.Cancel();
            try { _heartbeat?.WriteStop(); } catch { }
            try { StopWatchdog(); } catch { }
            if (_modeCombo != null) _modeCombo.SelectedItem = _runMode;
            UpdateModeStatus();
            FlashModeBadge();
            AppendLog("风扇已由独立看门狗恢复自动。原因：看门狗异常退出。");
            AppendLog("运行模式切换为 ReadOnly。原因：看门狗故障保护。");
        }

        private void StartWatchdog()
        {
            if (!_config.LaunchWatchdogInActiveMode)
            {
                return;
            }

            if (_watchdogProcess != null)
            {
                try
                {
                    if (!_watchdogProcess.HasExited)
                    {
                        return;
                    }
                }
                catch
                {
                }

                _watchdogProcess.Dispose();
                _watchdogProcess = null;
            }

            string watchdogExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15FanWatchdog.exe");
            if (!File.Exists(watchdogExe))
            {
                AppendLog("未找到看门狗可执行文件；活动模式仅使用进程内安全机制。");
                return;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = watchdogExe,
                Arguments = "--parent " + Process.GetCurrentProcess().Id + " --heartbeat \"" + _heartbeatPath + "\" --dll \"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll") + "\" --log \"" + _watchdogLogPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            _watchdogProcess = Process.Start(startInfo);
            AppendLog("看门狗已启动，PID " + (_watchdogProcess == null ? 0 : _watchdogProcess.Id) + "。");
        }

        private bool HasWatchdogExitedUnexpectedly()
        {
            if (!_config.LaunchWatchdogInActiveMode || _watchdogProcess == null)
            {
                return false;
            }

            try
            {
                return _watchdogProcess.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private void StopWatchdog()
        {
            try
            {
                _heartbeat?.WriteStop();
                if (_watchdogProcess != null && !_watchdogProcess.HasExited)
                {
                    if (!_watchdogProcess.WaitForExit(2500))
                    {
                        _watchdogProcess.Kill();
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (_watchdogProcess != null)
                {
                    _watchdogProcess.Dispose();
                    _watchdogProcess = null;
                }
            }
        }

        private void StartControlCenterLeaseWatchdog()
        {
            if (_controlCenterLeaseWatchdogProcess != null)
            {
                try { if (!_controlCenterLeaseWatchdogProcess.HasExited) return; } catch { }
                try { _controlCenterLeaseWatchdogProcess.Dispose(); } catch { }
                _controlCenterLeaseWatchdogProcess = null;
            }

            string watchdogExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15FanWatchdog.exe");
            string leasePath = Path.Combine(_dataDirectory, "controlcenter.lease.json");
            if (!File.Exists(watchdogExe) || !File.Exists(leasePath))
            {
                AppendLog("Control Center 接管看门狗未部署，未启用服务接管。");
                return;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = watchdogExe,
                Arguments = "--lease-only --parent " + Process.GetCurrentProcess().Id +
                             " --lease \"" + leasePath + "\" --log \"" + _watchdogLogPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            _controlCenterLeaseWatchdogProcess = Process.Start(startInfo);
            AppendLog("Control Center 接管看门狗已启动，PID " +
                      (_controlCenterLeaseWatchdogProcess == null ? 0 : _controlCenterLeaseWatchdogProcess.Id) + "。");
        }

        private void StopControlCenterLeaseWatchdog()
        {
            try
            {
                if (_controlCenterLeaseWatchdogProcess != null && !_controlCenterLeaseWatchdogProcess.HasExited)
                {
                    // Lease-only watchdog has no stop signal; terminate the
                    // helper immediately after a successful lease release.
                    _controlCenterLeaseWatchdogProcess.Kill();
                }
            }
            catch { }
            finally
            {
                try { _controlCenterLeaseWatchdogProcess?.Dispose(); } catch { }
                _controlCenterLeaseWatchdogProcess = null;
            }
        }

        private bool SaveConfig()
        {
            try
            {
                _configStore?.Save(_config);
                return true;
            }
            catch (Exception exception)
            {
                AppendLog("保存配置失败：" + exception.Message);
                return false;
            }
        }

        // 异步日志后台写入循环
        private const long MaxLogFileBytes = 2 * 1024 * 1024; // 2MB 上限

        private async Task LogFlushLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, token);
                    FlushLogQueue();
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
            // 退出前最后一次Flush
            FlushLogQueue();
        }

        private void FlushLogQueue()
        {
            if (_logQueue.IsEmpty) return;

            var sb = new StringBuilder();
            string line;
            int count = 0;
            // 注意：每条日志已自带 Environment.NewLine，不再追加
            while (_logQueue.TryDequeue(out line) && count < 100)
            {
                sb.Append(line);
                count++;
            }

            if (sb.Length > 0)
            {
                try
                {
                    string logPath = Path.Combine(_dataDirectory, "application.log");

                    // 限制 application.log 体积：超过上限时轮转一次，
                    // 保留最近两个文件（当前 + .1 备份），长期运行不会无限增长。
                    FileInfo logInfo = new FileInfo(logPath);
                    if (logInfo.Exists && logInfo.Length > MaxLogFileBytes)
                    {
                        try
                        {
                            string rotated = logPath + ".1";
                            if (File.Exists(rotated)) File.Delete(rotated);
                            File.Move(logPath, rotated);
                        }
                        catch { }
                    }

                    File.AppendAllText(logPath, sb.ToString());
                }
                catch { }
            }
        }

        private void AppendLog(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;

            // 加入异步日志队列（后台线程批量写入文件）
            _logQueue.Enqueue(DateTime.Now.ToString("O") + "  " + message + Environment.NewLine);
            // UI 显示行入独立队列，由 FlushUiLogQueue 批量追加
            _uiLogQueue.Enqueue(line + Environment.NewLine);

            // UI线程更新文本框（仅窗口可见时）。节流到最多每250ms一次：
            // 委托内从队列批量取出全部待显示行，一次AppendText。
            if (Visible && ShowInTaskbar && _logTextBox != null && !_logTextBox.IsDisposed)
            {
                long now = Environment.TickCount;
                if (now - _lastUiLogFlushTick >= 250)
                {
                    _lastUiLogFlushTick = now;
                    try
                    {
                        _logTextBox.BeginInvoke(new Action(FlushUiLogQueue));
                    }
                    catch { }
                }
            }
        }

        // UI线程：批量追加排队日志，避免每条日志一个BeginInvoke
        private void FlushUiLogQueue()
        {
            if (_logTextBox == null || _logTextBox.IsDisposed || _config == null)
                return;

            var sb = new StringBuilder();
            string pending;
            int count = 0;
            while (_uiLogQueue.TryDequeue(out pending) && count < 200)
            {
                sb.Append(pending);
                count++;
            }
            if (count == 0)
                return;

            try
            {
                _logTextBox.AppendText(sb.ToString());
                _currentLogLines += count;
                // 超过限制时批量删除旧行
                if (_currentLogLines > _config.MaxUiLogLines)
                {
                    int remove = _config.MaxUiLogLines / 2;
                    var text = _logTextBox.Text;
                    int idx = 0;
                    int removed = 0;
                    for (int i = 0; i < remove; i++)
                    {
                        int next = text.IndexOf(Environment.NewLine, idx);
                        if (next < 0) break;
                        idx = next + Environment.NewLine.Length;
                        removed++;
                    }
                    if (idx > 0)
                        _logTextBox.Text = text.Substring(idx);
                    _currentLogLines -= removed;
                }
            }
            catch { }
        }

        private void MainFormResize(object sender, EventArgs e)
        {
            // 最小化按钮：窗口进入任务栏，不隐藏到托盘
            if (WindowState == FormWindowState.Minimized)
            {
                if (_calibrationActive)
                {
                    if (!StopCalibration("窗口最小化"))
                    {
                        WindowState = FormWindowState.Normal;
                        MessageBox.Show(
                            "恢复自动失败，窗口不会最小化。请使用“恢复自动”并检查 EC 日志。",
                            "声学校准",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }

                    NotifyCalibrationWindowActionStopped();
                }
                // ShowInTaskbar保持true，用户可点击任务栏恢复
            }
        }

        private void SystemEventsPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                try
                {
                    Invoke(new Action(() => SystemEventsPowerModeChanged(sender, e)));
                }
                catch (Exception ex)
                {
                    AppendLog("处理电源状态变化失败：" + ex.Message);
                }
                return;
            }

            if (e.Mode == PowerModes.Suspend)
            {
                if (_calibrationActive)
                {
                    if (!StopCalibration("系统休眠"))
                    {
                        try { EcRestoreAllAuto(); }
                        catch (Exception ex) { AppendLog("休眠前恢复自动失败：" + ex.Message); }
                    }
                }
                else
                {
                    RestoreAuto("系统休眠");
                    StopWatchdog();
                }
            }
            else if (e.Mode == PowerModes.Resume)
            {
                BeginInvoke(new Action(async delegate
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                DisposeEc();
                                Interlocked.Exchange(ref _ecFaulted, 0);
                                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
                                _ecQueue = new EcAccessQueue(dllPath);
                                if (!_ecQueue.Ready.Wait(10000) || !_ecQueue.IsReady)
                                    throw new TimeoutException("EC worker initialization timed out after resume.");
                            }
                            catch (Exception ex) { AppendLog("恢复后EC初始化失败：" + ex.Message); }
                        });

                        lock (_engineLock) { _engine?.Reset(); }
                        SetRunMode(RunMode.ReadOnly, "恢复后安全重置");
                    }
                    catch (Exception ex)
                    {
                        AppendLog("恢复初始化异常：" + ex.Message);
                    }
                }));
            }
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowFinalClose)
            {
                return;
            }

            // 关闭 × → 隐藏到托盘，不退出
            if (e.CloseReason == CloseReason.UserClosing && !_explicitExitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // 真正退出：异步执行清理，不在UI线程Wait
            e.Cancel = true;
            _ = ExitAsync();
        }

        private async System.Threading.Tasks.Task ExitAsync()
        {
            // 防止重复执行
            if (_closing) return;
            _closing = true;
            _mainTimer.Stop();
            _runMode = RunMode.ReadOnly;
            StopHeartbeatMonitor();

            AppendLog("Control loop cancellation requested");
            _controlCts?.Cancel();

            // 停止校准（防止校准中隐藏导致固定占空比）
            try { StopCalibration("退出"); } catch { }

            // 等待后台控制循环实际结束（最多3秒）
            bool controlStopped = false;
            try
            {
                if (_controlTask != null)
                    controlStopped = await System.Threading.Tasks.Task.WhenAny(_controlTask,
                        System.Threading.Tasks.Task.Delay(3000)) == _controlTask;
            }
            catch { }

            if (controlStopped)
                AppendLog("Control loop stopped");
            else
                AppendLog("Control loop stop timeout — proceeding with cleanup");

            try { await RestoreAdaptivePowerPolicyAsync(); } catch (Exception exception) { AppendLog("恢复自适应功耗方案失败：" + exception.Message); }

            // 使验证任务失效
            InvalidateVerificationTasks();

            // 恢复Auto（此时确认无后台写入）
            AppendLog("RestoreAuto begin");
            try { RestoreAuto("应用程序关闭"); } catch { }
            AppendLog("RestoreAuto end");

            // 停止风扇看门狗；Control Center 租约恢复不能在 UI 线程同步等待
            // 服务启动。服务可能需要数秒，超时时保留租约看门狗接管恢复。
            try { StopWatchdog(); } catch { }
            await ReleaseControlCenterLeaseForExitAsync();
            try { _heartbeat?.WriteStop(); } catch { }

            // 停止GPU遥测
            if (_gpuTelemetry != null)
            {
                _gpuTelemetry.Dispose();
                _gpuTelemetry = null;
            }
            _csvLogger?.Dispose();
            _csvLogger = null;

            // Flush日志
            _logCts?.Cancel();
            try { if (_logFlushTask != null) await System.Threading.Tasks.Task.WhenAny(_logFlushTask, System.Threading.Tasks.Task.Delay(2000)); } catch { }
            FlushLogQueue();

            // 释放EC
            AppendLog("EC disposed");
            DisposeEc();

            // 释放NotifyIcon
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            SystemEvents.PowerModeChanged -= SystemEventsPowerModeChanged;

            // 清理完成后再次Close；FormClosing通过_allowFinalClose明确放行。
            _explicitExitRequested = true;
            Action finalClose = () =>
            {
                _allowFinalClose = true;
                Close();
            };
            if (InvokeRequired)
                BeginInvoke(finalClose);
            else
                finalClose();
        }

        private async Task ReleaseControlCenterLeaseForExitAsync()
        {
            ControlCenterLease lease = _controlCenterLease;
            if (lease == null)
            {
                return;
            }

            Task<Tuple<bool, string>> releaseTask = Task.Run(() =>
            {
                string diagnostic;
                bool restored = lease.Release(out diagnostic);
                return Tuple.Create(restored, diagnostic);
            });

            try
            {
                Task completed = await Task.WhenAny(releaseTask, Task.Delay(1500)).ConfigureAwait(true);
                if (completed == releaseTask)
                {
                    Tuple<bool, string> result = await releaseTask.ConfigureAwait(true);
                    AppendLog((result.Item1 ? "Control Center 已恢复：" : "Control Center 恢复失败：") + result.Item2);
                    if (result.Item1)
                    {
                        try { StopControlCenterLeaseWatchdog(); } catch { }
                    }
                }
                else
                {
                    AppendLog("Control Center 恢复服务仍在启动，退出不再等待；交由接管看门狗继续恢复。");
                }
            }
            catch (Exception exception)
            {
                AppendLog("Control Center 恢复任务异常，交由接管看门狗处理：" + exception.Message);
            }
        }

        private void DisposeEc()
        {
            EcAccessQueue queue = _ecQueue;
            _ecQueue = null;
            if (queue != null)
            {
                try
                {
                    queue.Dispose();
                }
                catch
                {
                }
            }
        }

    }
}
