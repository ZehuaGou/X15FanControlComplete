using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using X15FanCore.Control;
using X15FanCore.Models;
using X15FanCore.Native;

namespace X15FanControl
{
    public partial class MainForm : Form
    {
        private readonly string _dataDirectory;
        private readonly string _configPath;
        private readonly string _heartbeatPath;
        private readonly string _watchdogLogPath;
        private readonly System.Windows.Forms.Timer _mainTimer;

        // Background control loop (separate from UI thread)
        private System.Threading.CancellationTokenSource _controlCts;
        private Task _controlTask;
        private FanSnapshot _latestSnapshot;
        private ControlDecision _latestDecision;
        private readonly object _latestLock = new object();
        private int _controlLoopGuard;

        private ClevoEcInfo _ec;
        private readonly System.Threading.SemaphoreSlim _ecLock = new System.Threading.SemaphoreSlim(1, 1);
        // Write verification (async via Task.Delay)
        private int _ecSequenceId;
        private int _latestCpuVerificationSequence;
        private int _latestGpuVerificationSequence;
        private System.Threading.CancellationTokenSource _cpuVerificationCts;
        private System.Threading.CancellationTokenSource _gpuVerificationCts;
        private readonly object _verificationLock = new object();
        private AppConfig _config;
        private ConfigStore _configStore;
        private FanControlEngine _engine;
        private CsvLogger _csvLogger;
        private Heartbeat _heartbeat;
        private Process _watchdogProcess;
        private RunMode _runMode;
        private NotifyIcon _notifyIcon;
        private DateTime _lastTickUtc;
        private bool _closing;
        private bool _allowFinalClose;

        // Window behavior
        private bool _explicitExitRequested;
        private bool _trayHintShown;
        private bool _startMinimizedToTray;
        private bool _isAutoStart;
        private ToolStripMenuItem _trayModeItem;
        private ToolStripMenuItem _startupMenuItem;

        // Async logging
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private System.Threading.CancellationTokenSource _logCts;
        private Task _logFlushTask;
        private int _currentLogLines;

        // Chart downsampling
        private DateTime _lastChartSampleUtc = DateTime.MinValue;

        // Thread safety
        private readonly object _engineLock = new object();

        // Sensor stall detection (CPU only)
        private int _cpuTempStallCount;
        private int _lastCpuTemp;

        // GPU telemetry from NVIDIA
        private GpuTelemetryClient _gpuTelemetry;
        private GpuTelemetryData _lastGpuTelemetry;
        private int _gpuTelemetryValidSamples;
        private bool _gpuTelemetryReady;
        private Label _gpuNvidiaUtilLabel;
        private Label _gpuNvidiaPowerLabel;
        private Label _gpuNvidiaPStateLabel;
        private Label _gpuNvidiaSourceLabel;
        private Label _gpuNvidiaStatusLabel;

        // Write verification (async via Task.Delay)
                private ComboBox _modeCombo;
        private ComboBox _profileCombo;
        private Button _applyModeButton;
        private Button _restoreAutoButton;
        private Label _hardwareStatusLabel;
    private Label _modeStatusLabel;
    private Panel _modeStatusPanel;
    private Label _cpuTempLabel;
        private Label _cpuFilteredLabel;
        private Label _cpuDutyLabel;
        private Label _cpuTargetLabel;
        private Label _cpuRpmLabel;
        private Label _gpuTempLabel;
        private Label _gpuFilteredLabel;
        private Label _gpuDutyLabel;
        private Label _gpuTargetLabel;
        private Label _gpuRpmLabel;
        private GroupBox _cpuCardBox;
        private GroupBox _gpuCardBox;
        private Chart _historyChart;
        private TextBox _logTextBox;
        private PropertyGrid _profilePropertyGrid;
        private PropertyGrid _cpuPropertyGrid;
        private PropertyGrid _gpuPropertyGrid;
        private DataGridView _cpuCurveGrid;
        private DataGridView _gpuCurveGrid;
        private BindingList<FanCurvePoint> _cpuCurveBinding;
        private BindingList<FanCurvePoint> _gpuCurveBinding;
        private Button _saveProfileButton;
        private Button _reloadProfileButton;

