namespace X15FanCore.Models
{
    public enum RunMode
    {
        ReadOnly = 0,
        Simulation = 1,
        Active = 2
    }

    public enum FanKind
    {
        Cpu = 1,
        Gpu = 2
    }

    public enum DecisionReason
    {
        Normal = 0,
        EmergencyStage1 = 1,
        EmergencyStage2 = 2,
        InvalidSensor = 3,
        Initializing = 4,
        EmergencyStage3 = 5,
        RpmSafety = 6
    }
}
