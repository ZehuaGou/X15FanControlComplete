using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace X15FanCore.Control
{
    public static class ConflictDetector
    {
        private static readonly string[] ConflictingProcessNames =
        {
            "BrzClevoFanControl",
            "EcWatchDog",
            "ClevoFanControl",
            "MyFanControl",
            "BtoFanControl"
        };

        public static IList<string> FindConflicts()
        {
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;
                    if (ConflictingProcessNames.Any(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(name + " (PID " + process.Id + ")");
                    }
                }
                catch
                {
                    // Ignore processes that exit or cannot be inspected.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return found.OrderBy(value => value).ToList();
        }
    }
}