        private ComboBox _calibrationFanCombo;
        private NumericUpDown _calibrationStart;
        private NumericUpDown _calibrationEnd;
        private NumericUpDown _calibrationStep;
        private NumericUpDown _calibrationHold;
        private Button _calibrationStartButton;
        private Button _calibrationStopButton;
        private Button _calibrationMarkNoisyButton;
        private Button _calibrationMarkStableButton;
        private Button _calibrationGenerateZoneButton;
        private Label _calibrationStatusLabel;
        private ListBox _calibrationRecordsList;
        private volatile bool _calibrationActive;
        private volatile FanKind _calibrationFan;
        private volatile int _calibrationCurrentDuty;
        private DateTime _calibrationStepStartedUtc;
        private readonly List<CalibrationRecord> _calibrationRecords = new List<CalibrationRecord>();
        private FanSnapshot _lastSnapshot;

        public MainForm(bool startMinimized = false, bool isAutoStart = false)
        {
            _startMinimizedToTray = startMinimized;
            _isAutoStart = isAutoStart;
            Text = "X15 风扇控制 — 静音稳定控制器";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1080, 720);
            Size = new Size(1250, 820);
            Font = new Font("Segoe UI", 9F);
            BackColor = SystemColors.Control;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
            UpdateStyles();

            _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X15FanControl");
            _configPath = Path.Combine(_dataDirectory, "config.json");
            _heartbeatPath = Path.Combine(_dataDirectory, "active.heartbeat");
            _watchdogLogPath = Path.Combine(_dataDirectory, "watchdog.log");
            Directory.CreateDirectory(_dataDirectory);

            BuildUserInterface();
            BuildTrayIcon();

            _mainTimer = new Timer();
            _mainTimer.Tick += MainTimerTick;

            Load += MainFormLoad;
            Shown += MainForm_Shown;
            FormClosing += MainFormClosing;
            Resize += MainFormResize;
            SystemEvents.PowerModeChanged += SystemEventsPowerModeChanged;

            // Start background log flush task
            _logCts = new System.Threading.CancellationTokenSource();
            _logFlushTask = Task.Run(() => LogFlushLoop(_logCts.Token));
        }

        private void MainFormLoad(object sender, EventArgs e)
        {
            // 配置加载（快速，无阻塞）
            _configStore = new ConfigStore(_configPath);
            _config = _configStore.LoadOrCreate();
            if (!string.IsNullOrEmpty(_configStore.LastLoadDiagnostic))
            {
                AppendLog(_configStore.LastLoadDiagnostic);
            }
            _heartbeat = new Heartbeat(_heartbeatPath);

            PopulateModeCombo();
            PopulateProfiles();

            FanProfile profile = GetActiveProfile();
            _engine = new FanControlEngine(profile);
            LoadProfileIntoEditor(profile);

            _runMode = _config.StartupMode == RunMode.Active ? RunMode.ReadOnly : _config.StartupMode;
            _modeCombo.SelectedItem = _runMode;
            UpdateModeStatus();

            if (_config.EnableCsvLogging)
            {
                _csvLogger = new CsvLogger(Path.Combine(_dataDirectory, "logs"));
                AppendLog("CSV 日志：" + _csvLogger.FilePath);
            }

            AppendLog("界面初始化完成，硬件将在后台加载...");

            // 初始化UI状态标签
            _cpuTempLabel.Text = "等待中";
            _gpuTempLabel.Text = "等待中";
            _hardwareStatusLabel.Text = "硬件：正在初始化...";
            _gpuNvidiaStatusLabel.Text = "遥测：正在启动...";
        }

        private async void MainForm_Shown(object sender, EventArgs e)
        {
            // 如果 --minimized，在窗口显示前隐藏到托盘，避免闪烁
            if (_startMinimizedToTray)
            {
                // 窗口已创建但尚未显示：立即隐藏
                BeginInvoke(new Action(() =>
                {
                    HideToTray();
                    AppendLog("以 --minimized 模式启动，已隐藏到托盘");
                }));
            }

            try
            {
                await InitializeHardwareAsync();
            }
            catch (Exception ex)
            {
                AppendLog("初始化异常：" + ex.Message);
                if (!_startMinimizedToTray)
                {
                    _hardwareStatusLabel.Text = "初始化异常：" + ex.Message;
                    _hardwareStatusLabel.ForeColor = Color.Red;
                }
            }

            // Start background control loop (replaces UI timer control work)
            StartBackgroundControl();

            // Handle auto-Active on --autostart
            if (_isAutoStart && _config.AutoEnterActiveOnStartup)
            {
                TryAutoActive();
            }
        }

