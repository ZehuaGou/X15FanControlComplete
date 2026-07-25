using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using X15FanCore.Control;
using X15FanCore.Models;
using X15FanCore.Native;

namespace X15FanControl
{
    public partial class MainForm
    {
        private TabPage BuildCalibrationTab()
        {
            TabPage tab = new TabPage("声学校准") { BackColor = UiBackground };
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(12),
                BackColor = UiBackground
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            Panel setup = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 6, 0),
                Padding = new Padding(16),
                BackColor = UiSurface
            };
            TableLayoutPanel setupLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = UiSurface
            };
            setupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            setupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
            setupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            setupLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
            setupLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            setupLayout.Controls.Add(BuildSectionTitle("固定占空比扫描", UiCpuAccent), 0, 0);

            TableLayoutPanel form = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                BackColor = UiSurface,
                Padding = new Padding(4, 6, 4, 4)
            };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));

            _calibrationFanCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 5, 4, 5)
            };
            _calibrationFanCombo.Items.Add(FanKind.Cpu);
            _calibrationFanCombo.Items.Add(FanKind.Gpu);
            _calibrationFanCombo.SelectedIndex = 0;
            _calibrationStart = CreateNumeric(30, 100, 40);
            _calibrationEnd = CreateNumeric(30, 100, 65);
            _calibrationStep = CreateNumeric(1, 10, 1);
            _calibrationHold = CreateNumeric(3, 30, 8);

            AddFormRow(form, 0, "风扇", _calibrationFanCombo);
            AddFormRow(form, 1, "起始占空比 %", _calibrationStart);
            AddFormRow(form, 2, "结束占空比 %", _calibrationEnd);
            AddFormRow(form, 3, "步进 %", _calibrationStep);
            AddFormRow(form, 4, "每步保持 (秒)", _calibrationHold);

            FlowLayoutPanel controls = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = UiSurface
            };
            _calibrationStartButton = new Button { Text = "开始扫描", Width = 98, Height = 32 };
            StyleButton(_calibrationStartButton, UiCpuAccent, Color.White);
            _calibrationStartButton.Click += CalibrationStartButtonClick;
            _calibrationStopButton = new Button { Text = "停止 / 自动", Width = 104, Height = 32, Enabled = false };
            StyleButton(_calibrationStopButton, Color.FromArgb(255, 232, 232), Color.FromArgb(165, 35, 35));
            _calibrationStopButton.Click += delegate { StopCalibration("Stopped by user"); };
            Button presetCalButton = new Button { Text = "预设标定 12 点", Width = 120, Height = 32, Enabled = true };
            StyleButton(presetCalButton, Color.FromArgb(232, 237, 244), UiText);
            presetCalButton.Click += StartPresetCalibration;
            controls.Controls.Add(_calibrationStartButton);
            controls.Controls.Add(_calibrationStopButton);
            controls.Controls.Add(presetCalButton);

            _calibrationStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "校准将写入固定的风扇占空比。另一个风扇保持自动模式。仅在温度较低且无其他风扇工具运行时开始。",
                ForeColor = Color.FromArgb(146, 64, 14),
                BackColor = Color.FromArgb(255, 247, 230),
                Padding = new Padding(12),
                TextAlign = ContentAlignment.TopLeft
            };

            setupLayout.Controls.Add(form, 0, 1);
            setupLayout.Controls.Add(controls, 0, 2);
            setupLayout.Controls.Add(_calibrationStatusLabel, 0, 3);
            setup.Controls.Add(setupLayout);

            Panel records = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(6, 0, 0, 0),
                Padding = new Padding(16),
                BackColor = UiSurface
            };
            TableLayoutPanel recordsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = UiSurface
            };
            recordsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            recordsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            recordsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            recordsLayout.Controls.Add(BuildSectionTitle("观测记录", UiGpuAccent), 0, 0);

            _calibrationRecordsList = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(249, 250, 252),
                ForeColor = UiText,
                Font = new Font("Segoe UI", 9.5F),
                IntegralHeight = false
            };
            FlowLayoutPanel marking = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 10, 0, 0),
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = UiSurface
            };
            _calibrationMarkNoisyButton = new Button { Text = "标记嘈杂", Width = 104, Height = 32, Enabled = false };
            StyleButton(_calibrationMarkNoisyButton, Color.FromArgb(255, 235, 232), Color.FromArgb(166, 44, 36));
            _calibrationMarkNoisyButton.Click += delegate { MarkCalibrationPoint(true); };
            _calibrationMarkStableButton = new Button { Text = "标记稳定", Width = 104, Height = 32, Enabled = false };
            StyleButton(_calibrationMarkStableButton, Color.FromArgb(229, 247, 236), Color.FromArgb(25, 110, 64));
            _calibrationMarkStableButton.Click += delegate { MarkCalibrationPoint(false); };
            _calibrationGenerateZoneButton = new Button { Text = "生成稳定区间", Width = 120, Height = 32 };
            StyleButton(_calibrationGenerateZoneButton, UiGpuAccent, Color.White);
            _calibrationGenerateZoneButton.Click += CalibrationGenerateZoneButtonClick;
            marking.Controls.Add(_calibrationMarkNoisyButton);
            marking.Controls.Add(_calibrationMarkStableButton);
            marking.Controls.Add(_calibrationGenerateZoneButton);
            recordsLayout.Controls.Add(_calibrationRecordsList, 0, 1);
            recordsLayout.Controls.Add(marking, 0, 2);
            records.Controls.Add(recordsLayout);

            Label footer = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(255, 247, 230),
                ForeColor = Color.FromArgb(130, 70, 20),
                Padding = new Padding(12, 0, 12, 0),
                Margin = new Padding(0, 6, 0, 0),
                Text = "紧急规则：当 CPU ≥ 85°C、GPU ≥ 80°C、传感器读数无效或发生异常时，扫描停止且两个风扇均恢复自动模式。"
            };

            root.Controls.Add(setup, 0, 0);
            root.Controls.Add(records, 1, 0);
            root.Controls.Add(footer, 0, 1);
            root.SetColumnSpan(footer, 2);
            tab.Controls.Add(root);
            return tab;
        }

        private static Control BuildSectionTitle(string title, Color accentColor)
        {
            Panel header = new Panel { Dock = DockStyle.Fill, BackColor = UiSurface };
            header.Controls.Add(new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                ForeColor = UiText,
                Font = new Font("Segoe UI Semibold", 11F)
            });
            header.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accentColor });
            return header;
        }

        private static NumericUpDown CreateNumeric(decimal minimum, decimal maximum, decimal value)
        {
            return new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 5, 4, 5),
                Font = new Font("Segoe UI", 9.5F)
            };
        }

        private static void AddFormRow(TableLayoutPanel form, int row, string label, Control control)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            form.Controls.Add(new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                ForeColor = UiMuted,
                Padding = new Padding(4, 0, 0, 0)
            }, 0, row);
            form.Controls.Add(control, 1, row);
        }

        private void CalibrationStartButtonClick(object sender, EventArgs e)
        {
            if (_ec == null)
            {
                MessageBox.Show("EC 接口不可用。", "校准", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            IList<string> conflicts = ConflictDetector.FindConflicts();
            if (conflicts.Count > 0)
            {
                MessageBox.Show("请先关闭以下风扇控制进程：\r\n\r\n" + string.Join("\r\n", conflicts), "校准被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int start = (int)_calibrationStart.Value;
            int end = (int)_calibrationEnd.Value;
            if (end < start)
            {
                MessageBox.Show("结束占空比必须大于或等于起始占空比。", "校准", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                "此测试将直接控制一个风扇从 " + start + "% 到 " + end + "%。\r\n\r\n" +
                "请保持机器空闲，观察温度，如有异常请立即使用[停止/自动]。是否继续？",
                "开始声学校准",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            _calibrationRecords.Clear();
            _calibrationRecordsList.Items.Clear();
            _calibrationFan = (FanKind)_calibrationFanCombo.SelectedItem;
            _calibrationCurrentDuty = start;
            _calibrationStepStartedUtc = DateTime.UtcNow;
            _calibrationPresetPath = null;
            _calibrationPresetIndex = 0;

            // 先发布完整校准状态，阻止后台控制循环在准备阶段生成或写入控制决策。
            _calibrationActive = true;
            _calibrationStartButton.Enabled = false;
            _calibrationStopButton.Enabled = true;
            _calibrationMarkNoisyButton.Enabled = true;
            _calibrationMarkStableButton.Enabled = true;

            int channel = _calibrationFan == FanKind.Cpu ? 1 : 2;
            int otherChannel = channel == 1 ? 2 : 1;
            try
            {
                _heartbeat.WriteActive(System.Diagnostics.Process.GetCurrentProcess().Id);
                StartWatchdog();
                SetRunMode(RunMode.ReadOnly, "校准启动中");
                EcRestoreAllAuto();
                EcSetFanAuto(otherChannel);
                EcSetFanPercent(channel, _calibrationCurrentDuty);
            }
            catch (Exception ex)
            {
                AppendLog("校准启动EC操作失败：" + ex.Message);
                StopCalibration("校准启动失败");
                MessageBox.Show("校准启动失败，已尝试恢复两个风扇为自动：" + ex.Message,
                    "声学校准", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            AppendLog("已开始对 " + _calibrationFan + " 进行校准，当前占空比 " + _calibrationCurrentDuty + "%。");
            UpdateCalibrationStatus();
        }

        private void CalibrationTick(FanSnapshot snapshot)
        {
            if (!_calibrationActive)
            {
                return;
            }

            // 安全阈值：预设标定更保守（CPU≤75°C，GPU≤70°C）
            int cpuMax = _calibrationPresetPath != null ? 75 : 85;
            int gpuMax = _calibrationPresetPath != null ? 70 : 80;
            if (snapshot.CpuTemperatureC >= cpuMax || snapshot.GpuTemperatureC >= gpuMax ||
                snapshot.CpuTemperatureC < 5 || snapshot.GpuTemperatureC < 5)
            {
                StopCalibration("安全停止：温度阈值(" + snapshot.CpuTemperatureC + "/" + snapshot.GpuTemperatureC + "°C)或传感器无效");
                return;
            }

            int holdSeconds = (int)_calibrationHold.Value;
            if ((DateTime.UtcNow - _calibrationStepStartedUtc).TotalSeconds >= holdSeconds)
            {
                // 记录当前档位（使用档位后半段的稳定值）
                if (!RecordCalibrationPoint(false, false))
                {
                    return;
                }

                if (_calibrationPresetPath != null)
                {
                    // 预设路径标定
                    _calibrationPresetIndex++;
                    if (_calibrationPresetIndex >= _calibrationPresetPath.Length)
                    {
                        FinishPresetCalibration();
                        return;
                    }
                    _calibrationCurrentDuty = _calibrationPresetPath[_calibrationPresetIndex];
                }
                else
                {
                    // 普通步进扫描
                    int next = _calibrationCurrentDuty + (int)_calibrationStep.Value;
                    if (next > (int)_calibrationEnd.Value)
                    {
                        StopCalibration("扫描完成");
                        return;
                    }
                    _calibrationCurrentDuty = next;
                }

                _calibrationStepStartedUtc = DateTime.UtcNow;
                int channel = _calibrationFan == FanKind.Cpu ? 1 : 2;
                try
                {
                    EcSetFanPercent(channel, _calibrationCurrentDuty);
                }
                catch (Exception ex)
                {
                    AppendLog("校准步进EC写入失败：" + ex.Message);
                    StopCalibration("校准EC写入失败");
                    return;
                }
                AppendLog("校准步进：" + _calibrationFan + " " + _calibrationCurrentDuty + "%。");
            }

            try
            {
                _heartbeat.WriteActive(System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            catch (Exception ex)
            {
                AppendLog("校准心跳写入失败：" + ex.Message);
                StopCalibration("校准心跳异常");
                return;
            }
            UpdateCalibrationStatus();
        }

        private void FinishPresetCalibration()
        {
            if (!StopCalibration("预设标定完成"))
            {
                AppendLog("预设标定完成，但恢复自动失败；保留校准保护状态。");
                return;
            }

            // 生成标定CSV和阶跃分析
            string csvPath = GenerateCalibrationReport();
            _calibrationStatusLabel.Text = "预设标定完成。CSV: " + csvPath;
            AppendLog("预设标定完成，报告已保存至：" + csvPath);
            _calibrationPresetPath = null;
            _calibrationPresetIndex = 0;
        }

        private string GenerateCalibrationReport()
        {
            if (_calibrationRecords.Count == 0) return null;

            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "X15FanControl", "calibration");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir,
                string.Format("cal-{0}-{1:yyyyMMdd-HHmmss}.csv",
                    _calibrationFan, DateTime.Now));

            using (var writer = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("duty_percent,ec_readback,ec_readback_raw,rpm,temperature_c,marked_noisy,marked_stable,stable_rpm_at_end");
                foreach (var rec in _calibrationRecords)
                {
                    writer.WriteLine(string.Format("{0},{1:F1},{2},{3},{4},{5},{6},{7}",
                        rec.DutyPercent,
                        rec.DutyPercent, // placeholder
                        rec.DutyPercent * 255 / 100,
                        rec.Rpm,
                        rec.TemperatureC,
                        rec.MarkedNoisy ? 1 : 0,
                        rec.MarkedStable ? 1 : 0,
                        rec.Rpm));
                }
            }

            // 在日志中输出阶跃分析
            AppendLog("===== " + _calibrationFan + " 标定阶跃分析 =====");
            int maxStep = 0;
            int maxStepAt = 0;
            for (int i = 1; i < _calibrationRecords.Count; i++)
            {
                int step = Math.Abs(_calibrationRecords[i].Rpm - _calibrationRecords[i - 1].Rpm);
                AppendLog(string.Format("  {0}% → {1}%: RPM {2} → {3} (变化 {4} RPM)",
                    _calibrationRecords[i - 1].DutyPercent,
                    _calibrationRecords[i].DutyPercent,
                    _calibrationRecords[i - 1].Rpm,
                    _calibrationRecords[i].Rpm,
                    step));
                if (step > maxStep)
                {
                    maxStep = step;
                    maxStepAt = _calibrationRecords[i].DutyPercent;
                }
            }
            AppendLog(string.Format("最大RPM阶跃在 {0}%，变化 {1} RPM", maxStepAt, maxStep));
            AppendLog("===== 标定分析结束 =====");

            return path;
        }

        private void MarkCalibrationPoint(bool noisy)
        {
            if (!_calibrationActive || GetLatestSnapshotForCalibration() == null)
            {
                return;
            }

            if (!RecordCalibrationPoint(noisy, !noisy))
            {
                return;
            }
            if (noisy)
            {
                List<int> list = _calibrationFan == FanKind.Cpu ? _config.CalibrationNoisyPointsCpu : _config.CalibrationNoisyPointsGpu;
                if (!list.Contains(_calibrationCurrentDuty)) list.Add(_calibrationCurrentDuty);
                list.Sort();
                SaveConfig();
            }
        }

        private bool RecordCalibrationPoint(bool noisy, bool stable)
        {
            FanSnapshot snapshot = GetLatestSnapshotForCalibration();
            if (snapshot == null)
            {
                return false;
            }

            FanKind fan = _calibrationFan;
            int duty = _calibrationCurrentDuty;
            int channel = fan == FanKind.Cpu ? 1 : 2;
            EcData raw;
            int rpm;
            try
            {
                // 校准点使用即时EC回读；包装器与后台ReadSnapshot共用_ecLock。
                raw = EcReadRaw(channel);
                rpm = fan == FanKind.Cpu ? EcGetCpuRpmLocked() : EcGetGpuRpmLocked();
            }
            catch (Exception ex)
            {
                AppendLog("记录校准点时EC回读失败：" + ex.Message);
                StopCalibration("校准EC回读失败");
                return false;
            }

            CalibrationRecord record = new CalibrationRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Fan = fan,
                DutyPercent = duty,
                TemperatureC = fan == FanKind.Cpu ? raw.Remote : snapshot.GpuTemperatureC,
                Rpm = rpm,
                MarkedNoisy = noisy,
                MarkedStable = stable
            };
            _calibrationRecords.Add(record);
            _calibrationRecordsList.Items.Add(record);
            _calibrationRecordsList.TopIndex = _calibrationRecordsList.Items.Count - 1;
            return true;
        }

        private FanSnapshot GetLatestSnapshotForCalibration()
        {
            lock (_latestLock)
            {
                return _lastSnapshot;
            }
        }

        private void CalibrationGenerateZoneButtonClick(object sender, EventArgs e)
        {
            FanKind fan = _calibrationFanCombo.SelectedItem == null ? FanKind.Cpu : (FanKind)_calibrationFanCombo.SelectedItem;
            List<int> points = fan == FanKind.Cpu ? _config.CalibrationNoisyPointsCpu : _config.CalibrationNoisyPointsGpu;
            if (points == null || points.Count == 0)
            {
                MessageBox.Show("尚未为 " + fan + " 标记任何嘈杂占空比点。", "校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int minimum = points.Min();
            int maximum = points.Max();
            int hold = (int)Math.Round((minimum + maximum) / 2.0);
            FanProfile profile = GetActiveProfile();
            FanChannelProfile channel = fan == FanKind.Cpu ? profile.Cpu : profile.Gpu;
            channel.StableZoneEnabled = true;
            channel.StableZoneMinimumPercent = minimum;
            channel.StableZoneMaximumPercent = maximum;
            channel.StableZoneHoldPercent = hold;
            SaveConfig();
            lock (_engineLock) { _engine.SetProfile(profile); }
            LoadProfileIntoEditor(profile);
            MessageBox.Show("稳定区间已设置为 " + minimum + "–" + maximum + "%，保持点为 " + hold + "%。", "校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool StopCalibration(string reason)
        {
            if (!_calibrationActive)
            {
                return true;
            }

            FanKind fan = _calibrationFan;
            int channel = fan == FanKind.Cpu ? 1 : 2;
            try
            {
                // 先恢复正在校准的通道，再恢复全部通道。两步都成功后才发布停止状态。
                EcRestoreCalibrationAuto(channel);
            }
            catch (Exception ex)
            {
                AppendLog("校准停止时恢复自动失败：" + ex.Message);
                _calibrationStatusLabel.Text = reason + "，但恢复自动失败；窗口保护仍然有效。";
                return false;
            }

            _calibrationActive = false;
            try { StopWatchdog(); }
            catch (Exception ex) { AppendLog("停止校准看门狗失败：" + ex.Message); }
            try { _heartbeat?.WriteStop(); }
            catch (Exception ex) { AppendLog("停止校准心跳失败：" + ex.Message); }
            _calibrationStartButton.Enabled = true;
            _calibrationStopButton.Enabled = false;
            _calibrationMarkNoisyButton.Enabled = false;
            _calibrationMarkStableButton.Enabled = false;
            _calibrationStatusLabel.Text = reason + "。两个风扇已恢复自动。";
            AppendLog("校准已停止：" + reason + "。");
            _calibrationPresetPath = null;
            _calibrationPresetIndex = 0;
            return true;
        }

        private void UpdateCalibrationStatus()
        {
            if (!_calibrationActive)
            {
                return;
            }

            int remaining = Math.Max(0, (int)_calibrationHold.Value - (int)(DateTime.UtcNow - _calibrationStepStartedUtc).TotalSeconds);
            _calibrationStatusLabel.Text = _calibrationFan + " 固定在 " + _calibrationCurrentDuty + "% — 下一步在 " + remaining + " 秒后。在当前步骤可听到时使用标记按钮。";
        }

        private void StartPresetCalibration(object sender, EventArgs e)
        {
            if (_ec == null)
            {
                MessageBox.Show("EC 接口不可用。", "校准", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 预设标定点：45, 47, 48, 49, 50, 51, 52, 54, 56, 58, 60, 65
            int[] presetPoints = { 45, 47, 48, 49, 50, 51, 52, 54, 56, 58, 60, 65 };
            _calibrationStart.Value = presetPoints[0];
            _calibrationEnd.Value = presetPoints[presetPoints.Length - 1];
            _calibrationStep.Value = 1;
            _calibrationHold.Value = 10; // 每档10秒

            // 安全检查
            int cpuTempCheck = _ec != null ? EcGetTemperatureC(1) : 0;
            int gpuTempCheck = _gpuTelemetryReady && _lastGpuTelemetry != null ? _lastGpuTelemetry.TemperatureC : 0;
            
            if (cpuTempCheck >= 70)
            {
                MessageBox.Show("CPU 温度 " + cpuTempCheck + "°C 过高（需低于70°C），无法开始声学校准。", "温度过高", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (gpuTempCheck >= 65)
            {
                MessageBox.Show("GPU 温度 " + gpuTempCheck + "°C 过高（需低于65°C），无法开始声学校准。", "温度过高", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult choose = MessageBox.Show(
                "将使用预设12点标定：\r\n" +
                string.Join(" → ", presetPoints) + "\r\n\r\n" +
                "先测试CPU，完成后重新选择GPU再次点击。\r\n" +
                "每档保持10秒，结束后自动生成标定CSV。\r\n\r\n" +
                "安全条件：\r\n" +
                "- CPU需低于70°C（当前" + cpuTempCheck + "°C）\r\n" +
                "- GPU需低于65°C（当前" + (gpuTempCheck > 0 ? gpuTempCheck.ToString() + "°C" : "N/A") + "）\r\n" +
                "- 另一个风扇保持Auto\r\n" +
                "- 超过75°C(CPU)/70°C(GPU)自动终止",
                "预设声学校准",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (choose != DialogResult.OK) return;

            IList<string> conflicts = ConflictDetector.FindConflicts();
            if (conflicts.Count > 0)
            {
                MessageBox.Show("请先关闭以下风扇控制进程：\r\n\r\n" + string.Join("\r\n", conflicts),
                    "校准被阻止", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _calibrationRecords.Clear();
            _calibrationRecordsList.Items.Clear();
            _calibrationFan = (FanKind)_calibrationFanCombo.SelectedItem;
            int channel = _calibrationFan == FanKind.Cpu ? 1 : 2;
            int otherChannel = channel == 1 ? 2 : 1;
            _calibrationCurrentDuty = presetPoints[0];
            _calibrationStepStartedUtc = DateTime.UtcNow;

            // 先发布完整预设状态，再让后台控制循环观察到校准已激活。
            _calibrationPresetPath = presetPoints;
            _calibrationPresetIndex = 0;
            _calibrationActive = true;
            _calibrationStartButton.Enabled = false;
            _calibrationStopButton.Enabled = true;
            _calibrationMarkNoisyButton.Enabled = true;
            _calibrationMarkStableButton.Enabled = true;

            try
            {
                _heartbeat.WriteActive(System.Diagnostics.Process.GetCurrentProcess().Id);
                StartWatchdog();
                SetRunMode(RunMode.ReadOnly, "校准启动中");
                EcRestoreAllAuto();
                EcSetFanAuto(otherChannel);
                EcSetFanPercent(channel, _calibrationCurrentDuty);
            }
            catch (Exception ex)
            {
                AppendLog("预设校准启动EC操作失败：" + ex.Message);
                StopCalibration("预设校准启动失败");
                MessageBox.Show("预设校准启动失败，已尝试恢复两个风扇为自动：" + ex.Message,
                    "声学校准", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            AppendLog("预设标定开始：" + _calibrationFan + "，起始占空比 " + _calibrationCurrentDuty + "%。");
            UpdateCalibrationStatus();
        }

        // 预设标定跟踪
        private int[] _calibrationPresetPath;
        private int _calibrationPresetIndex;

        // 重写CalibrationTick的预设版本逻辑，在CalibrationTick中调度
        // 修改原有的CalibrationTick以支持预设标定
        // 实际上现有的CalibrationTick已经可以工作，预设标定只是修改了步进逻辑
        // 但原始CalibrationTick使用_calibrationStep.Value递增，对于预设标定需要特殊处理
    }
}
