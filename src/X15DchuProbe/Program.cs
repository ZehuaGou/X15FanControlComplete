using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace X15DchuProbe
{
    /// <summary>
    /// READ-ONLY DCHU AppSettings probe for the Control Center mechanism audit.
    ///
    /// Loads InsydeDCHU.dll (staged from WindowsApps to bypass ACL), then only
    /// calls the exported ReadAppSettings(page, offset, len, buffer). It NEVER
    /// calls SetDCHU_Data / WriteAppSettings / Apply / Restore.
    ///
    /// Reads:
    ///   page1 offset1 : OEM power mode byte (0-4)
    ///   page6 offset33 len4 : CPU PL1 (W)   [mechanism audit: A-level readback]
    ///   page6 offset37 len4 : CPU PL2 (W)   [mechanism audit: A-level readback]
    ///   page6 offset41 len4 : CPU Tau (s)   [mechanism audit: A-level readback]
    ///   page5 offset20 len2 : GPU CoreOC stored value
    ///   page5 offset22 len2 : GPU MEMOC stored value
    ///   page5 offset6  len7 : GPU info block (total/base/driver)
    /// Output: one line per field with hex + decimal, plus exit code.
    ///
    /// E0 扩展（2026-08-03，实机验收前收尾）：PL1/PL2/Tau 读取使用与 mode
    /// 字节完全相同的 ReadAppSettings 只读导出，偏移来自机制审计第 2.2 节
    /// （GetPowerLimit1/2CurrentValue / GetPowerLimitTimeValue 的静态反编译
    /// 调用路径，证据等级 A）。本工具源码经 static scan 确认不含任何
    /// Set/Write/Apply/Restore 调用。
    /// </summary>
    internal static class Program
    {
        private static IntPtr _module;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string dllSource = args.Length > 0 && args[0] == "--dll" && args.Length > 1
                ? args[1]
                : @"C:\Program Files\WindowsApps\CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0\FnKey\CPUOC\InsydeDCHU.dll";

            try
            {
                string staged = StageDll(dllSource);
                _module = LoadLibrary(staged);
                if (_module == IntPtr.Zero)
                {
                    Console.WriteLine("ERROR loadlibrary_failed last_error=" + Marshal.GetLastWin32Error());
                    return 2;
                }

                // mode byte: page1 offset1 len1
                int mode = ReadAppSettingsInt(1, 1, 1);
                Console.WriteLine("MODE_BYTE=" + mode + " (hex 0x" + (mode < 0 ? "FFFFFFFF" : mode.ToString("X2")) + ")");

                // CPU PL1/PL2/Tau（只读；page6 offset33/37/41 len4）
                int pl1 = ReadAppSettingsInt(6, 33, 4);
                Console.WriteLine("CPU_PL1_WATTS=" + pl1 + " (hex 0x" + (pl1 < 0 ? "FFFFFFFF" : pl1.ToString("X8")) + ")");
                int pl2 = ReadAppSettingsInt(6, 37, 4);
                Console.WriteLine("CPU_PL2_WATTS=" + pl2 + " (hex 0x" + (pl2 < 0 ? "FFFFFFFF" : pl2.ToString("X8")) + ")");
                int tau = ReadAppSettingsInt(6, 41, 4);
                Console.WriteLine("CPU_TAU_SECONDS=" + tau + " (hex 0x" + (tau < 0 ? "FFFFFFFF" : tau.ToString("X8")) + ")");

                // GPU CoreOC: page5 offset20 len2
                int coreOC = ReadAppSettingsInt(5, 20, 2);
                Console.WriteLine("PAGE5_OFF20_COREOC=" + coreOC + " (hex 0x" + (coreOC < 0 ? "FFFFFFFF" : coreOC.ToString("X4")) + ")");

                // GPU MEMOC: page5 offset22 len2
                int memOC = ReadAppSettingsInt(5, 22, 2);
                Console.WriteLine("PAGE5_OFF22_MEMOC=" + memOC + " (hex 0x" + (memOC < 0 ? "FFFFFFFF" : memOC.ToString("X4")) + ")");

                // GPU info block: page5 offset6 len7
                byte[] info = ReadAppSettingsBuffer(5, 6, 7);
                Console.WriteLine("PAGE5_OFF6_INFO=" + BitConverter.ToString(info).Replace("-", ""));

                FreeLibrary(_module);
                Console.WriteLine("DONE");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message);
                if (_module != IntPtr.Zero) FreeLibrary(_module);
                return 1;
            }
        }

        private static string StageDll(string source)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("InsydeDCHU.dll not found", source);
            string nativeDir = Path.Combine(Path.GetTempPath(), "x15-dchu-probe");
            Directory.CreateDirectory(nativeDir);
            string staged = Path.Combine(nativeDir, "InsydeDCHU.dll");
            if (File.Exists(staged))
            {
                try { File.Delete(staged); } catch { }
            }
            File.Copy(source, staged, true);
            return staged;
        }

        private static int ReadAppSettingsInt(int page, int offset, int length)
        {
            byte[] buf = new byte[length];
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int rc = ReadAppSettings(page, offset, length, h.AddrOfPinnedObject());
                // NOTE: InsydeDCHU ReadAppSettings returns a status code (e.g.
                // 4096) that is NOT a failure indicator on this platform; the
                // buffer is still populated. Report both rc and data.
                int val = 0;
                for (int i = 0; i < length && i < 4; i++) val |= buf[i] << (8 * i);
                Console.WriteLine("RC=" + rc);
                return val;
            }
            finally { h.Free(); }
        }

        private static byte[] ReadAppSettingsBuffer(int page, int offset, int length)
        {
            byte[] buf = new byte[length];
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                ReadAppSettings(page, offset, length, h.AddrOfPinnedObject());
                return buf;
            }
            finally { h.Free(); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        // ReadAppSettings(page, offset, length, unsigned char* buffer) -> int
        private static int ReadAppSettings(int page, int offset, int length, IntPtr buffer)
        {
            IntPtr proc = GetProcAddress(_module, "ReadAppSettings");
            if (proc == IntPtr.Zero)
                throw new MissingMethodException("ReadAppSettings export not found");
            ReadAppSettingsDelegate del = (ReadAppSettingsDelegate)Marshal.GetDelegateForFunctionPointer(proc, typeof(ReadAppSettingsDelegate));
            return del(page, offset, length, buffer);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadAppSettingsDelegate(int page, int offset, int length, IntPtr buffer);
    }
}
