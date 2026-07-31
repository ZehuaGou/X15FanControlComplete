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
        private static readonly Color UiBackground = Color.FromArgb(242, 245, 249);
        private static readonly Color UiSurface = Color.White;
        private static readonly Color UiBorder = Color.FromArgb(218, 224, 233);
        private static readonly Color UiText = Color.FromArgb(37, 47, 61);
        private static readonly Color UiMuted = Color.FromArgb(101, 112, 130);
        private static readonly Color UiCpuAccent = Color.FromArgb(42, 112, 232);
        private static readonly Color UiGpuAccent = Color.FromArgb(124, 82, 214);
        private static readonly Color UiWarmAccent = Color.FromArgb(239, 126, 56);

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

        private EcAccessQueue _ecQueue;
        // CPU and GPU verification share the same physical EC access path.
        private readonly System.Threading.SemaphoreSlim _verificationEcGate = new System.Threading.SemaphoreSlim(1, 1);
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
        private System.Threading.Timer _heartbeatMonitorTimer;
        private Process _watchdogProcess;
        private Process _controlCenterLeaseWatchdogProcess;
        private ControlCenterLease _controlCenterLease;
        private RunMode _runMode;
        private NotifyIcon _notifyIcon;
        private DateTime _lastTickUtc;
        private long _lastControlProgressUtcTicks;
        // Updated after each completed native EC operation.  A full control
        // cycle can legitimately span several EC calls on this notebook.
        private long _lastEcActivityUtcTicks;
        private int _lastCpuRpm;
        private int _lastGpuRpm;
        private int _cpuZeroDutyReadCount;
        private int _gpuZeroDutyReadCount;
        private int _ecFaulted;
        private int _watchdogFailureHandling;
        private bool _closing;
        private bool _allowFinalClose;

        // Window behavior
        private bool _explicitExitRequested;
        private bool _trayHintShown;
        private bool _startMinimizedToTray;
        private bool _isAutoStart;
        private ToolStripMenuItem _trayModeItem;
        private ToolStripMenuItem _trayStrategyItem;
        private ToolStripMenuItem _trayTierItem;
        private readonly Dictionary<StrategyMode, ToolStripMenuItem> _trayStrategyItems =
            new Dictionary<StrategyMode, ToolStripMenuItem>();
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
        private PerformanceCounter _cpuUtilizationCounter;
        private PerformanceCounter _cpuPerformanceCounter;
        private AdaptivePowerTierController _adaptivePowerTierController;
        private AdaptivePowerTier _adaptiveAppliedTier = (AdaptivePowerTier)(-1);
        private AdaptivePowerTier _adaptiveCurrentTier = AdaptivePowerTier.Daily;
        private bool _adaptiveXtuConfirmed;
        private bool _adaptivePowerApplying;
        private bool _adaptivePolicyCaptured;
        private string _adaptiveLastReason = "等待硬件初始化";
        private string _adaptiveBackendName = "未应用";
        private AdaptivePowerTier _adaptiveFanAppliedTier = (AdaptivePowerTier)(-1);
        private WindowsProcessorPolicy _adaptiveProcessorPolicy;
        private WindowsProcessorPolicySnapshot _adaptivePolicySnapshot;
        private DateTime _adaptiveNextApplyUtc = DateTime.MinValue;
        private System.Threading.CancellationTokenSource _adaptiveApplyCts;
        private Task _adaptiveApplyTask;
        private Label _gpuNvidiaUtilLabel;
        private Label _gpuNvidiaPowerLabel;
        private Label _gpuNvidiaPStateLabel;
        private Label _gpuNvidiaSourceLabel;
        private Label _gpuNvidiaStatusLabel;
        private Label _strategyModeValueLabel;
        private Label _strategyTierValueLabel;
        private Label _strategyPowerValueLabel;
        private Label _strategyReasonValueLabel;
        private Label _strategyBackendValueLabel;
        private Label _strategyCpuValueLabel;
        private Label _strategyGpuValueLabel;

        // Write verification (async via Task.Delay)
                private ComboBox _modeCombo;
        private ComboBox _profileCombo;
        private Button _applyModeButton;
        private Button _restoreAutoButton;
        private Label _hardwareStatusLabel;
        private FlowLayoutPanel _hardwareStatusFlow;
        private Label _cpuEcRemoteStatusValueLabel;
        private Label _cpuEcLocalStatusValueLabel;
        private Label _gpuEcRemoteStatusValueLabel;
        private Label _gpuEcLocalStatusValueLabel;
        private Label _cpuRpmStatusValueLabel;
        private Label _gpuRpmStatusValueLabel;
        private Label _temperatureRiseStatusValueLabel;
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
        private Chart _cpuHistoryChart;
        private Chart _gpuHistoryChart;
        private TextBox _logTextBox;
        private TabControl _mainTabs;

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

        public MainForm(bool startMinimized = false, bool isAutoStart = false, bool uiPreview = false)
        {
            _startMinimizedToTray = startMinimized;
            _isAutoStart = isAutoStart;
            Text = "X15 风扇控制 — 静音稳定控制器";
            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                Icon = SystemIcons.Application;
            }
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

            if (!uiPreview)
            {
                Load += MainFormLoad;
                Shown += MainForm_Shown;
                FormClosing += MainFormClosing;
                Resize += MainFormResize;
                SystemEvents.PowerModeChanged += SystemEventsPowerModeChanged;

                // Start background log flush task
                _logCts = new System.Threading.CancellationTokenSource();
                _logFlushTask = Task.Run(() => LogFlushLoop(_logCts.Token));
            }
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
            _controlCenterLease = new ControlCenterLease(Path.Combine(_dataDirectory, "controlcenter.lease.json"));
            string controlCenterDiagnostic;
            if (_controlCenterLease.Acquire(out controlCenterDiagnostic))
            {
                AppendLog(controlCenterDiagnostic);
                StartControlCenterLeaseWatchdog();
            }
            else
            {
                AppendLog(controlCenterDiagnostic);
                _controlCenterLease = null;
            }
            _adaptivePowerTierController = new AdaptivePowerTierController();
            InitializeAdaptivePowerCounters();

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

            // Active must wait until EC and telemetry initialization has completed.
            // StartupMode is the current preference; the second condition preserves
            // compatibility with configurations created by older builds.
            if (_config.StartupMode == RunMode.Active ||
                (_isAutoStart && _config.AutoEnterActiveOnStartup))
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
                        System.Threading.Interlocked.Exchange(ref _ecFaulted, 0);
                        string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
                        _ecQueue = new EcAccessQueue(dllPath);
                        _ecQueue.Ready.Wait(10000);
                        return _ecQueue.IsReady;
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
                    DisposeEc();
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
                DisposeEc();
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

            await ProbeIntelXtuBridgeAsync();

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

        private async Task ProbeIntelXtuBridgeAsync()
        {
            string bridgePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15XtuBridge.exe");
            if (!File.Exists(bridgePath))
            {
                AppendLog("Intel XTU 桥接程序未部署，跳过功耗通道探测。");
                return;
            }

            Process bridge = null;
            try
            {
                bridge = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = bridgePath,
                        WorkingDirectory = Path.GetDirectoryName(bridgePath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };
                if (!bridge.Start())
                {
                    AppendLog("Intel XTU 桥接程序启动失败。");
                    return;
                }

                Task<string> outputTask = bridge.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = bridge.StandardError.ReadToEndAsync();
                Task waitTask = Task.Run(() => bridge.WaitForExit());
                if (await Task.WhenAny(waitTask, Task.Delay(15000)) != waitTask)
                {
                    try { bridge.Kill(); } catch { }
                    AppendLog("Intel XTU 桥接程序超时，已终止；未执行任何功耗写入。");
                    return;
                }

                string output = await outputTask;
                string error = await errorTask;
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    AppendLog("Intel XTU 桥接：" + line);
                }
                foreach (string line in error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    AppendLog("Intel XTU 桥接错误：" + line);
                }
                AppendLog("Intel XTU 桥接退出码：" + bridge.ExitCode + "（当前仅探测，不写入功耗）");
            }
            catch (Exception exception)
            {
                AppendLog("Intel XTU 桥接探测跳过：" + exception.Message);
            }
            finally
            {
                if (bridge != null)
                {
                    bridge.Dispose();
                }
            }
        }

        private void InitializeAdaptivePowerCounters()
        {
            try
            {
                _cpuUtilizationCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuUtilizationCounter.NextValue();
            }
            catch (Exception exception)
            {
                _cpuUtilizationCounter = null;
                AppendLog("自适应功耗：CPU利用率计数器不可用，将使用温度/EC活动作为保守输入。" + exception.Message);
            }

            try
            {
                _cpuPerformanceCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                _cpuPerformanceCounter.NextValue();
            }
            catch
            {
                _cpuPerformanceCounter = null;
            }
        }

        private void BuildTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开主窗口", null, delegate { ShowFromTray(); });

            _trayModeItem = new ToolStripMenuItem("当前模式：只读") { Enabled = false };
            menu.Items.Add(_trayModeItem);

            _trayStrategyItem = new ToolStripMenuItem("当前策略：自动策略") { Enabled = false };
            _trayTierItem = new ToolStripMenuItem("当前档位：2档 · 日常") { Enabled = false };
            menu.Items.Add(_trayStrategyItem);
            menu.Items.Add(_trayTierItem);

            menu.Items.Add(new ToolStripSeparator());
            _trayStrategyItems.Clear();
            AddTrayStrategyItem(menu, StrategyMode.Auto, "切换到自动策略");
            AddTrayStrategyItem(menu, StrategyMode.Quiet, "切换到1档 · 安静");
            AddTrayStrategyItem(menu, StrategyMode.Daily, "切换到2档 · 日常");
            AddTrayStrategyItem(menu, StrategyMode.Code, "切换到3档 · 代码");
            AddTrayStrategyItem(menu, StrategyMode.Heavy, "切换到4档 · 重负载");

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
                Icon = Icon ?? SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = menu
            };
            _notifyIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private void AddTrayStrategyItem(ContextMenuStrip menu, StrategyMode mode, string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { SelectStrategyFromTray(mode); };
            _trayStrategyItems[mode] = item;
            menu.Items.Add(item);
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
            _mainTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(118, 34),
                Padding = new Point(16, 6),
                Font = new Font("Segoe UI Semibold", 9.5F)
            };
            _mainTabs.DrawItem += MainTabsDrawItem;
            _mainTabs.TabPages.Add(BuildDashboardTab());
            _mainTabs.TabPages.Add(BuildProfilesTab());
            _mainTabs.TabPages.Add(BuildCalibrationTab());
            _mainTabs.TabPages.Add(BuildLogsTab());

            Controls.Add(_mainTabs);
            Controls.Add(header);
        }

        private Panel BuildHeader()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 82,
                Padding = new Padding(16, 10, 14, 10),
                BackColor = Color.FromArgb(27, 36, 49)
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = panel.BackColor
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));

            Panel brand = new Panel { Dock = DockStyle.Fill, BackColor = panel.BackColor };
            Label title = new Label
            {
                Text = "X15 风扇控制",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 17F),
                Location = new Point(0, 0)
            };
            Label subtitle = new Label
            {
                Text = "COLORFUL X15 AT 23 / Clevo NP50SNE — x86 EC 控制器",
                ForeColor = Color.Gainsboro,
                AutoSize = true,
                Location = new Point(2, 37)
            };
            brand.Controls.Add(title);
            brand.Controls.Add(subtitle);

            _profileCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 170,
                Height = 30,
                Margin = new Padding(6, 5, 6, 0)
            };
            _profileCombo.SelectedIndexChanged += ProfileComboSelectedIndexChanged;
            _modeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 112,
                Height = 30,
                Margin = new Padding(0, 5, 6, 0)
            };
            _applyModeButton = new Button { Text = "应用模式", Width = 88, Height = 31 };
            StyleButton(_applyModeButton, UiCpuAccent, Color.White);
            _applyModeButton.Click += ApplyModeButtonClick;
            _restoreAutoButton = new Button { Text = "恢复自动", Width = 92, Height = 31 };
            StyleButton(_restoreAutoButton, Color.FromArgb(255, 244, 204), Color.FromArgb(102, 76, 0));
            _restoreAutoButton.Click += delegate { RestoreAuto("Manual Restore Auto"); };
            Button ecProbeButton = new Button { Text = "EC诊断", Width = 68, Height = 31 };
            StyleButton(ecProbeButton, Color.FromArgb(225, 247, 250), Color.FromArgb(0, 91, 104));
            ecProbeButton.Click += delegate { RunEcProbe(); };
            Button strategyStatusButton = new Button { Text = "策略状态", Width = 78, Height = 31 };
            StyleButton(strategyStatusButton, Color.FromArgb(255, 235, 205), Color.FromArgb(115, 70, 0));
            strategyStatusButton.Click += delegate { if (_mainTabs != null) _mainTabs.SelectedIndex = 0; };
            _modeStatusPanel = new Panel
            {
                Width = 58,
                Height = 30,
                Margin = new Padding(6, 5, 0, 0),
                BackColor = Color.FromArgb(0, 100, 0)
            };
            _modeStatusLabel = new Label
            {
                Text = "只读",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _modeStatusPanel.Controls.Add(_modeStatusLabel);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 12, 0, 0),
                BackColor = panel.BackColor
            };
            actions.Controls.Add(new Label
            {
                Text = "配置",
                ForeColor = Color.Gainsboro,
                AutoSize = true,
                Margin = new Padding(0, 11, 2, 0)
            });
            actions.Controls.Add(_profileCombo);
            actions.Controls.Add(_modeCombo);
            actions.Controls.Add(_applyModeButton);
            actions.Controls.Add(_restoreAutoButton);
            actions.Controls.Add(ecProbeButton);
            actions.Controls.Add(strategyStatusButton);
            actions.Controls.Add(_modeStatusPanel);

            layout.Controls.Add(brand, 0, 0);
            layout.Controls.Add(actions, 1, 0);
            panel.Controls.Add(layout);
            return panel;
        }

        private void MainTabsDrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabs = sender as TabControl;
            if (tabs == null || e.Index < 0 || e.Index >= tabs.TabPages.Count)
                return;

            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle bounds = e.Bounds;
            using (SolidBrush background = new SolidBrush(selected ? UiSurface : UiBackground))
            using (SolidBrush textBrush = new SolidBrush(selected ? UiCpuAccent : UiMuted))
            {
                e.Graphics.FillRectangle(background, bounds);
                TextRenderer.DrawText(
                    e.Graphics,
                    tabs.TabPages[e.Index].Text,
                    tabs.Font,
                    bounds,
                    textBrush.Color,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine);
            }
            if (selected)
            {
                using (SolidBrush accent = new SolidBrush(UiCpuAccent))
                {
                    e.Graphics.FillRectangle(accent, bounds.Left + 10, bounds.Bottom - 3, bounds.Width - 20, 3);
                }
            }
        }

        private static void StyleButton(Button button, Color backColor, Color foreColor)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.Font = new Font("Segoe UI Semibold", 9F);
            button.Margin = new Padding(4, 5, 4, 0);
            button.Cursor = Cursors.Hand;
        }

        private TabPage BuildDashboardTab()
        {
            TabPage tab = new TabPage("仪表盘") { BackColor = UiBackground };
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = UiBackground
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            TableLayoutPanel cards = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiBackground
            };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            GroupBox cpuCard = BuildFanDashboardCard(
                "CPU", UiCpuAccent,
                out _cpuTempLabel, out _cpuFilteredLabel, out _cpuDutyLabel,
                out _cpuTargetLabel, out _cpuRpmLabel, out _cpuCardBox,
                out _cpuHistoryChart);
            cpuCard.Margin = new Padding(0, 0, 6, 0);

            GroupBox gpuCard = BuildFanDashboardCard(
                "GPU", UiGpuAccent,
                out _gpuTempLabel, out _gpuFilteredLabel, out _gpuDutyLabel,
                out _gpuTargetLabel, out _gpuRpmLabel, out _gpuCardBox,
                out _gpuHistoryChart);
            gpuCard.Margin = new Padding(6, 0, 0, 0);

            cards.Controls.Add(cpuCard, 0, 0);
            cards.Controls.Add(gpuCard, 1, 0);

            // 底部状态栏：硬件状态 + GPU遥测详情
            TableLayoutPanel statusPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(10, 2, 10, 2),
                Margin = new Padding(0, 6, 0, 0),
                BackColor = UiSurface
            };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _hardwareStatusFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = UiSurface
            };
            _hardwareStatusLabel = AddHardwareStatusText(_hardwareStatusFlow, "EC CPU ch1: R=");
            _cpuEcRemoteStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 34);
            AddHardwareStatusText(_hardwareStatusFlow, "°C L=");
            _cpuEcLocalStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 34);
            AddHardwareStatusText(_hardwareStatusFlow, "°C | EC GPU ch2: R=");
            _gpuEcRemoteStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 34);
            AddHardwareStatusText(_hardwareStatusFlow, "°C L=");
            _gpuEcLocalStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 34);
            AddHardwareStatusText(_hardwareStatusFlow, "°C | CPU转速=");
            _cpuRpmStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 48);
            AddHardwareStatusText(_hardwareStatusFlow, " GPU转速=");
            _gpuRpmStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 48);
            AddHardwareStatusText(_hardwareStatusFlow, " | CPU温升速率: ");
            _temperatureRiseStatusValueLabel = AddHardwareStatusValue(_hardwareStatusFlow, 66);
            AddHardwareStatusText(_hardwareStatusFlow, "°C/s");

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
            telemetryPanel.Controls.Add(_gpuNvidiaPStateLabel);

            statusPanel.Controls.Add(_hardwareStatusFlow, 0, 0);
            statusPanel.Controls.Add(telemetryPanel, 0, 1);

            root.Controls.Add(cards, 0, 0);
            root.Controls.Add(BuildStrategyStatusPanel(), 0, 1);
            root.Controls.Add(statusPanel, 0, 2);
            tab.Controls.Add(root);
            return tab;
        }

        private Panel BuildStrategyStatusPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UiSurface,
                Padding = new Padding(12, 4, 12, 4),
                Margin = new Padding(0, 6, 0, 0)
            };
            FlowLayoutPanel primary = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 24,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = UiSurface,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            primary.Controls.Add(CreateStrategyFixedLabel("策略：", 44, true));
            _strategyModeValueLabel = CreateStrategyValueLabel(92, true);
            primary.Controls.Add(_strategyModeValueLabel);
            primary.Controls.Add(CreateStrategyFixedLabel("当前档位：", 70, true));
            _strategyTierValueLabel = CreateStrategyValueLabel(105, true);
            primary.Controls.Add(_strategyTierValueLabel);
            primary.Controls.Add(CreateStrategyFixedLabel("功耗：", 44, true));
            _strategyPowerValueLabel = CreateStrategyValueLabel(260, true);
            primary.Controls.Add(_strategyPowerValueLabel);

            FlowLayoutPanel secondary = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = UiSurface,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            secondary.Controls.Add(CreateStrategyFixedLabel("原因：", 44, false));
            _strategyReasonValueLabel = CreateStrategyValueLabel(315, false);
            secondary.Controls.Add(_strategyReasonValueLabel);
            secondary.Controls.Add(CreateStrategyFixedLabel("后端：", 44, false));
            _strategyBackendValueLabel = CreateStrategyValueLabel(155, false);
            secondary.Controls.Add(_strategyBackendValueLabel);
            secondary.Controls.Add(CreateStrategyFixedLabel("CPU：", 38, false));
            _strategyCpuValueLabel = CreateStrategyValueLabel(48, false);
            secondary.Controls.Add(_strategyCpuValueLabel);
            secondary.Controls.Add(CreateStrategyFixedLabel("GPU：", 38, false));
            _strategyGpuValueLabel = CreateStrategyValueLabel(48, false);
            secondary.Controls.Add(_strategyGpuValueLabel);

            panel.Controls.Add(secondary);
            panel.Controls.Add(primary);
            return panel;
        }

        private static Label CreateStrategyFixedLabel(string text, int width, bool prominent)
        {
            return new Label
            {
                Text = text,
                Width = width,
                Height = 22,
                AutoSize = false,
                ForeColor = prominent ? UiText : UiMuted,
                Font = new Font("Segoe UI", prominent ? 9.5F : 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
        }

        private static Label CreateStrategyValueLabel(int width, bool prominent)
        {
            return new Label
            {
                Width = width,
                Height = 22,
                AutoSize = false,
                ForeColor = prominent ? UiCpuAccent : UiMuted,
                Font = new Font("Segoe UI Semibold", prominent ? 9.5F : 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Text = "—",
                AutoEllipsis = true
            };
        }

        private static Label AddHardwareStatusText(FlowLayoutPanel panel, string text)
        {
            Label label = new Label
            {
                AutoSize = true,
                Text = text,
                ForeColor = Color.DarkGreen,
                Margin = new Padding(0),
                Padding = new Padding(0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(label);
            return label;
        }

        private static Label AddHardwareStatusValue(FlowLayoutPanel panel, int width)
        {
            Label label = new Label
            {
                AutoSize = false,
                Width = width,
                Height = 22,
                Text = "—",
                ForeColor = Color.DarkGreen,
                Margin = new Padding(0),
                Padding = new Padding(0),
                TextAlign = ContentAlignment.MiddleLeft
            };
            panel.Controls.Add(label);
            return label;
        }

        private GroupBox BuildFanDashboardCard(
            string title,
            Color accentColor,
            out Label temperature,
            out Label filtered,
            out Label duty,
            out Label target,
            out Label rpm,
            out GroupBox boxRef,
            out Chart historyChart)
        {
            GroupBox box = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 10, 12, 12),
                BackColor = UiSurface,
                Font = new Font("Segoe UI Semibold", 9.5F)
            };
            boxRef = box;
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = UiSurface
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            TableLayoutPanel metrics = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = UiSurface
            };
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            TableLayoutPanel primaryMetrics = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            primaryMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            primaryMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            primaryMetrics.Controls.Add(
                BuildMetricTile("实时温度", "°C", accentColor, true, out temperature), 0, 0);
            primaryMetrics.Controls.Add(
                BuildMetricTile("风扇转速", "RPM", accentColor, true, out rpm), 1, 0);

            TableLayoutPanel secondaryMetrics = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1
            };
            secondaryMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            secondaryMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            secondaryMetrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            Color secondaryValueColor = UiText;
            secondaryMetrics.Controls.Add(
                BuildMetricTile("平滑温度", "°C", secondaryValueColor, false, out filtered), 0, 0);
            secondaryMetrics.Controls.Add(
                BuildMetricTile("当前输出", "%", secondaryValueColor, false, out duty), 1, 0);
            secondaryMetrics.Controls.Add(
                BuildMetricTile("目标输出", "%", secondaryValueColor, false, out target), 2, 0);

            metrics.Controls.Add(primaryMetrics, 0, 0);
            metrics.Controls.Add(secondaryMetrics, 0, 1);

            historyChart = BuildHistoryChart(
                title + " 趋势",
                title + " 温度",
                title + " 设定",
                accentColor);
            layout.Controls.Add(metrics, 0, 0);
            layout.Controls.Add(historyChart, 0, 1);

            Panel accentBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 4,
                BackColor = accentColor
            };
            box.Controls.Add(layout);
            box.Controls.Add(accentBar);
            return box;
        }

        private Control BuildMetricTile(
            string caption,
            string unit,
            Color valueColor,
            bool prominent,
            out Label valueLabel)
        {
            Color tileColor = prominent
                ? Color.FromArgb(247, 250, 255)
                : Color.FromArgb(247, 248, 250);
            Panel tile = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(4),
                Padding = new Padding(8, 5, 8, 5),
                BackColor = tileColor
            };
            TableLayoutPanel content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = tileColor
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, prominent ? 24 : 20));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(102, 112, 128),
                Font = new Font("Segoe UI", prominent ? 9.5F : 8.5F)
            }, 0, 0);

            TableLayoutPanel valueRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 1,
                BackColor = tileColor
            };
            valueRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            FlowLayoutPanel valueGroup = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.None,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = tileColor
            };

            valueLabel = new StableValueLabel
            {
                Text = "—",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = Padding.Empty,
                ForeColor = valueColor,
                BackColor = tileColor,
                Font = new Font("Segoe UI Semibold", prominent ? 22F : 15.5F)
            };
            Label unitLabel = new Label
            {
                Text = unit,
                AutoSize = true,
                Anchor = AnchorStyles.None,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, prominent ? 8 : 4, 0, 0),
                Margin = Padding.Empty,
                ForeColor = valueColor,
                BackColor = tileColor,
                Font = new Font("Segoe UI Semibold", prominent ? 10F : 9F)
            };
            valueGroup.Controls.Add(valueLabel);
            valueGroup.Controls.Add(unitLabel);
            valueRow.Controls.Add(valueGroup, 0, 0);
            content.Controls.Add(valueRow, 0, 1);
            tile.Controls.Add(content);
            return tile;
        }

        private Chart BuildHistoryChart(
            string title,
            string temperatureSeriesName,
            string targetSeriesName,
            Color accentColor)
        {
            Chart chart = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Margin = new Padding(4, 8, 4, 0)
            };
            ChartArea area = new ChartArea("History");
            area.BackColor = Color.White;
            area.AxisX.LabelStyle.Enabled = false;
            area.AxisY.Minimum = 0;
            area.AxisY.Maximum = 105;
            area.AxisY.Interval = 20;
            area.AxisY.Title = "°C / %";
            area.AxisX.MajorGrid.Enabled = false;
            area.AxisX.LineColor = Color.FromArgb(210, 216, 224);
            area.AxisY.LineColor = Color.FromArgb(210, 216, 224);
            area.AxisY.MajorGrid.LineColor = Color.FromArgb(229, 233, 239);
            area.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dot;
            area.AxisY.LabelStyle.ForeColor = Color.FromArgb(100, 110, 124);
            area.AxisY.TitleForeColor = Color.FromArgb(100, 110, 124);
            chart.ChartAreas.Add(area);
            chart.Legends.Add(new Legend("Legend")
            {
                Docking = Docking.Top,
                Alignment = StringAlignment.Far,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5F)
            });
            chart.Titles.Add(new Title(title)
            {
                Alignment = ContentAlignment.TopLeft,
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = Color.FromArgb(55, 65, 81)
            });
            AddSeries(chart, temperatureSeriesName, "温度", SeriesChartType.FastLine, accentColor, ChartDashStyle.Solid);
            AddSeries(chart, targetSeriesName, "目标输出", SeriesChartType.StepLine,
                UiWarmAccent, ChartDashStyle.Dash);
            return chart;
        }

        private static void AddSeries(
            Chart chart,
            string name,
            string legendText,
            SeriesChartType chartType,
            Color color,
            ChartDashStyle dashStyle)
        {
            Series series = new Series(name)
            {
                LegendText = legendText,
                ChartType = chartType,
                BorderWidth = 2,
                BorderDashStyle = dashStyle,
                Color = color,
                ChartArea = "History"
            };
            chart.Series.Add(series);
        }

        private TabPage BuildLogsTab()
        {
            TabPage tab = new TabPage("日志") { BackColor = UiBackground };
            Panel root = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                BackColor = UiBackground
            };
            Panel card = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                BackColor = UiSurface
            };
            Panel header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                BackColor = UiSurface
            };
            header.Controls.Add(new Label
            {
                Text = "运行日志",
                Dock = DockStyle.Left,
                Width = 160,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                ForeColor = UiText,
                Font = new Font("Segoe UI Semibold", 11F)
            });
            header.Controls.Add(new Label
            {
                Text = "实时追加 · 只读",
                Dock = DockStyle.Right,
                Width = 150,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 8, 0),
                ForeColor = UiMuted
            });
            header.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 4, BackColor = UiCpuAccent });
            _logTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5F),
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(24, 31, 42),
                ForeColor = Color.FromArgb(220, 226, 235)
            };
            card.Controls.Add(_logTextBox);
            card.Controls.Add(header);
            root.Controls.Add(card);
            tab.Controls.Add(root);
            return tab;
        }
    }
}
