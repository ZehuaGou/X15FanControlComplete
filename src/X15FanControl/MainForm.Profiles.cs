using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
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
            TabPage tab = new TabPage("配置与曲线") { BackColor = UiBackground };
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(12),
                BackColor = UiBackground
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

            Panel cpuEditor = BuildProfileEditorCard(
                "CPU 曲线与控制参数", UiCpuAccent,
                out _cpuCurveGrid, out _cpuPropertyGrid);
            cpuEditor.Margin = new Padding(0, 0, 6, 0);
            Panel gpuEditor = BuildProfileEditorCard(
                "GPU 曲线与控制参数", UiGpuAccent,
                out _gpuCurveGrid, out _gpuPropertyGrid);
            gpuEditor.Margin = new Padding(6, 0, 0, 0);

            Panel footer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UiSurface,
                Margin = new Padding(0, 8, 0, 0),
                Padding = new Padding(10, 4, 10, 4)
            };
            _profilePropertyGrid = new PropertyGrid { Visible = false };
            _saveProfileButton = new Button { Text = "保存配置", Width = 112, Height = 32 };
            StyleButton(_saveProfileButton, UiCpuAccent, Color.White);
            _saveProfileButton.Margin = new Padding(4, 1, 4, 0);
            _saveProfileButton.Click += SaveProfileButtonClick;
            _reloadProfileButton = new Button { Text = "重新加载", Width = 96, Height = 32 };
            StyleButton(_reloadProfileButton, Color.FromArgb(232, 237, 244), UiText);
            _reloadProfileButton.Margin = new Padding(4, 1, 4, 0);
            _reloadProfileButton.Click += delegate { LoadProfileIntoEditor(GetActiveProfile()); };
            Button profileSettingsButton = new Button { Text = "配置设置", Width = 102, Height = 32 };
            StyleButton(profileSettingsButton, Color.FromArgb(232, 237, 244), UiText);
            profileSettingsButton.Margin = new Padding(4, 1, 4, 0);
            profileSettingsButton.Click += delegate { ShowProfileSettingsDialog(); };
            Label note = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = UiMuted,
                Padding = new Padding(0, 0, 8, 0),
                Text = "活动模式使用已保存的配置。曲线温度必须从上到下递增。"
            };
            FlowLayoutPanel footerActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = UiSurface
            };
            footerActions.Controls.Add(_saveProfileButton);
            footerActions.Controls.Add(_reloadProfileButton);
            footerActions.Controls.Add(profileSettingsButton);
            footer.Controls.Add(note);
            footer.Controls.Add(footerActions);

            root.Controls.Add(cpuEditor, 0, 0);
            root.Controls.Add(gpuEditor, 1, 0);
            root.Controls.Add(footer, 0, 1);
            root.SetColumnSpan(footer, 2);
            tab.Controls.Add(root);
            return tab;
        }

        private Panel BuildProfileEditorCard(
            string title,
            Color accentColor,
            out DataGridView curveGrid,
            out PropertyGrid propertyGrid)
        {
            Panel card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = UiSurface,
                Padding = new Padding(12, 10, 12, 12)
            };
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = UiSurface
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));

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

            curveGrid = BuildCurveGrid(accentColor);
            propertyGrid = BuildPropertyGrid();
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(BuildEditorSection("风扇曲线", curveGrid), 0, 1);
            layout.Controls.Add(BuildEditorSection("控制参数", propertyGrid), 0, 2);
            card.Controls.Add(layout);
            return card;
        }

        private static Panel BuildEditorSection(string title, Control content)
        {
            Panel section = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 0, 4),
                Margin = new Padding(4),
                BackColor = UiSurface
            };
            section.Controls.Add(content);
            section.Controls.Add(new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiMuted,
                Font = new Font("Segoe UI Semibold", 9F)
            });
            return section;
        }

        private static DataGridView BuildCurveGrid(Color accentColor)
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle = BorderStyle.None,
                BackgroundColor = UiSurface,
                GridColor = UiBorder,
                RowTemplate = { Height = 32 },
                ColumnHeadersHeight = 36,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(239, 243, 248);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = UiText;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.DefaultCellStyle.BackColor = UiSurface;
            grid.DefaultCellStyle.ForeColor = UiText;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(
                Math.Min(255, accentColor.R + 175),
                Math.Min(255, accentColor.G + 125),
                Math.Min(255, accentColor.B + 20));
            grid.DefaultCellStyle.SelectionForeColor = UiText;
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
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

        private static PropertyGrid BuildPropertyGrid()
        {
            return new PropertyGrid
            {
                Dock = DockStyle.Fill,
                HelpVisible = false,
                ToolbarVisible = false,
                PropertySort = PropertySort.Categorized,
                BackColor = UiSurface,
                ViewBackColor = UiSurface,
                ViewForeColor = UiText,
                LineColor = UiBorder,
                CategoryForeColor = UiText
            };
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
            lock (_engineLock) { _engine?.SetProfile(selected); _engine?.Reset(); }
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
                    lock (_engineLock) { _engine.SetProfile(profile); }
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
                lock (_engineLock) { _engine.SetProfile(profile); _engine.Reset(); }
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
