using System;
using System.Drawing;
using System.Windows.Forms;
using X15FanCore.Control;
using X15FanCore.Models;

namespace X15FanControl
{
    public partial class MainForm
    {
        private sealed class StrategyOption
        {
            public StrategyMode Mode { get; set; }
            public string Name { get { return StrategyModeInfo.GetName(Mode); } }
            public override string ToString() { return Name; }
        }

        private TabPage BuildProfilesTab()
        {
            TabPage tab = new TabPage("策略说明") { BackColor = UiBackground };
            Panel root = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                BackColor = UiBackground
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 510,
                ColumnCount = 1,
                RowCount = 7,
                BackColor = UiSurface,
                Padding = new Padding(18)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label title = new Label
            {
                Text = "固定策略（不可手工修改）",
                Dock = DockStyle.Fill,
                ForeColor = UiText,
                Font = new Font("Segoe UI Semibold", 13F),
                TextAlign = ContentAlignment.MiddleLeft
            };
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "顶部下拉框只负责选择策略。功耗、CPU性能上限、风扇曲线和自动升降档时间均由程序内置并经过边界限制，页面不提供数值编辑入口。",
                Dock = DockStyle.Fill,
                ForeColor = UiMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            }, 0, 1);

            layout.Controls.Add(BuildStrategyRow("自动策略", "从当前实际档位开始；升档需持续10–15秒，降档需持续45–90秒，并按相邻档逐级切换。"), 0, 2);
            layout.Controls.Add(BuildStrategyRow("1档 · 安静", "25W / 35W / 28秒，CPU性能上限75%，风扇曲线最安静；高温安全爬升不受影响。"), 0, 3);
            layout.Controls.Add(BuildStrategyRow("2档 · 日常", "30W / 45W / 28秒，CPU性能上限85%，日常阅读、办公和轻量代码使用。"), 0, 4);
            layout.Controls.Add(BuildStrategyRow("3档 · 代码", "38W / 55W / 28秒，CPU性能上限95%，适合编译和持续代码任务。"), 0, 5);
            layout.Controls.Add(BuildStrategyRow("4档 · 重负载", "55W / 69W / 28秒，CPU性能上限100%，适合持续高负载任务。"), 0, 6);

