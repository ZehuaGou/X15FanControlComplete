using System;
using System.Collections.Generic;

namespace X15FanCore.Control
{
    public sealed class TemperatureFilter
    {
        private readonly Queue<double> _window = new Queue<double>();
        private int _windowSize;
        private double _fastAlpha;
        private double _slowAlpha;
        private bool _initialized;
        private double _sum;

        public TemperatureFilter(int windowSize, double fastAlpha, double slowAlpha)
        {
            Configure(windowSize, fastAlpha, slowAlpha);
        }

        public double FastEma { get; private set; }
        public double SlowEma { get; private set; }
        public double MovingAverage { get; private set; }
        public double ControlTemperature { get; private set; }

        public void Configure(int windowSize, double fastAlpha, double slowAlpha)
        {
            _windowSize = Math.Max(1, windowSize);
            _fastAlpha = Clamp(fastAlpha, 0.01, 1.0);
            _slowAlpha = Clamp(slowAlpha, 0.01, 1.0);
        }

        public void Reset()
        {
            _window.Clear();
            _sum = 0;
            _initialized = false;
            FastEma = 0;
            SlowEma = 0;
            MovingAverage = 0;
            ControlTemperature = 0;
        }

        public double Add(double value)
        {
            if (!_initialized)
            {
                FastEma = value;
                SlowEma = value;
                MovingAverage = value;
                ControlTemperature = value;
                _initialized = true;
            }
            else
            {
                FastEma = _fastAlpha * value + (1.0 - _fastAlpha) * FastEma;
                SlowEma = _slowAlpha * value + (1.0 - _slowAlpha) * SlowEma;
            }

            _window.Enqueue(value);
            _sum += value;
            while (_window.Count > _windowSize)
            {
                _sum -= _window.Dequeue();
            }

            MovingAverage = _sum / _window.Count;

            // React promptly while temperature is rising, but decay slowly after a spike.
            double risingSignal = Math.Max(FastEma, MovingAverage);
            double fallingSignal = Math.Max(SlowEma, MovingAverage);
            ControlTemperature = risingSignal >= SlowEma ? risingSignal : fallingSignal;
            return ControlTemperature;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
