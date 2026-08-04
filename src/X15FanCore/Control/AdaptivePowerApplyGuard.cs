namespace X15FanCore.Control
{
    /// <summary>
    /// Guards publication of an asynchronous CPU power application.
    /// Acoustic and thermal governance can deliberately make the effective
    /// tier differ from the load-requested tier, so only generation and the
    /// current effective tier determine whether a completed write is current.
    /// </summary>
    public static class AdaptivePowerApplyGuard
    {
        public static bool CanPublish(
            int completedGeneration,
            int currentGeneration,
            AdaptivePowerTier completedTier,
            AdaptivePowerTier currentEffectiveTier)
        {
            return completedGeneration == currentGeneration &&
                   completedTier == currentEffectiveTier;
        }
    }
}