            root.Controls.Add(layout);
            tab.Controls.Add(root);
            return tab;
        }

        private static Control BuildStrategyRow(string name, string description)
        {
            Panel row = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 252), Padding = new Padding(12, 6, 12, 6) };
            Label nameLabel = new Label
            {
                Text = name,
                Dock = DockStyle.Left,
                Width = 150,
                ForeColor = UiCpuAccent,
                Font = new Font("Segoe UI Semibold", 10.5F),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Label detail = new Label
            {
                Text = description,
                Dock = DockStyle.Fill,
                ForeColor = UiText,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            row.Controls.Add(detail);
            row.Controls.Add(nameLabel);
            return row;
        }

        private void PopulateProfiles()
        {
            _profileCombo.SelectedIndexChanged -= ProfileComboSelectedIndexChanged;
            _profileCombo.Items.Clear();
            _profileCombo.Items.Add(new StrategyOption { Mode = StrategyMode.Auto });
            _profileCombo.Items.Add(new StrategyOption { Mode = StrategyMode.Quiet });
            _profileCombo.Items.Add(new StrategyOption { Mode = StrategyMode.Daily });
            _profileCombo.Items.Add(new StrategyOption { Mode = StrategyMode.Code });
            _profileCombo.Items.Add(new StrategyOption { Mode = StrategyMode.Heavy });

            StrategyMode mode = _config == null ? StrategyMode.Auto : _config.StrategyMode;
            for (int index = 0; index < _profileCombo.Items.Count; index++)
            {
                StrategyOption option = _profileCombo.Items[index] as StrategyOption;
                if (option != null && option.Mode == mode)
                {
                    _profileCombo.SelectedIndex = index;
                    break;
                }
            }
            if (_profileCombo.SelectedIndex < 0)
                _profileCombo.SelectedIndex = 0;
            _profileCombo.SelectedIndexChanged += ProfileComboSelectedIndexChanged;
        }

        private FanProfile GetActiveProfile()
        {
            StrategyMode mode = _config == null ? StrategyMode.Auto : _config.StrategyMode;
            AdaptivePowerTier tier = GetTierForMode(mode);
            return CreateFixedFanProfile(mode, tier);
        }

        private static AdaptivePowerTier GetTierForMode(StrategyMode mode)
        {
            switch (mode)
            {
                case StrategyMode.Quiet: return AdaptivePowerTier.Quiet;
                case StrategyMode.Code: return AdaptivePowerTier.Code;
                case StrategyMode.Heavy: return AdaptivePowerTier.Heavy;
                default: return AdaptivePowerTier.Daily;
            }
        }

        private static FanProfile CreateFixedFanProfile(StrategyMode mode, AdaptivePowerTier tier)
        {
            if (tier == AdaptivePowerTier.Heavy || mode == StrategyMode.Heavy)
                return DefaultProfiles.CreatePerformanceProfile();
            if (mode == StrategyMode.Quiet || tier == AdaptivePowerTier.Quiet)
                return DefaultProfiles.CreateQuietProfile();
            if (tier == AdaptivePowerTier.Daily || mode == StrategyMode.Daily)
                return DefaultProfiles.CreateDailyProfile();
            if (tier == AdaptivePowerTier.Code || mode == StrategyMode.Code)
                return DefaultProfiles.CreateBalancedProfile();
            return DefaultProfiles.CreateDailyProfile();
        }

        private static string GetCurrentStrategyLevelName(StrategyMode selectedMode, AdaptivePowerTier tier)
        {
            if (selectedMode == StrategyMode.Quiet || tier == AdaptivePowerTier.Quiet)
                return "1档 · 安静";
            switch (tier)
            {
                case AdaptivePowerTier.Code: return "3档 · 代码";
                case AdaptivePowerTier.Heavy: return "4档 · 重负载";
                default: return "2档 · 日常";
            }
        }

        private void ApplyFixedFanProfile(AdaptivePowerTier tier)
        {
            StrategyMode mode = _config == null ? StrategyMode.Auto : _config.StrategyMode;
            FanProfile profile = CreateFixedFanProfile(mode, tier);
            lock (_engineLock)
            {
                if (_engine != null)
                {
                    _engine.SetProfile(profile);
                    _engine.Reset();
                }
            }
        }

        private void ProfileComboSelectedIndexChanged(object sender, EventArgs e)
        {
            StrategyOption selected = _profileCombo.SelectedItem as StrategyOption;
            if (selected == null || _config == null)
                return;

            StrategyMode previousMode = _config.StrategyMode;
            AdaptivePowerTier previousTier = _adaptiveCurrentTier;
            _config.StrategyMode = selected.Mode;
            _config.ActiveProfileName = StrategyModeInfo.GetProfileName(selected.Mode);
            AdaptivePowerTier selectedTier = selected.Mode == StrategyMode.Auto
                ? GetAutoStartingTier(previousMode, previousTier)
                : GetTierForMode(selected.Mode);
            _adaptivePowerTierController?.ForceTier(
                selectedTier,
                selected.Mode == StrategyMode.Auto
                    ? "自动策略从当前" + GetCurrentStrategyLevelName(previousMode, previousTier) + "开始"
                    : "用户选择固定策略");
            _adaptiveCurrentTier = selectedTier;
            _adaptiveAppliedTier = (AdaptivePowerTier)(-1);
            _adaptiveXtuConfirmed = false;
            _adaptiveLastReason = "用户选择" + selected.Name + "策略，等待应用";
            ApplyFixedFanProfile(selectedTier);
            UpdateTrayStrategyStatus();
            SaveConfig();
            AppendLog("已选择策略：" + selected.Name + "；功耗和风扇参数由内置策略管理");
        }

        private static AdaptivePowerTier GetAutoStartingTier(StrategyMode previousMode, AdaptivePowerTier currentTier)
        {
            if (previousMode == StrategyMode.Auto)
                return currentTier;
            if (currentTier == AdaptivePowerTier.Quiet ||
                currentTier == AdaptivePowerTier.Daily ||
                currentTier == AdaptivePowerTier.Code ||
                currentTier == AdaptivePowerTier.Heavy)
                return currentTier;
            return GetTierForMode(previousMode);
        }

        private void SelectStrategyFromTray(StrategyMode mode)
        {
            if (_profileCombo == null)
                return;
            for (int index = 0; index < _profileCombo.Items.Count; index++)
            {
                StrategyOption option = _profileCombo.Items[index] as StrategyOption;
                if (option != null && option.Mode == mode)
                {
                    _profileCombo.SelectedIndex = index;
                    return;
                }
            }
        }

        private void LoadProfileIntoEditor(FanProfile profile)
        {
            // The former curve/property editors were intentionally removed.
            // Keeping this no-op preserves calibration call sites without
            // exposing writable fan or power parameters to the user.
        }
    }
}
