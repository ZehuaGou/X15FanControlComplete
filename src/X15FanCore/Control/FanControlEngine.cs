using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class FanControlEngine
    {
        private FanProfile _profile;
        private ChannelController _cpu;
        private ChannelController _gpu;
        private DateTime _lastUpdateUtc;

        public FanControlEngine(FanProfile profile)
        {
            SetProfile(profile);
            Reset();
        }

        public FanProfile Profile
        {
            get { return _profile; }
        }

        public void SetProfile(FanProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException("profile");
            if (_cpu == null)
            {
                _cpu = new ChannelController(FanKind.Cpu, profile.Cpu);
                _gpu = new ChannelController(FanKind.Gpu, profile.Gpu);
            }
            else
            {
                _cpu.SetProfile(profile.Cpu);
                _gpu.SetProfile(profile.Gpu);
            }
        }

        public void Reset()
        {
            _cpu.Reset();
            _gpu.Reset();
            _lastUpdateUtc = DateTime.MinValue;
        }

        public ControlDecision Update(FanSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            DateTime now = snapshot.TimestampUtc == default(DateTime) ? DateTime.UtcNow : snapshot.TimestampUtc;
            double elapsedSeconds = _lastUpdateUtc == DateTime.MinValue ? 0.5 : Math.Max(0.05, Math.Min(5.0, (now - _lastUpdateUtc).TotalSeconds));
            _lastUpdateUtc = now;

            double cpuCoupling = 0;
            double gpuCoupling = 0;
            if (_profile.CouplingEnabled)
            {
                cpuCoupling = CalculateCoupling(snapshot.GpuTemperatureC);
                gpuCoupling = CalculateCoupling(snapshot.CpuTemperatureC);
            }

            return new ControlDecision
            {
                Cpu = _cpu.Update(snapshot.CpuTemperatureC, snapshot.CpuDutyPercent, cpuCoupling, now, elapsedSeconds),
                Gpu = _gpu.Update(snapshot.GpuTemperatureC, snapshot.GpuDutyPercent, gpuCoupling, now, elapsedSeconds)
            };
        }

        public void MarkCpuWritten(int percent, DateTime timestampUtc)
        {
            _cpu.MarkWritten(percent, timestampUtc);
        }

        public void MarkGpuWritten(int percent, DateTime timestampUtc)
        {
            _gpu.MarkWritten(percent, timestampUtc);
        }

        // External override detection is fed by the async write verification
        // tasks.  A confirmed override means another controller is fighting the
        // EC duty register, which this application must never tolerate.
        public bool CheckCpuExternalOverride(double readbackPercent)
        {
            return _cpu.CheckExternalOverride(readbackPercent);
        }

        public bool CheckGpuExternalOverride(double readbackPercent)
        {
            return _gpu.CheckExternalOverride(readbackPercent);
        }

        private double CalculateCoupling(int otherTemperatureC)
        {
            if (otherTemperatureC <= _profile.CouplingStartTemperatureC)
            {
                return 0;
            }

            double ratio = Math.Min(1.0, (otherTemperatureC - _profile.CouplingStartTemperatureC) / 15.0);
            return Math.Max(0, _profile.CouplingMaximumPercent) * ratio;
        }
    }
}
