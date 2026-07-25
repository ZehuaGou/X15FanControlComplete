using System.Runtime.Serialization;

namespace X15FanCore.Models
{
    [DataContract]
    public sealed class FanProfile
    {
        public FanProfile()
        {
            Name = "Profile";
            Description = string.Empty;
            Cpu = new FanChannelProfile();
            Gpu = new FanChannelProfile();
            CouplingEnabled = false;
            CouplingStartTemperatureC = 75;
            CouplingMaximumPercent = 4;
        }

        [DataMember(Order = 1)]
        public string Name { get; set; }

        [DataMember(Order = 2)]
        public string Description { get; set; }

        [DataMember(Order = 3)]
        public FanChannelProfile Cpu { get; set; }

        [DataMember(Order = 4)]
        public FanChannelProfile Gpu { get; set; }

        [DataMember(Order = 5)]
        public bool CouplingEnabled { get; set; }

        [DataMember(Order = 6)]
        public int CouplingStartTemperatureC { get; set; }

        [DataMember(Order = 7)]
        public double CouplingMaximumPercent { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
