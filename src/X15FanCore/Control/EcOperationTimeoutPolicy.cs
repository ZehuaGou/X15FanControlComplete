namespace X15FanCore.Control
{
    /// <summary>
    /// EC native calls on this notebook can occasionally take a little over
    /// five seconds and still complete successfully.  A slow completed call is
    /// diagnostic evidence, not proof that the native worker is hung.
    ///
    /// The hard deadline remains below the independent watchdog's 30-second
    /// heartbeat deadline, leaving the watchdog time to restore OEM automatic
    /// fan control if the native call really stops making progress.
    /// </summary>
    public static class EcOperationTimeoutPolicy
    {
        public const int SlowWarningMilliseconds = 5000;
        public const int HardTimeoutMilliseconds = 12000;

        public static bool IsSlow(long elapsedMilliseconds)
        {
            return elapsedMilliseconds >= SlowWarningMilliseconds;
        }

        public static bool IsHardTimeout(long elapsedMilliseconds, bool operationCompleted)
        {
            return !operationCompleted && elapsedMilliseconds >= HardTimeoutMilliseconds;
        }
    }
}
