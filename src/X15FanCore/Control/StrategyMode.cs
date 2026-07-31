using System;

namespace X15FanCore.Control
{
    public enum StrategyMode
    {
        Auto = 0,
        Quiet = 1,
        // Keep Code/Heavy numeric values compatible with the previous build;
        // Daily is the newly introduced level 2.
        Code = 2,
        Heavy = 3,
        Daily = 4
    }

    public static class StrategyModeInfo
    {
        public static string GetName(StrategyMode mode)
        {
            switch (mode)
            {
                case StrategyMode.Quiet: return "1档 · 安静";
                case StrategyMode.Daily: return "2档 · 日常";
                case StrategyMode.Code: return "3档 · 代码";
                case StrategyMode.Heavy: return "4档 · 重负载";
                default: return "自动策略";
            }
        }

        public static bool TryParse(string value, out StrategyMode mode)
        {
            string text = value ?? string.Empty;
            if (text.Equals("Quiet", StringComparison.OrdinalIgnoreCase) || text.IndexOf("安静", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("静音", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                mode = StrategyMode.Quiet;
                return true;
            }
            if (text.Equals("Daily", StringComparison.OrdinalIgnoreCase) || text.IndexOf("日常", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                mode = StrategyMode.Daily;
                return true;
            }
            if (text.Equals("Stable", StringComparison.OrdinalIgnoreCase) || text.IndexOf("代码", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Code", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                mode = StrategyMode.Code;
                return true;
            }
            if (text.Equals("Performance", StringComparison.OrdinalIgnoreCase) || text.IndexOf("重负载", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                mode = StrategyMode.Heavy;
                return true;
            }
            if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) || text.IndexOf("自动", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                mode = StrategyMode.Auto;
                return true;
            }
            mode = StrategyMode.Auto;
            return false;
        }
    }
}
