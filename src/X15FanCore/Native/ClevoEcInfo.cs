using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace X15FanCore.Native
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EcData
    {
        public byte Remote;
        public byte Local;
        public byte FanDuty;
        public byte Reserve;
    }

    public sealed class ClevoEcInfo : IDisposable
    {
        private IntPtr _module;
        private bool _disposed;

        private InitIoDelegate _initIo;
        private GetTempFanDutyDelegate _getTempFanDuty;
        private SetFanDutyDelegate _setFanDuty;
        private SetFanDutyAutoDelegate _setFanDutyAuto;
        private GetIntDelegate _getFanCount;
        private GetIntDelegate _getCpuFanRpmRaw;
        private GetIntDelegate _getGpuFanRpmRaw;

        public ClevoEcInfo(string dllPath)
        {
            if (IntPtr.Size != 4)
            {
                throw new PlatformNotSupportedException("ClevoEcInfo.dll is 32-bit. Build and run this program as x86.");
            }

            if (string.IsNullOrWhiteSpace(dllPath))
            {
                throw new ArgumentNullException("dllPath");
            }

            string fullPath = Path.GetFullPath(dllPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("ClevoEcInfo.dll was not found.", fullPath);
            }

            _module = NativeMethods.LoadLibrary(fullPath);
            if (_module == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load ClevoEcInfo.dll. The NTPort driver may be missing or blocked.");
            }

            try
            {
                _initIo = GetDelegate<InitIoDelegate>("InitIo");
                _getTempFanDuty = GetDelegate<GetTempFanDutyDelegate>("GetTempFanDuty");
                _setFanDuty = GetDelegate<SetFanDutyDelegate>("SetFanDuty");
                _setFanDutyAuto = GetDelegate<SetFanDutyAutoDelegate>("SetFanDutyAuto");
                _getFanCount = TryGetDelegate<GetIntDelegate>("GetFanCount");
                _getCpuFanRpmRaw = TryGetDelegate<GetIntDelegate>("GetCpuFanRpm");
                _getGpuFanRpmRaw = TryGetDelegate<GetIntDelegate>("GetGpuFanRpm");

                if (!_initIo())
                {
                    throw new InvalidOperationException("Clevo EC initialization failed. Run as administrator and verify the original driver installation.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int GetFanCount()
        {
            ThrowIfDisposed();
            return _getFanCount == null ? 2 : _getFanCount();
        }

        public EcData ReadChannel(int fanNumber)
        {
            ThrowIfDisposed();
            return _getTempFanDuty(fanNumber);
        }

        public int GetTemperatureC(int fanNumber)
        {
            return ReadChannel(fanNumber).Remote;
        }

        public int GetTemperatureLocalC(int fanNumber)
        {
            return ReadChannel(fanNumber).Local;
        }

        public EcData ReadRaw(int fanNumber)
        {
            return ReadChannel(fanNumber);
        }

        public int GetDutyPercent(int fanNumber)
        {
            int raw = ReadChannel(fanNumber).FanDuty;
            return Clamp((int)Math.Round(raw * 100.0 / 255.0), 0, 100);
        }

        public int GetCpuRpm()
        {
            return ConvertRawRpm(_getCpuFanRpmRaw == null ? 0 : _getCpuFanRpmRaw());
        }

        public int GetGpuRpm()
        {
            return ConvertRawRpm(_getGpuFanRpmRaw == null ? 0 : _getGpuFanRpmRaw());
        }

        public void SetFanPercent(int fanNumber, int powerPercent)
        {
            ThrowIfDisposed();
            int safePercent = Clamp(powerPercent, 0, 100);
            int rawDuty = safePercent * 255 / 100;
            _setFanDuty(fanNumber, rawDuty);
        }

        public void SetFanAuto(int fanNumber)
        {
            ThrowIfDisposed();
            _setFanDutyAuto(fanNumber);
        }

        public void RestoreAllAuto()
        {
            if (_disposed || _setFanDutyAuto == null)
            {
                return;
            }

            // X15 AT 23 uses channel 1 for CPU and channel 2 for GPU.
            // Extra calls are deliberately avoided because unknown EC channels should not be touched.
            SafeAuto(1);
            SafeAuto(2);
        }

        private void SafeAuto(int channel)
        {
            try
            {
                _setFanDutyAuto(channel);
            }
            catch
            {
                // Best effort during fail-safe cleanup.
            }
        }

        private static int ConvertRawRpm(int raw)
        {
            if (raw <= 0)
            {
                return 0;
            }

            // Matches the conversion used by the original Brz application.
            return 2162688 / raw;
        }

        private T GetDelegate<T>(string exportName) where T : class
        {
            IntPtr proc = NativeMethods.GetProcAddress(_module, exportName);
            if (proc == IntPtr.Zero)
            {
                throw new MissingMethodException("ClevoEcInfo.dll export not found: " + exportName);
            }

            return (T)(object)Marshal.GetDelegateForFunctionPointer(proc, typeof(T));
        }

        private T TryGetDelegate<T>(string exportName) where T : class
        {
            IntPtr proc = NativeMethods.GetProcAddress(_module, exportName);
            if (proc == IntPtr.Zero)
            {
                return null;
            }

            return (T)(object)Marshal.GetDelegateForFunctionPointer(proc, typeof(T));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("ClevoEcInfo");
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_module != IntPtr.Zero)
            {
                NativeMethods.FreeLibrary(_module);
                _module = IntPtr.Zero;
            }

            _initIo = null;
            _getTempFanDuty = null;
            _setFanDuty = null;
            _setFanDutyAuto = null;
            _getFanCount = null;
            _getCpuFanRpmRaw = null;
            _getGpuFanRpmRaw = null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool InitIoDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate EcData GetTempFanDutyDelegate(int fanNumber);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetFanDutyDelegate(int fanNumber, int rawDuty);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetFanDutyAutoDelegate(int fanNumber);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetIntDelegate();

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr LoadLibrary(string path);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
            internal static extern IntPtr GetProcAddress(IntPtr module, string exportName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool FreeLibrary(IntPtr module);
        }
    }
}
