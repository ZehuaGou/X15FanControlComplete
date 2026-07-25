using System;
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
            if (config.UiRefreshIntervalMs <= 0) { config.UiRefreshIntervalMs = 500; changed = true; }
            if (config.ChartSampleIntervalMs <= 0) { config.ChartSampleIntervalMs = 1000; changed = true; }
            if (config.MaxUiLogLines <= 0) { config.MaxUiLogLines = 1500; changed = true; }

            return changed;
        }

        // 将默认配置中有但用户配置中没有的配置追加进去，方便用户升级后使用新配置
        private static bool MergeDefaultProfiles(AppConfig userConfig, AppConfig defaultConfig)
        {
            if (defaultConfig?.Profiles == null)
                return false;

            bool changed = false;
            foreach (FanProfile defaultProfile in defaultConfig.Profiles)
            {
                bool exists = false;
                foreach (FanProfile userProfile in userConfig.Profiles)
                {
                    if (userProfile.Name == defaultProfile.Name)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    userConfig.Profiles.Add(defaultProfile);
                    changed = true;
                }
            }

            return changed;
        }
    }
}
