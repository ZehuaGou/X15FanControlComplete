using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
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
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
                _ecLock.Wait();
                try
                {
                    _ec = new ClevoEcInfo(dllPath);
                }
                finally { _ecLock.Release(); }
                _hardwareStatusLabel.Text = "硬件：EC 已初始化；风扇通道数：" + EcGetCount() + "。CPU=1，GPU=2。";
                _hardwareStatusLabel.ForeColor = Color.DarkGreen;
                AppendLog("EC 初始化成功。");
            }
            catch (Exception exception)
            {
                _ec = null;
                _hardwareStatusLabel.Text = "硬件不可用：" + exception.Message;
                _hardwareStatusLabel.ForeColor = Color.DarkRed;
                AppendLog("EC 初始化失败：" + exception);
            }
        }

        // EC串行化访问。注意：这些包装器必须直接调用 _ec，绝不能再次调用自身。
        // 旧代码中的 EcReadRaw/EcGetDuty/EcGet*RpmLocked 都发生了自递归：
        // 第一次拿到 SemaphoreSlim 后再次 Wait，导致 UI 定时器永久死锁。
        private int EcGetCount()
        {
            _ecLock.Wait();
            try { return _ec?.GetFanCount() ?? 2; }
            finally { _ecLock.Release(); }
        }

        private EcData EcReadRaw(int ch)
        {
            _ecLock.Wait();
            try { return _ec == null ? default(EcData) : _ec.ReadRaw(ch); }
            finally { _ecLock.Release(); }
        }

        private int EcGetDuty(int ch)
        {
            _ecLock.Wait();
            try { return _ec?.GetDutyPercent(ch) ?? 0; }
            finally { _ecLock.Release(); }
        }

        private int EcGetCpuRpmLocked()
        {
            _ecLock.Wait();
            try { return _ec?.GetCpuRpm() ?? 0; }
            finally { _ecLock.Release(); }
        }

        private int EcGetGpuRpmLocked()
        {
            _ecLock.Wait();
            try { return _ec?.GetGpuRpm() ?? 0; }
            finally { _ecLock.Release(); }
        }

        private void EcSetFanPercent(int ch, int pct)
        {
            _ecLock.Wait();
            try
            {
                if (_ec == null) throw new InvalidOperationException("EC尚未初始化。");
                _ec.SetFanPercent(ch, pct);
            }
            finally { _ecLock.Release(); }
        }

        private void EcSetFanAuto(int ch)
        {
            _ecLock.Wait();
            try { _ec?.SetFanAuto(ch); }
            finally { _ecLock.Release(); }
        }

        private void EcRestoreAllAuto()
        {
            _ecLock.Wait();
            try { _ec?.RestoreAllAuto(); }
            finally { _ecLock.Release(); }
        }

        // 异步EC访问版本（用于初始化路径，不阻塞UI线程）
        private async System.Threading.Tasks.Task<int> EcGetCountAsync()
        {
            await _ecLock.WaitAsync();
            try { return _ec?.GetFanCount() ?? 2; }
            finally { _ecLock.Release(); }
        }

        private int EcGetTemperatureC(int fanNumber)
        {
            _ecLock.Wait();
            try { return _ec.GetTemperatureC(fanNumber); }
            finally { _ecLock.Release(); }
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

            SetRunMode((RunMode)_modeCombo.SelectedItem, "User request");
        }

        private void SetRunMode(RunMode requestedMode, string reason)
        {
            if (requestedMode == RunMode.Active)
            {
                if (_ec == null)
                {
                    MessageBox.Show("EC 接口不可用，无法启动活动模式。", "X15 风扇控制", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    requestedMode = RunMode.ReadOnly;
                }
                else
                {
                    IList<string> conflicts = ConflictDetector.FindConflicts();
                    if (conflicts.Count > 0)
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
                        else
                        {
                            DialogResult confirmation = MessageBox.Show(
                                "活动模式将风扇占空比写入嵌入式控制器。\r\n\r\n" +
                                "无效传感器、退出、休眠或异常时会恢复自动控制。\r\n\r\n是否启动活动模式？",
                                "启用硬件控制",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2);
                            if (confirmation != DialogResult.Yes)
                                requestedMode = RunMode.ReadOnly;
                        }
                    }
                }
            }

            // ReadOnly/Simulation 必须明确交还 EC；Active 才启动心跳与看门狗。
            if (requestedMode != RunMode.Active)
            {
                EcRestoreAllAuto();
                StopWatchdog();
                _heartbeat?.WriteStop();
            }

            _runMode = requestedMode;
            _engine?.Reset();
            _modeCombo.SelectedItem = _runMode;
            UpdateModeStatus();
            FlashModeBadge();

            if (_runMode == RunMode.Active)
            {
                _heartbeat?.WriteActive(Process.GetCurrentProcess().Id);
                StartWatchdog();
            }

            AppendLog("运行模式切换为 " + _runMode + "。原因：" + reason + "。");
        }

    private void MainTimerTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if (_closing || _ec == null)
            {
                return;
            }

            try
            {
                FanSnapshot snapshot = ReadSnapshot(now);
                _lastSnapshot = snapshot;

                // 传感器停滞检测
                DetectSensorStalls(snapshot);

                if (_calibrationActive)
                {
                    CalibrationTick(snapshot);
                    UpdateDashboard(snapshot, null);
                    return;
                }

                ControlDecision decision = _engine.Update(snapshot);
                if (decision.RequestAutoFallback)
                {
                    RestoreAuto("控制器请求自动故障保护");
                    SetRunMode(RunMode.ReadOnly, "传感器故障保护");
                    UpdateDashboard(snapshot, decision);
                    return;
                }

                if (_runMode == RunMode.Active)
                {
                    WriteDecision(decision, now);
                    _heartbeat?.WriteActive(Process.GetCurrentProcess().Id);
                }

                UpdateDashboard(snapshot, decision);
                _csvLogger?.Write(snapshot, decision);
                _lastTickUtc = now;
            }
            catch (Exception exception)
            {
                AppendLog("控制循环错误：" + exception);
                RestoreAuto("控制循环异常");
                SetRunMode(RunMode.ReadOnly, "异常故障保护");
            }
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

            CancellationTokenSource cts;
            lock (_verificationLock)
            {
                if (channel == 1)
                {
                    _latestCpuVerificationSequence = seqId;
                    if (_cpuVerificationCts != null)
                    {
                        try { _cpuVerificationCts.Cancel(); } catch { }
                        _cpuVerificationCts.Dispose();
                    }
                    _cpuVerificationCts = new CancellationTokenSource();
                    cts = _cpuVerificationCts;
                }
                else
                {
                    _latestGpuVerificationSequence = seqId;
                    if (_gpuVerificationCts != null)
                    {
                        try { _gpuVerificationCts.Cancel(); } catch { }
                        _gpuVerificationCts.Dispose();
                    }
                    _gpuVerificationCts = new CancellationTokenSource();
                    cts = _gpuVerificationCts;
                }
            }

            CancellationToken token = cts.Token;
            var stopwatch = Stopwatch.StartNew();

            // 在后台线程执行异步验证，不影响控制循环
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // 检查是否已被取代
                    if (!IsLatestVerification(channel, seqId, token)) return;

                    // 50ms回读
                    await System.Threading.Tasks.Task.Delay(50, token);
                    if (!IsLatestVerification(channel, seqId, token)) return;
                    int duty50 = 0, rpmAt50 = 0;
                    await _ecLock.WaitAsync();
                    try
                    {
                        duty50 = _ec.ReadRaw(channel).FanDuty;
                        rpmAt50 = channel == 1 ? _ec.GetCpuRpm() : _ec.GetGpuRpm();
                    }
                    finally { _ecLock.Release(); }
                    double pct50 = duty50 * 100.0 / 255.0;
                    long delay50 = stopwatch.ElapsedMilliseconds;
                    LogVerification(seqId, channel, "50ms", requestedPercent, duty50, pct50, beforeDuty, delay50, rpmAt50, beforeRpm);

                    // 200ms回读
                    await System.Threading.Tasks.Task.Delay(150, token); // 50+150=200
                    if (!IsLatestVerification(channel, seqId, token)) return;
                    int duty200 = 0, rpmAt200 = 0;
                    await _ecLock.WaitAsync();
                    try
                    {
                        duty200 = _ec.ReadRaw(channel).FanDuty;
                        rpmAt200 = channel == 1 ? _ec.GetCpuRpm() : _ec.GetGpuRpm();
                    }
                    finally { _ecLock.Release(); }
                    double pct200 = duty200 * 100.0 / 255.0;
                    long delay200 = stopwatch.ElapsedMilliseconds;
                    LogVerification(seqId, channel, "200ms", requestedPercent, duty200, pct200, beforeDuty, delay200, rpmAt200, beforeRpm);

                    // 1000ms回读 + RPM响应检查
                    await System.Threading.Tasks.Task.Delay(800, token); // 200+800=1000
                    if (!IsLatestVerification(channel, seqId, token)) return;
                    int duty1000 = 0, rpm1000 = 0;
                    await _ecLock.WaitAsync();
                    try
                    {
                        duty1000 = _ec.ReadRaw(channel).FanDuty;
                        rpm1000 = channel == 1 ? _ec.GetCpuRpm() : _ec.GetGpuRpm();
                    }
                    finally { _ecLock.Release(); }
                    double pct1000 = duty1000 * 100.0 / 255.0;
                    long delay1000 = stopwatch.ElapsedMilliseconds;
                    LogVerification(seqId, channel, "1000ms", requestedPercent, duty1000, pct1000, beforeDuty, delay1000, rpm1000, beforeRpm);

                    // RPM方向检查
                    int beforeDutyPct = beforeDuty * 100 / 255;
                    int expectedDir = requestedPercent - beforeDutyPct;
                    bool rpmDataValid = beforeRpm > 0 && rpm1000 > 0;
                    bool rpmDirectionOk = true;
                    if (rpmDataValid)
                    {
                        int rpmDelta = rpm1000 - beforeRpm;
                        if (Math.Abs(expectedDir) >= 3)
                        {
                            if (expectedDir > 0 && rpmDelta < -200)
                                rpmDirectionOk = false; // 占空比上升但RPM下降
                            else if (expectedDir < 0 && rpmDelta > 200)
                                rpmDirectionOk = false; // 占空比下降但RPM上升
                        }
                        if (!rpmDirectionOk)
                            AppendLog($"  [seq={seqId}] RPM方向异常: 占空比变化 {expectedDir:+0;-#}%, RPM变化 {rpmDelta:+0;#0}");
                    }

                    // 3000ms RPM最终确认
                    await System.Threading.Tasks.Task.Delay(2000, token); // 1000+2000=3000
                    if (!IsLatestVerification(channel, seqId, token)) return;
                    int rpm3000 = 0;
                    await _ecLock.WaitAsync();
                    try { rpm3000 = channel == 1 ? _ec.GetCpuRpm() : _ec.GetGpuRpm(); }
                    finally { _ecLock.Release(); }
                    long delay3000 = stopwatch.ElapsedMilliseconds;
                    AppendLog($"  [seq={seqId}] [{delay3000}ms] 最终RPM: {rpm3000} (写入前={beforeRpm})");

                    // 外部覆盖检测：50ms匹配但1000ms被改回
                    if (Math.Abs(pct50 - requestedPercent) <= 2.0 && Math.Abs(pct1000 - requestedPercent) > 3.0)
                        AppendLog($"  [seq={seqId}] ⚠ 外部覆盖检测: 50ms差异={Math.Abs(pct50 - requestedPercent):F1}%, 1000ms差异={Math.Abs(pct1000 - requestedPercent):F1}%, 写入被改回");
                }
                catch (OperationCanceledException)
                {
                    AppendLog($"  [seq={seqId}] 验证被更新写入取代 (Superseded)");
                }
                catch (Exception ex)
                {
                    AppendLog($"  [seq={seqId}] 异步验证异常: {ex.Message}");
                }
            });
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
            lock (_verificationLock)
            {
                _latestCpuVerificationSequence = -1;
                _latestGpuVerificationSequence = -1;
                if (_cpuVerificationCts != null)
                {
                    try { _cpuVerificationCts.Cancel(); } catch { }
                    _cpuVerificationCts.Dispose();
                    _cpuVerificationCts = null;
                }
                if (_gpuVerificationCts != null)
                {
                    try { _gpuVerificationCts.Cancel(); } catch { }
                    _gpuVerificationCts.Dispose();
                    _gpuVerificationCts = null;
                }
            }
        }

        private void LogVerification(int seqId, int ch, string label, int target, int duty, double pct, int beforeDuty, long actualDelayMs, int rpm, int beforeRpm)
        {
            double diff = Math.Abs(pct - target);
            AppendLog(string.Format("  [seq={0}] [{1}] Ch{2} 写入前占空={3}({4:F1}%), 目标={5}%, EC回读={6}({7:F1}%), 差异={8:F1}%, 实际延迟={9}ms, RPM={10}(前{11})",
                seqId, label, ch, beforeDuty, beforeDuty * 100.0 / 255.0, target, duty, pct, diff, actualDelayMs, rpm, beforeRpm));
        }

        private FanSnapshot ReadSnapshot(DateTime timestampUtc)
        {
            EcData cpuRaw = EcReadRaw(1);
            EcData gpuRaw = EcReadRaw(2);
            int gpuDuty = EcGetDuty(2);
            int cpuDuty = EcGetDuty(1);

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
                CpuRpm = EcGetCpuRpmLocked(),
                GpuRpm = EcGetGpuRpmLocked(),
                GpuTelemetryAvailable = gpuTelemetryOk,
                GpuTelemetryUtilization = gpuTelemetryOk ? _lastGpuTelemetry.UtilizationPercent : 0,
                GpuTelemetryPowerWatts = gpuTelemetryOk ? _lastGpuTelemetry.PowerWatts : 0,
                GpuTelemetryPState = gpuTelemetryOk ? (_lastGpuTelemetry.PState ?? "") : "",
                GpuTelemetrySource = gpuTelemetryOk ? (_lastGpuTelemetry.SourceName ?? "") : "",
                TimestampUtc = timestampUtc
            };
        }

        private void WriteDecision(ControlDecision decision, DateTime now)
        {
            if (decision.Cpu.ShouldWrite)
            {
                EcData before = EcReadRaw(1);
                int beforeRpm = EcGetCpuRpmLocked();
                EcSetFanPercent(1, decision.Cpu.WritePercent);
                _engine.MarkCpuWritten(decision.Cpu.WritePercent, now);

                double beforePercent = before.FanDuty * 100.0 / 255.0;
                if (Math.Abs(decision.Cpu.WritePercent - beforePercent) >= 2.0)
                    StartWriteVerification(1, decision.Cpu.WritePercent, before.FanDuty, beforeRpm);
            }

            if (decision.Gpu.ShouldWrite)
            {
                EcData before = EcReadRaw(2);
                int beforeRpm = EcGetGpuRpmLocked();
                EcSetFanPercent(2, decision.Gpu.WritePercent);
                _engine.MarkGpuWritten(decision.Gpu.WritePercent, now);

                double beforePercent = before.FanDuty * 100.0 / 255.0;
                if (Math.Abs(decision.Gpu.WritePercent - beforePercent) >= 2.0)
                    StartWriteVerification(2, decision.Gpu.WritePercent, before.FanDuty, beforeRpm);
            }
        }

        private void UpdateDashboard(FanSnapshot snapshot, ControlDecision decision)
        {
            _cpuTempLabel.Text = snapshot.CpuTemperatureC > 0 ? snapshot.CpuTemperatureC + " °C" : "—";
            _cpuDutyLabel.Text = snapshot.CpuDutyPercent + "%";
            _cpuRpmLabel.Text = snapshot.CpuRpm > 0 ? snapshot.CpuRpm.ToString() : "—";

            // CPU控制状态
            string cpuStateStr = "—";
            if (decision != null && decision.Cpu != null)
            {
                cpuStateStr = GetControlStateText(decision.Cpu.State, decision.Cpu.DownHoldRemainingSeconds);
                _cpuFilteredLabel.Text = decision.Cpu.ControlTemperatureC.ToString("0.0") + " °C";
                _cpuTargetLabel.Text = decision.Cpu.AppliedPercent.ToString("0.0") + "%";
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
                _gpuTempLabel.Text = snapshot.GpuTemperatureC + " °C";
                _gpuTempLabel.ForeColor = Color.Black;
                _gpuNvidiaUtilLabel.Text = _lastGpuTelemetry.UtilizationPercent + "%";
                _gpuNvidiaPowerLabel.Text = _lastGpuTelemetry.PowerWatts.ToString("F1") + " W";
                _gpuNvidiaPStateLabel.Text = _lastGpuTelemetry.PState ?? "N/A";
                _gpuNvidiaSourceLabel.Text = _lastGpuTelemetry.SourceName ?? "nvidia-smi";
                _gpuNvidiaStatusLabel.Text = "正常";
                _gpuNvidiaStatusLabel.ForeColor = Color.DarkGreen;

                if (decision != null && decision.Gpu != null)
                {
                    gpuStateStr = GetControlStateText(decision.Gpu.State, decision.Gpu.DownHoldRemainingSeconds);
                    _gpuFilteredLabel.Text = decision.Gpu.ControlTemperatureC.ToString("0.0") + " °C";
                    _gpuTargetLabel.Text = decision.Gpu.AppliedPercent.ToString("0.0") + "%";
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
                _gpuTempLabel.Text = "—";
                _gpuTempLabel.ForeColor = Color.Gray;
                _gpuNvidiaUtilLabel.Text = "—";
                _gpuNvidiaPowerLabel.Text = "—";
                _gpuNvidiaPStateLabel.Text = "—";
                _gpuNvidiaSourceLabel.Text = _lastGpuTelemetry?.SourceName ?? "—";
                _gpuNvidiaStatusLabel.Text = status;
                _gpuNvidiaStatusLabel.ForeColor = statusColor;
            }
            _gpuDutyLabel.Text = snapshot.GpuDutyPercent + "%";
            _gpuRpmLabel.Text = snapshot.GpuRpm > 0 ? snapshot.GpuRpm.ToString() : "—";

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
            string riseRateStr = decision != null && decision.Cpu != null
                ? string.Format("CPU温升速率: {0:F2}°C/s", decision.Cpu.TemperatureRiseRateCPerSec)
                : "";
            _hardwareStatusLabel.Text = string.Format("EC CPU ch1: R={0}°C L={1}°C | EC GPU ch2: R={2}°C L={3}°C | CPU转速={4} GPU转速={5} | {6}",
                snapshot.CpuTemperatureC > 0 ? snapshot.CpuTemperatureC.ToString() : "—",
                snapshot.CpuTemperatureLocalC,
                snapshot.GpuTemperatureLocalC,
                _lastSnapshot != null ? _lastSnapshot.GpuTemperatureLocalC.ToString() : "—",
                snapshot.CpuRpm > 0 ? snapshot.CpuRpm.ToString() : "—",
                snapshot.GpuRpm > 0 ? snapshot.GpuRpm.ToString() : "—",
                riseRateStr);

            if (_gpuTelemetryReady && _lastGpuTelemetry != null && !_lastGpuTelemetry.IsStale)
                _hardwareStatusLabel.ForeColor = Color.DarkGreen;
            else
                _hardwareStatusLabel.ForeColor = Color.OrangeRed;

            if (decision == null)
            {
                _cpuFilteredLabel.Text = "—";
                _gpuFilteredLabel.Text = "—";
                _cpuTargetLabel.Text = "—";
                _gpuTargetLabel.Text = "—";
                return;
            }

            _cpuFilteredLabel.Text = decision.Cpu.ControlTemperatureC.ToString("0.0") + " °C";
            _gpuFilteredLabel.Text = decision.Gpu.ControlTemperatureC.ToString("0.0") + " °C";
            _cpuTargetLabel.Text = decision.Cpu.AppliedPercent.ToString("0.0") + "%";
            _gpuTargetLabel.Text = decision.Gpu.AppliedPercent.ToString("0.0") + "%";

            AddHistoryPoint("CPU 温度", snapshot.CpuTemperatureC);
            AddHistoryPoint("GPU 温度", _gpuTelemetryReady ? snapshot.GpuTemperatureC : 0);
            AddHistoryPoint("CPU 设定", decision.Cpu.AppliedPercent);
            AddHistoryPoint("GPU 设定", decision.Gpu.AppliedPercent);
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

        private void RunEcProbe()
        {
            AppendLog("===== EC通道诊断探测开始 =====");
            if (_ec == null)
            {
                AppendLog("EC未初始化，无法探测。");
                return;
            }

            try
            {
                int fanCount = EcGetCount();
                AppendLog("EC报告的通道数：" + fanCount);

                for (int ch = 0; ch <= 3; ch++)
                {
                    EcData raw = EcReadRaw(ch);
                    AppendLog(string.Format(
                        "  通道{0}: Remote={1}°C, Local={2}°C, FanDuty={3}({4:F1}%), Reserve={5}",
                        ch, raw.Remote, raw.Local, raw.FanDuty,
                        raw.FanDuty * 100.0 / 255.0, raw.Reserve));
                }

                // CPU/GPU 转速
                AppendLog(string.Format("CPU转速: {0}, GPU转速: {1}",
                    EcGetCpuRpmLocked() > 0 ? EcGetCpuRpmLocked().ToString() : "—",
                    EcGetGpuRpmLocked() > 0 ? EcGetGpuRpmLocked().ToString() : "—"));

                // 诊断建议
                EcData ch1 = EcReadRaw(1);
                EcData ch2 = EcReadRaw(2);

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

        private async System.Threading.Tasks.Task RunEcProbeAsync()
        {
            AppendLog("===== EC通道诊断探测开始 =====");
            if (_ec == null)
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
            await _ecLock.WaitAsync();
            try { return _ec == null ? default(EcData) : _ec.ReadRaw(ch); }
            finally { _ecLock.Release(); }
        }

        private async System.Threading.Tasks.Task<int> EcGetCpuRpmAsync()
        {
            await _ecLock.WaitAsync();
            try { return _ec?.GetCpuRpm() ?? 0; }
            finally { _ecLock.Release(); }
        }

        private async System.Threading.Tasks.Task<int> EcGetGpuRpmAsync()
        {
            await _ecLock.WaitAsync();
            try { return _ec?.GetGpuRpm() ?? 0; }
            finally { _ecLock.Release(); }
        }

        private void AddHistoryPoint(string seriesName, double value)
        {
            if (_historyChart == null)
            {
                return;
            }

            var series = _historyChart.Series[seriesName];
            series.Points.AddY(value);
            while (series.Points.Count > 300)
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

        private void SaveConfig()
        {
            try
            {
                _configStore?.Save(_config);
            }
            catch (Exception exception)
            {
                AppendLog("保存配置失败：" + exception.Message);
            }
        }

        private void AppendLog(string message)
        {
            if (_logTextBox != null && !_logTextBox.IsDisposed && _logTextBox.InvokeRequired)
            {
                try { _logTextBox.BeginInvoke(new Action<string>(AppendLog), message); }
                catch { }
                return;
            }

            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            if (_logTextBox != null && !_logTextBox.IsDisposed)
                _logTextBox.AppendText(line + Environment.NewLine);

            try
            {
                File.AppendAllText(Path.Combine(_dataDirectory, "application.log"), DateTime.Now.ToString("O") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        private void MainFormResize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
            }
        }

        private void SystemEventsPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                    RestoreAuto("系统休眠");
                    StopWatchdog();
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
                                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
                                _ecLock.Wait();
                                try { _ec = new ClevoEcInfo(dllPath); }
                                finally { _ecLock.Release(); }
                            }
                            catch { }
                        });

                        _engine?.Reset();
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
            _closing = true;
            _mainTimer.Stop();
            InvalidateVerificationTasks();
            StopCalibration("应用程序关闭");
            RestoreAuto("应用程序关闭");
            StopWatchdog();
            _heartbeat?.WriteStop();
            _csvLogger?.Dispose();
            _csvLogger = null;
            if (_gpuTelemetry != null)
            {
                _gpuTelemetry.Dispose();
                _gpuTelemetry = null;
            }
            DisposeEc();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            SystemEvents.PowerModeChanged -= SystemEventsPowerModeChanged;
        }

        private void DisposeEc()
        {
            if (_ec != null)
            {
                try
                {
                    _ec.Dispose();
                }
                catch
                {
                }
                _ec = null;
            }
        }
    }
}