        private async System.Threading.Tasks.Task InitializeHardwareAsync()
        {
            // 传感器停滞检测初始化（仅CPU）
            _cpuTempStallCount = 0;
            _lastCpuTemp = -1;

            // 1. EC初始化（异步，最多10秒）
            AppendLog("开始EC初始化...");
            bool ecReady = false;
            try
            {
                ecReady = await System.Threading.Tasks.Task.Run(() =>
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
                        return true;
                    }
                    catch { return false; }
                });

                if (ecReady)
                {
                    int count = await EcGetCountAsync();
                    Invoke(new Action(() =>
                    {
                        _hardwareStatusLabel.Text = "硬件：EC 已初始化；风扇通道数：" + count + "。CPU=1，GPU=2。";
                        _hardwareStatusLabel.ForeColor = Color.DarkGreen;
                    }));
                    AppendLog("EC 初始化成功。通道数：" + count);
                }
                else
                {
                    Invoke(new Action(() =>
                    {
                        _hardwareStatusLabel.Text = "硬件：EC初始化失败";
                        _hardwareStatusLabel.ForeColor = Color.DarkRed;
                    }));
                    AppendLog("EC 初始化失败。");
                }
            }
            catch (Exception ex)
            {
                Invoke(new Action(() =>
                {
                    _hardwareStatusLabel.Text = "硬件初始化异常：" + ex.Message;
                    _hardwareStatusLabel.ForeColor = Color.DarkRed;
                }));
                AppendLog("EC初始化异常：" + ex.Message);
            }

            // 2. GPU遥测启动（异步）
            AppendLog("开始启动GPU遥测...");
            _gpuTelemetry = new GpuTelemetryClient(pollIntervalMs: 1000);
            bool started = _gpuTelemetry.Start();
            if (started)
            {
                Invoke(new Action(() => _gpuNvidiaStatusLabel.Text = "遥测：启动中..."));
                AppendLog("GPU遥测辅助程序已启动，等待首个样本...");
            }
            else
            {
                Invoke(new Action(() =>
                {
                    _gpuNvidiaStatusLabel.Text = "遥测：启动失败";
                    _gpuNvidiaStatusLabel.ForeColor = Color.Red;
                }));
                AppendLog("GPU遥测启动失败");
            }

            // 3. 等待首个GPU遥测样本（最多15秒，不阻塞UI）
            bool telemetryReceived = false;
            for (int i = 0; i < 30 && !telemetryReceived; i++)
            {
                await System.Threading.Tasks.Task.Delay(500);
                _lastGpuTelemetry = _gpuTelemetry?.Latest;
                if (_lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale)
                {
                    telemetryReceived = true;
                    _gpuTelemetryReady = true;
                }
            }

            if (telemetryReceived)
            {
                Invoke(new Action(() =>
                {
                    _gpuNvidiaStatusLabel.Text = "正常";
                    _gpuNvidiaStatusLabel.ForeColor = Color.DarkGreen;
                    _gpuNvidiaSourceLabel.Text = _lastGpuTelemetry.SourceName ?? "nvidia-smi";
                    _gpuNvidiaUtilLabel.Text = _lastGpuTelemetry.UtilizationPercent + "%";
                    _gpuNvidiaPowerLabel.Text = _lastGpuTelemetry.PowerWatts.ToString("F1") + "W";
                    _gpuNvidiaPStateLabel.Text = _lastGpuTelemetry.PState ?? "N/A";
                }));
                AppendLog("GPU遥测首个样本已到达：温度" + _lastGpuTelemetry.TemperatureC + "°C");
            }
            else
            {
                Invoke(new Action(() =>
                {
                    _gpuNvidiaStatusLabel.Text = "不可用";
                    _gpuNvidiaStatusLabel.ForeColor = Color.OrangeRed;
                }));
                AppendLog("GPU遥测15秒内未收到数据，标记为不可用");
            }

            // 4. 启动UI刷新定时器（不再负责硬件控制）
            Invoke(new Action(() =>
            {
                _mainTimer.Interval = Math.Max(250, Math.Min(2000, _config.UiRefreshIntervalMs));
                _mainTimer.Start();
                _lastTickUtc = DateTime.UtcNow;
            }));

            // 5. 自动运行EC通道探测
            await RunEcProbeAsync();

            AppendLog("初始化完成。EC=" + (ecReady ? "就绪" : "不可用") +
                       "，GPU遥测=" + (telemetryReceived ? "就绪" : "不可用"));
        }

        private void BuildTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开主窗口", null, delegate { ShowFromTray(); });

            _trayModeItem = new ToolStripMenuItem("当前模式：只读") { Enabled = false };
            menu.Items.Add(_trayModeItem);

            menu.Items.Add("恢复原厂自动", null, delegate { RestoreAuto("Tray command"); });

            _startupMenuItem = new ToolStripMenuItem("开机自动启动")
            {
                Checked = IsStartupTaskRegistered()
            };
            _startupMenuItem.Click += delegate { ToggleStartupTask(); };
            menu.Items.Add(_startupMenuItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            _notifyIcon = new NotifyIcon
            {
                Text = "X15 风扇控制",
                Icon = SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = menu
            };
            _notifyIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Invalidate(true);
            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            // 校准中不能隐藏到托盘：先停止校准并恢复风扇Auto
            if (_calibrationActive)
            {
                if (!StopCalibration("窗口隐藏"))
                {
                    MessageBox.Show(
                        "恢复自动失败，窗口不会隐藏。请使用“恢复自动”并检查 EC 日志。",
                        "声学校准",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                NotifyCalibrationWindowActionStopped();
            }

            Hide();
            // Hide first: changing ShowInTaskbar while visible recreates the native handle
            // and briefly exposes an unpainted white client area.
            ShowInTaskbar = false;

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                AppendLog("窗口已隐藏到托盘。右键托盘图标可恢复窗口或退出。");
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int WmSysCommand = 0x0112;
            const int ScClose = 0xF060;

            if (message.Msg == WmSysCommand &&
                (message.WParam.ToInt64() & 0xFFF0) == ScClose &&
                !_explicitExitRequested &&
                !_allowFinalClose)
            {
                // Handle the title-bar close command before WinForms begins its close/repaint cycle.
                HideToTray();
                return;
            }

            base.WndProc(ref message);
        }

        private void NotifyCalibrationWindowActionStopped()
        {
            const string message = "声学校准已停止并恢复自动，校准期间不能隐藏或最小化窗口。";
            AppendLog(message);
            MessageBox.Show(message, "声学校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApplication()
        {
            _explicitExitRequested = true;
            Close();
        }

        private sealed class StableValueLabel : Label
        {
            public StableValueLabel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint, true);
                DoubleBuffered = true;
                UseCompatibleTextRendering = false;
            }
        }

        private bool IsStartupTaskRegistered()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"X15FanControl\" /FO CSV /NH")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return p.ExitCode == 0 && !string.IsNullOrEmpty(output);
                }
            }
            catch { return false; }
        }

        private bool RegisterStartupTask()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15FanControl.exe");
                if (!File.Exists(exePath)) return false;

                // 根据 StartMinimizedToTray 决定是否带 --minimized
                string minimizedFlag = _config.StartMinimizedToTray ? " --minimized" : "";
                string taskArgs = string.Format("/Create /TN \"X15FanControl\" /TR \"\\\"{0}\\\" --autostart{1}\" /SC ONLOGON /RL HIGHEST /F",
                    exePath, minimizedFlag);
                var psi = new ProcessStartInfo("schtasks.exe", taskArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private bool UnregisterStartupTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Delete /TN \"X15FanControl\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private void ToggleStartupTask()
        {
            bool currentlyRegistered = IsStartupTaskRegistered();
            bool success;
            if (currentlyRegistered)
            {
                success = UnregisterStartupTask();
                if (success) success = !IsStartupTaskRegistered();
                if (success)
                {
                    AppendLog("开机自启动已关闭");
                    _config.StartWithWindows = false;
                }
            }
            else
            {
                success = RegisterStartupTask();
                if (success) success = IsStartupTaskRegistered();
                if (success)
                {
                    AppendLog("开机自启动已开启");
                    _config.StartWithWindows = true;
                }
            }

            if (success)
            {
                if (_startupMenuItem != null) _startupMenuItem.Checked = !currentlyRegistered;
                SaveConfig();
            }
            else
            {
                AppendLog("修改开机自启动失败，请以管理员权限运行");
                MessageBox.Show("修改开机自启动失败。\r\n请以管理员权限运行本程序后重试。",
                    "开机自启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BuildUserInterface()
        {
            Panel header = BuildHeader();
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildDashboardTab());
            tabs.TabPages.Add(BuildProfilesTab());
            tabs.TabPages.Add(BuildCalibrationTab());
            tabs.TabPages.Add(BuildLogsTab());

            Controls.Add(tabs);
            Controls.Add(header);
        }

        private Panel BuildHeader()
        {
            Panel panel = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(12), BackColor = Color.FromArgb(29, 37, 49) };
            Label title = new Label
            {
                Text = "X15 风扇控制",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 17F),
                Location = new Point(12, 10)
            };
            Label subtitle = new Label
            {
                Text = "COLORFUL X15 AT 23 / Clevo NP50SNE — x86 EC 控制器",
                ForeColor = Color.Gainsboro,
                AutoSize = true,
                Location = new Point(14, 45)
            };

            _profileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Location = new Point(480, 23) };
            _profileCombo.SelectedIndexChanged += ProfileComboSelectedIndexChanged;
            _modeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125, Location = new Point(680, 23) };
            _applyModeButton = new Button { Text = "应用模式", Width = 95, Height = 27, Location = new Point(815, 22), BackColor = Color.FromArgb(60, 120, 200), ForeColor = Color.White };
            _applyModeButton.Click += ApplyModeButtonClick;
            _restoreAutoButton = new Button { Text = "恢复自动", Width = 105, Height = 27, Location = new Point(920, 22), BackColor = Color.LightGoldenrodYellow };
            _restoreAutoButton.Click += delegate { RestoreAuto("Manual Restore Auto"); };
            Button ecProbeButton = new Button { Text = "EC诊断", Width = 65, Height = 27, Location = new Point(1035, 22), BackColor = Color.LightCyan };
            ecProbeButton.Click += delegate { RunEcProbe(); };
            _modeStatusPanel = new Panel
            {
                Width = 60, Height = 26, Location = new Point(1110, 24),
                BackColor = Color.FromArgb(0, 100, 0)
            };
            _modeStatusLabel = new Label
            {
                Text = "只读",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = false, Width = 60, Height = 26,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(0, 0)
            };
            _modeStatusPanel.Controls.Add(_modeStatusLabel);

            panel.Controls.Add(title);
            panel.Controls.Add(subtitle);
            panel.Controls.Add(new Label { Text = "配置", ForeColor = Color.White, AutoSize = true, Location = new Point(430, 27) });
            panel.Controls.Add(_profileCombo);
            panel.Controls.Add(_modeCombo);
            panel.Controls.Add(_applyModeButton);
            panel.Controls.Add(_restoreAutoButton);
            panel.Controls.Add(ecProbeButton);
            panel.Controls.Add(_modeStatusPanel);
            return panel;
        }

        private TabPage BuildDashboardTab()
        {
            TabPage tab = new TabPage("仪表盘");
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cards.Controls.Add(BuildFanCard("CPU", out _cpuTempLabel, out _cpuFilteredLabel, out _cpuDutyLabel, out _cpuTargetLabel, out _cpuRpmLabel, out _cpuCardBox), 0, 0);
            cards.Controls.Add(BuildFanCard("GPU", out _gpuTempLabel, out _gpuFilteredLabel, out _gpuDutyLabel, out _gpuTargetLabel, out _gpuRpmLabel, out _gpuCardBox), 1, 0);

            _historyChart = BuildHistoryChart();

            // 底部状态栏：硬件状态 + GPU遥测详情
            TableLayoutPanel statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _hardwareStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "硬件：初始化中…" };

            FlowLayoutPanel telemetryPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            telemetryPanel.Controls.Add(new Label { Text = "GPU遥测: ", AutoSize = true, ForeColor = Color.DimGray });
            _gpuNvidiaStatusLabel = new Label { Text = "等待中…", AutoSize = true, ForeColor = Color.Gray };
            telemetryPanel.Controls.Add(_gpuNvidiaStatusLabel);
            telemetryPanel.Controls.Add(new Label { Text = " | 来源: ", AutoSize = true, ForeColor = Color.DimGray });
            _gpuNvidiaSourceLabel = new Label { Text = "—", AutoSize = true };
            telemetryPanel.Controls.Add(_gpuNvidiaSourceLabel);
            telemetryPanel.Controls.Add(new Label { Text = " | 利用率: ", AutoSize = true, ForeColor = Color.DimGray });
            _gpuNvidiaUtilLabel = new Label { Text = "—", AutoSize = true };
            telemetryPanel.Controls.Add(_gpuNvidiaUtilLabel);
            telemetryPanel.Controls.Add(new Label { Text = " | 功耗: ", AutoSize = true, ForeColor = Color.DimGray });
            _gpuNvidiaPowerLabel = new Label { Text = "—", AutoSize = true };
            telemetryPanel.Controls.Add(_gpuNvidiaPowerLabel);
            telemetryPanel.Controls.Add(new Label { Text = " | P-State: ", AutoSize = true, ForeColor = Color.DimGray });
            _gpuNvidiaPStateLabel = new Label { Text = "—", AutoSize = true };

            statusPanel.Controls.Add(_hardwareStatusLabel, 0, 0);
            statusPanel.Controls.Add(telemetryPanel, 0, 1);

            root.Controls.Add(cards, 0, 0);
            root.Controls.Add(_historyChart, 0, 1);
            root.Controls.Add(statusPanel, 0, 2);
            tab.Controls.Add(root);
            return tab;
        }

        private GroupBox BuildFanCard(string title, out Label temperature, out Label filtered, out Label duty, out Label target, out Label rpm, out GroupBox boxRef)
        {
            GroupBox box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(12) };
            boxRef = box;
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2 };
            for (int i = 0; i < 5; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            string[] headings = { "实时温度", "平滑温度", "当前输出", "目标输出", "风扇转速" };
            Label[] values = new Label[5];
            for (int i = 0; i < headings.Length; i++)
            {
                grid.Controls.Add(new Label { Text = headings[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray }, i, 0);
                values[i] = new StableValueLabel
                {
                    Text = "—",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Consolas", 17F, FontStyle.Bold)
                };
                grid.Controls.Add(values[i], i, 1);
            }

            temperature = values[0];
            filtered = values[1];
            duty = values[2];
            target = values[3];
            rpm = values[4];
            box.Controls.Add(grid);
            return box;
        }

        private Chart BuildHistoryChart()
        {
            Chart chart = new Chart { Dock = DockStyle.Fill };
            ChartArea area = new ChartArea("History");
            area.AxisX.LabelStyle.Enabled = false;
            area.AxisY.Minimum = 0;
            area.AxisY.Maximum = 105;
            area.AxisY.Title = "°C / %";
            area.AxisX.MajorGrid.Enabled = false;
            chart.ChartAreas.Add(area);
            chart.Legends.Add(new Legend("Legend") { Docking = Docking.Top });
            AddSeries(chart, "CPU 温度", SeriesChartType.FastLine);
            AddSeries(chart, "GPU 温度", SeriesChartType.FastLine);
            AddSeries(chart, "CPU 设定", SeriesChartType.StepLine);
            AddSeries(chart, "GPU 设定", SeriesChartType.StepLine);
            return chart;
        }

        private static void AddSeries(Chart chart, string name, SeriesChartType chartType)
        {
            Series series = new Series(name) { ChartType = chartType, BorderWidth = 2, ChartArea = "History" };
            chart.Series.Add(series);
        }

        private TabPage BuildLogsTab()
        {
            TabPage tab = new TabPage("日志");
            _logTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9F),
                WordWrap = false
            };
            tab.Controls.Add(_logTextBox);
            return tab;
        }
    }
}
