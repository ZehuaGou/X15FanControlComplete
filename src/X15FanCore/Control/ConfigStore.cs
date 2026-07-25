using System;
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

        public AppConfig LoadOrCreate()
        {
            if (!File.Exists(ConfigPath))
            {
                AppConfig defaults = DefaultProfiles.CreateConfig();
                Save(defaults);
                return defaults;
            }

            try
            {
                using (FileStream stream = File.OpenRead(ConfigPath))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppConfig));
                    AppConfig config = serializer.ReadObject(stream) as AppConfig;
                    if (config == null || config.Profiles == null || config.Profiles.Count == 0)
                    {
                        throw new InvalidDataException("Configuration contains no profiles.");
                    }

                    // 合并默认配置中缺失的配置（用户升级后自动获得新配置）
                    MergeDefaultProfiles(config, DefaultProfiles.CreateConfig());

                    // 规范化新增字段（旧JSON反序列化后可能缺失）
                    NormalizeConfig(config);

                    return config;
                }
            }
            catch
            {
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
        private static void NormalizeConfig(AppConfig config)
        {
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
            }
        }

        // 将默认配置中有但用户配置中没有的配置追加进去，方便用户升级后使用新配置
        private static void MergeDefaultProfiles(AppConfig userConfig, AppConfig defaultConfig)
        {
            if (defaultConfig?.Profiles == null)
                return;

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
                }
            }
        }
    }
}
