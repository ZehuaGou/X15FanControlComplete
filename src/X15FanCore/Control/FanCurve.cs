using System;
using System.Collections.Generic;
using System.Linq;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public static class FanCurve
    {
        public static double Interpolate(IList<FanCurvePoint> points, double temperatureC)
        {
            List<FanCurvePoint> curve = Normalize(points);
            if (curve.Count == 0)
            {
                return 100;
            }

            if (temperatureC <= curve[0].TemperatureC)
            {
                return Clamp(curve[0].PowerPercent, 0, 100);
            }

            for (int index = 1; index < curve.Count; index++)
            {
                FanCurvePoint high = curve[index];
                FanCurvePoint low = curve[index - 1];
                if (temperatureC <= high.TemperatureC)
                {
                    double span = high.TemperatureC - low.TemperatureC;
                    if (span <= 0.0001)
                    {
                        return Clamp(high.PowerPercent, 0, 100);
                    }

                    double ratio = (temperatureC - low.TemperatureC) / span;
                    return Clamp(low.PowerPercent + ratio * (high.PowerPercent - low.PowerPercent), 0, 100);
                }
            }

            return Clamp(curve[curve.Count - 1].PowerPercent, 0, 100);
        }

        public static double ApproximateTemperatureForPower(IList<FanCurvePoint> points, double powerPercent)
        {
            List<FanCurvePoint> curve = Normalize(points);
            if (curve.Count == 0)
            {
                return 100;
            }

            if (powerPercent <= curve[0].PowerPercent)
            {
                return curve[0].TemperatureC;
            }

            for (int index = 1; index < curve.Count; index++)
            {
                FanCurvePoint high = curve[index];
                FanCurvePoint low = curve[index - 1];
                double lowPower = Math.Min(low.PowerPercent, high.PowerPercent);
                double highPower = Math.Max(low.PowerPercent, high.PowerPercent);
                if (powerPercent >= lowPower && powerPercent <= highPower)
                {
                    double span = high.PowerPercent - low.PowerPercent;
                    if (Math.Abs(span) <= 0.0001)
                    {
                        return high.TemperatureC;
                    }

                    double ratio = (powerPercent - low.PowerPercent) / span;
                    return low.TemperatureC + ratio * (high.TemperatureC - low.TemperatureC);
                }
            }

            return curve[curve.Count - 1].TemperatureC;
        }

        public static List<FanCurvePoint> Normalize(IList<FanCurvePoint> points)
        {
            if (points == null)
            {
                return new List<FanCurvePoint>();
            }

            return points
                .Where(point => point != null)
                .OrderBy(point => point.TemperatureC)
                .Select(point => new FanCurvePoint(point.TemperatureC, Clamp(point.PowerPercent, 0, 100)))
                .ToList();
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
