namespace X15FanCore.Control
{
    /// <summary>
    /// OEM mode 只读观测器（架构收束 2026-08-02）。
    ///
    /// 只记录 DCHU page1 offset1 的 OEM mode 观测值供日志/诊断，判断潜在
    /// 外部影响（用户手动切换 Control Center 模式等）。本类没有任何写入
    /// 或写回 API（无 Set/Apply/Restore），编译期即保证「OEM mode 改变
    /// 只记日志，不触发自动写回或档位重映射」。
    /// </summary>
    public sealed class OemModeObserver
    {
        private int _observedMode = -1;
        private string _lastTransition = string.Empty;

        public int ObservedMode { get { return _observedMode; } }
        public string LastTransition { get { return _lastTransition ?? string.Empty; } }

        /// <summary>记录一次观测；模式变化时更新转换描述（供日志输出）。</summary>
        public void Observe(int mode)
        {
            if (mode == _observedMode)
                return;
            _lastTransition = _observedMode < 0
                ? "initial=" + mode
                : "changed " + _observedMode + "->" + mode;
            _observedMode = mode;
        }

        public void Reset()
        {
            _observedMode = -1;
            _lastTransition = string.Empty;
        }
    }
}
