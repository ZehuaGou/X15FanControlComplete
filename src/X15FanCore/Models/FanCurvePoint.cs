using System.Runtime.Serialization;

namespace X15FanCore.Models
{
    [DataContract]
    public sealed class FanCurvePoint
    {
        public FanCurvePoint()
        {
        }

        public FanCurvePoint(double temperatureC, double powerPercent)
        {
            TemperatureC = temperatureC;
            PowerPercent = powerPercent;
        }

        [DataMember(Order = 1)]
        public double TemperatureC { get; set; }

        [DataMember(Order = 2)]
        public double PowerPercent { get; set; }

        public override string ToString()
        {
            return TemperatureC.ToString("0.0") + " °C → " + PowerPercent.ToString("0.0") + "%";
        }
    }
}
