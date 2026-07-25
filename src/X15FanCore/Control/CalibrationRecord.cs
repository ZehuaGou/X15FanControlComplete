using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class CalibrationRecord
    {
        public DateTime TimestampUtc { get; set; }
        public FanKind Fan { get; set; }
        public int DutyPercent { get; set; }
        public int TemperatureC { get; set; }
        public int Rpm { get; set; }
        public bool MarkedNoisy { get; set; }
        public bool MarkedStable { get; set; }

        public override string ToString()
        {
            string mark = MarkedNoisy ? "NOISY" : MarkedStable ? "STABLE" : "";
            return Fan + " " + DutyPercent + "% / " + Rpm + " RPM / " + TemperatureC + " °C " + mark;
        }
    }
}
