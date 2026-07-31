using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Json;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class ConfigStore
    {
        public ConfigStore(string configPath)
        {
            ConfigPath = configPath ?? throw new ArgumentNullException("configPath");
        }

        public string ConfigPath { get; private set; }
        public string LastLoadDiagnostic { get; private set; }

        public AppConfig LoadOrCreate()
        {
            LastLoadDiagnostic = null;

            if (!File.Exists(ConfigPath))
            {
                AppConfig defaults = DefaultProfiles.CreateConfig();
                Save(defaults);
                return defaults;
            }

            AppConfig config;
            try
            {
                using (FileStream stream = File.OpenRead(ConfigPath))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppConfig));
                    config = serializer.ReadObject(stream) as AppConfig;
                    if (config == null || config.Profiles == null || config.Profiles.Count == 0)
                    {
                        throw new InvalidDataException("Configuration contains no profiles.");
                    }
                }
            }
            catch (Exception ex)
            {
                LastLoadDiagnostic = "配置读取/解析失败：" + ex.Message;
                Trace.TraceError(LastLoadDiagnostic);
                string backup = ConfigPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                try
                {
                    File.Copy(ConfigPath, backup, true);
                }
                catch
                {
                }
                AppConfig defaults = DefaultProfiles.CreateConfig();
                Save(defaults);
                return defaults;
            }

            bool changed = MergeDefaultProfiles(config, DefaultProfiles.CreateConfig());
            changed |= NormalizeConfig(config);
            if (changed)
            {
                try
                {
                    Save(config);
                }
                catch (Exception ex)
                {
                    // 配置已经成功读取。迁移写回失败不能触发“损坏配置”恢复，
                    // 否则会用默认配置覆盖用户的 Profile 和曲线。
                    LastLoadDiagnostic = "配置迁移保存失败；继续使用已读取的内存配置，原文件保持不变：" + ex.Message;
                    Trace.TraceError(LastLoadDiagnostic);
                }
            }

            return config;
        }

        public void Save(AppConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            string directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = ConfigPath + ".tmp";
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppConfig));
            using (FileStream stream = File.Create(temporaryPath))
            {
                serializer.WriteObject(stream, config);
                stream.Flush(true);
            }

            if (File.Exists(ConfigPath))
            {
                File.Replace(temporaryPath, ConfigPath, ConfigPath + ".bak", true);
            }
            else
            {
                File.Move(temporaryPath, ConfigPath);
            }
        }

        // 规范化配置文件：为旧版本config.json中缺失的字段设置安全默认值
        // 返回true表示进行了修改，调用方应保存
        private static bool NormalizeConfig(AppConfig config)
        {
            bool changed = false;

            if (config.ConfigVersion < 2)
            {
                // 从版本1升级：桌面体验字段可能缺失
                config.StartMinimizedToTray = true;
                config.DetailedVerificationLogging = false;
                config.AutoEnterActiveOnStartup = false;
                config.StartWithWindows = false;
                config.UiRefreshIntervalMs = 500;
                config.ChartSampleIntervalMs = 1000;
                config.MaxUiLogLines = 1500;
                config.ConfigVersion = 2;
                changed = true;
            }

            // 对异常值进行规范化（无论版本）
            if (!Enum.IsDefined(typeof(RunMode), config.StartupMode))
            {
                config.StartupMode = RunMode.ReadOnly;
                changed = true;
            }
            if (config.UiRefreshIntervalMs <= 0) { config.UiRefreshIntervalMs = 500; changed = true; }
            if (config.ChartSampleIntervalMs <= 0) { config.ChartSampleIntervalMs = 1000; changed = true; }
            if (config.MaxUiLogLines <= 0) { config.MaxUiLogLines = 1500; changed = true; }
            if (config.AdaptivePower == null)
            {
                config.AdaptivePower = new AdaptivePowerSettings();
                changed = true;
            }
            config.AdaptivePower.Normalize();

            StrategyMode parsedMode;
            if (StrategyModeInfo.TryParse(config.ActiveProfileName, out parsedMode))
            {
                if (config.StrategyMode != parsedMode)
                {
                    config.StrategyMode = parsedMode;
                    changed = true;
                }
            }
            else if (!System.Enum.IsDefined(typeof(StrategyMode), config.StrategyMode))
            {
                config.StrategyMode = StrategyMode.Auto;
                changed = true;
            }

            return changed;
        }

        // 将默认配置中有但用户配置中没有的配置追加进去，方便用户升级后使用新配置
        private static bool MergeDefaultProfiles(AppConfig userConfig, AppConfig defaultConfig)
        {
            if (userConfig == null || userConfig.Profiles == null || defaultConfig?.Profiles == null)
                return false;

            bool changed = false;
            Dictionary<string, FanProfile> selectedBuiltIns = new Dictionary<string, FanProfile>(StringComparer.OrdinalIgnoreCase);
            List<FanProfile> retainedProfiles = new List<FanProfile>();
            string activeBuiltInKey = null;

            foreach (FanProfile userProfile in userConfig.Profiles)
            {
                string builtInKey = GetBuiltInKey(userProfile);
                if (string.Equals(userProfile?.Name, userConfig.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                    activeBuiltInKey = builtInKey;

                if (builtInKey == null)
                {
                    retainedProfiles.Add(userProfile);
                    continue;
                }

                if (selectedBuiltIns.ContainsKey(builtInKey))
                {
                    changed = true;
                    continue;
                }

                FanProfile canonical = FindDefaultProfile(defaultConfig, builtInKey);
                if (canonical == null)
                {
                    // Brz Legacy is a retired rollback profile, not a normal user mode.
                    // Drop it instead of silently reintroducing it on every startup.
                    changed = true;
                    continue;
                }

                selectedBuiltIns[builtInKey] = userProfile;
                if (!string.Equals(userProfile.Name, canonical.Name, StringComparison.Ordinal))
                {
                    userProfile.Name = canonical.Name;
                    changed = true;
                }
                changed |= DefaultProfiles.ApplySafetyPolicy(userProfile);
                retainedProfiles.Add(userProfile);
            }

            foreach (FanProfile defaultProfile in defaultConfig.Profiles)
            {
                string builtInKey = GetBuiltInKey(defaultProfile);
                if (builtInKey == null || selectedBuiltIns.ContainsKey(builtInKey))
                    continue;

                retainedProfiles.Add(defaultProfile);
                selectedBuiltIns[builtInKey] = defaultProfile;
                changed = true;
            }

            if (retainedProfiles.Count == 0)
                return true;

            userConfig.Profiles = retainedProfiles;
            if (activeBuiltInKey != null)
            {
                FanProfile active = FindDefaultProfile(userConfig, activeBuiltInKey);
                if (active != null && !string.Equals(userConfig.ActiveProfileName, active.Name, StringComparison.Ordinal))
                {
                    userConfig.ActiveProfileName = active.Name;
                    changed = true;
                }
                else if (active == null)
                {
                    userConfig.ActiveProfileName = userConfig.Profiles[0].Name;
                    changed = true;
                }
            }
            else if (string.IsNullOrWhiteSpace(userConfig.ActiveProfileName) || FindProfile(userConfig, userConfig.ActiveProfileName) == null)
            {
                userConfig.ActiveProfileName = userConfig.Profiles[0].Name;
                changed = true;
            }

            return changed;
        }

        private static FanProfile FindDefaultProfile(AppConfig config, string key)
        {
            if (config?.Profiles == null)
                return null;

            foreach (FanProfile profile in config.Profiles)
            {
                if (string.Equals(GetBuiltInKey(profile), key, StringComparison.OrdinalIgnoreCase))
                    return profile;
            }

            return null;
        }

        private static FanProfile FindProfile(AppConfig config, string name)
        {
            if (config?.Profiles == null || name == null)
                return null;

            foreach (FanProfile profile in config.Profiles)
            {
                if (string.Equals(profile?.Name, name, StringComparison.OrdinalIgnoreCase))
                    return profile;
            }

            return null;
        }

        private static string GetBuiltInKey(FanProfile profile)
        {
            if (profile == null)
                return null;

            string name = profile.Name ?? string.Empty;
            if (name.Equals("自动", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Auto", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Auto";

            if (name.Equals("代码", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Stable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Code", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("LowNoise", StringComparison.OrdinalIgnoreCase) >= 0 ||
                HasPoint(profile.Cpu, 50, 15) && HasPoint(profile.Cpu, 80, 54) && HasPoint(profile.Gpu, 70, 42))
                return "Code";

            if (name.IndexOf("Quiet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Silent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("静音", StringComparison.OrdinalIgnoreCase) >= 0 ||
                HasPoint(profile.Cpu, 65, 35) && HasPoint(profile.Cpu, 75, 55) && HasPoint(profile.Cpu, 95, 100))
                return "Quiet";

            if (name.Equals("重负载", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Performance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                HasPoint(profile.Cpu, 80, 70) && HasPoint(profile.Cpu, 85, 88) && HasPoint(profile.Gpu, 80, 65))
                return "Heavy";

            if (name.IndexOf("Brz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("BRZ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                HasPoint(profile.Cpu, 80, 60) && HasPoint(profile.Cpu, 85, 82) && HasPoint(profile.Gpu, 80, 55))
                return "Brz Legacy";

            return null;
        }

        private static bool HasPoint(FanChannelProfile channel, double temperature, double percent)
        {
            if (channel?.Curve == null)
                return false;

            foreach (FanCurvePoint point in channel.Curve)
            {
                if (Math.Abs(point.TemperatureC - temperature) < 0.01 && Math.Abs(point.PowerPercent - percent) < 0.01)
                    return true;
            }

            return false;
        }
    }
}
