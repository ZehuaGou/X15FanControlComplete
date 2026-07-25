using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using X15FanCore.Control;
using X15FanCore.Models;

namespace X15FanControl
{
    public partial class MainForm
    {
        private TabPage BuildProfilesTab()
        {
            TabPage tab = new TabPage("配置与曲线");
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            TabControl cpuTabs = new TabControl { Dock = DockStyle.Fill };
            TabPage cpuCurvePage = new TabPage("CPU 曲线");
            _cpuCurveGrid = BuildCurveGrid();
            cpuCurvePage.Controls.Add(_cpuCurveGrid);
            TabPage cpuSettingsPage = new TabPage("CPU 控制设置");
            _cpuPropertyGrid = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = true };
            cpuSettingsPage.Controls.Add(_cpuPropertyGrid);
            cpuTabs.TabPages.Add(cpuCurvePage);
            cpuTabs.TabPages.Add(cpuSettingsPage);

            TabControl gpuTabs = new TabControl { Dock = DockStyle.Fill };
            TabPage gpuCurvePage = new TabPage("GPU 曲线");
            _gpuCurveGrid = BuildCurveGrid();
            gpuCurvePage.Controls.Add(_gpuCurveGrid);
            TabPage gpuSettingsPage = new TabPage("GPU 控制设置");
            _gpuPropertyGrid = new PropertyGrid { Dock = DockStyle.Fill, HelpVisible = true, ToolbarVisible = true };
            gpuSettingsPage.Controls.Add(_gpuPropertyGrid);
            gpuTabs.TabPages.Add(gpuCurvePage);
            gpuTabs.TabPages.Add(gpuSettingsPage);

            Panel footer = new Panel { Dock = DockStyle.Fill };
            _profilePropertyGrid = new PropertyGrid { Visible = false };
            _saveProfileButton = new Button { Text = "保存配置", Width = 120, Height = 30, Left = 8, Top = 7 };
            _saveProfileButton.Click += SaveProfileButtonClick;
            _reloadProfileButton = new Button { Text = "重新加载", Width = 95, Height = 30, Left = 136, Top = 7 };
            _reloadProfileButton.Click += delegate { LoadProfileIntoEditor(GetActiveProfile()); };
            Button profileSettingsButton = new Button { Text = "配置设置", Width = 115, Height = 30, Left = 239, Top = 7 };
            profileSettingsButton.Click += delegate { ShowProfileSettingsDialog(); };
            Label note = new Label
            {
                AutoSize = true,
                Left = 365,
                Top = 13,
                Text = "活动模式使用已保存的配置。曲线温度必须从上到下递增。"
            };
            footer.Controls.Add(_saveProfileButton);
            footer.Controls.Add(_reloadProfileButton);
            footer.Controls.Add(profileSettingsButton);
            footer.Controls.Add(note);

            root.Controls.Add(cpuTabs, 0, 0);
            root.Controls.Add(gpuTabs, 1, 0);
            root.Controls.Add(footer, 0, 1);
            root.SetColumnSpan(footer, 2);
            tab.Controls.Add(root);
            return tab;
        }

        private static DataGridView BuildCurveGrid()
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "TemperatureC",
                HeaderText = "温度 °C",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = "PowerPercent",
                HeaderText = "风扇功率 %",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            return grid;
        }

        private void PopulateProfiles()
        {
            _profileCombo.Items.Clear();
            foreach (FanProfile profile in _config.Profiles)
            {
                _profileCombo.Items.Add(profile);
            }

            FanProfile active = GetActiveProfile();
            _profileCombo.SelectedItem = active;
            if (_profileCombo.SelectedIndex < 0 && _profileCombo.Items.Count > 0)
            {
                _profileCombo.SelectedIndex = 0;
            }
        }

        private FanProfile GetActiveProfile()
        {
            FanProfile profile = _config.Profiles.FirstOrDefault(item => string.Equals(item.Name, _config.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
            {
                profile = _config.Profiles.First();
                _config.ActiveProfileName = profile.Name;
            }

            return profile;
        }

        private void ProfileComboSelectedIndexChanged(object sender, EventArgs e)
        {
            FanProfile selected = _profileCombo.SelectedItem as FanProfile;
            if (selected == null || _config == null)
            {
                return;
            }

            _config.ActiveProfileName = selected.Name;
            _engine?.SetProfile(selected);
            _engine?.Reset();
            LoadProfileIntoEditor(selected);
            SaveConfig();
            AppendLog("已选择配置：" + selected.Name);
        }

        private void LoadProfileIntoEditor(FanProfile profile)
        {
            if (profile == null || _cpuCurveGrid == null)
            {
                return;
            }

            _cpuCurveBinding = new BindingList<FanCurvePoint>(profile.Cpu.Curve.Select(point => new FanCurvePoint(point.TemperatureC, point.PowerPercent)).ToList());
            _gpuCurveBinding = new BindingList<FanCurvePoint>(profile.Gpu.Curve.Select(point => new FanCurvePoint(point.TemperatureC, point.PowerPercent)).ToList());
            _cpuCurveGrid.DataSource = _cpuCurveBinding;
            _gpuCurveGrid.DataSource = _gpuCurveBinding;
            _cpuPropertyGrid.SelectedObject = profile.Cpu;
            _gpuPropertyGrid.SelectedObject = profile.Gpu;
            _profilePropertyGrid.SelectedObject = profile;
        }

        private void ShowProfileSettingsDialog()
        {
            FanProfile profile = GetActiveProfile();
            using (Form dialog = new Form())
            {
                dialog.Text = "配置设置 — " + profile.Name;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new System.Drawing.Size(520, 620);
                PropertyGrid grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = profile, HelpVisible = true };
                Button save = new Button { Text = "保存", Dock = DockStyle.Bottom, Height = 38 };
                save.Click += delegate
                {
                    _config.ActiveProfileName = profile.Name;
                    _configStore.Save(_config);
                    _engine.SetProfile(profile);
                    PopulateProfiles();
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };
                dialog.Controls.Add(grid);
                dialog.Controls.Add(save);
                dialog.ShowDialog(this);
            }
        }

        private void SaveProfileButtonClick(object sender, EventArgs e)
        {
            try
            {
                _cpuCurveGrid.EndEdit();
                _gpuCurveGrid.EndEdit();
                FanProfile profile = GetActiveProfile();
                profile.Cpu.Curve = ValidateAndNormalizeCurve(_cpuCurveBinding, "CPU");
                profile.Gpu.Curve = ValidateAndNormalizeCurve(_gpuCurveBinding, "GPU");
                _configStore.Save(_config);
                _engine.SetProfile(profile);
                _engine.Reset();
                LoadProfileIntoEditor(profile);
                AppendLog("已保存配置：" + profile.Name);
                MessageBox.Show("配置已保存。", "X15 风扇控制", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "配置验证错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static List<FanCurvePoint> ValidateAndNormalizeCurve(IEnumerable<FanCurvePoint> source, string name)
        {
            List<FanCurvePoint> curve = FanCurve.Normalize(source == null ? null : source.ToList());
            if (curve.Count < 2)
            {
                throw new InvalidOperationException(name + " 曲线至少需要两个点。");
            }

            for (int index = 1; index < curve.Count; index++)
            {
                if (curve[index].TemperatureC <= curve[index - 1].TemperatureC)
                {
                    throw new InvalidOperationException(name + " 曲线温度必须严格递增。");
                }

                if (curve[index].PowerPercent < curve[index - 1].PowerPercent)
                {
                    throw new InvalidOperationException(name + " 风扇功率不应随温度升高而降低。");
                }
            }

            return curve;
        }
    }
}
