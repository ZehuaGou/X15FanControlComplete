using System;
using System.Runtime.InteropServices;
using System.Text;

namespace X15FanCore.Control
{
    /// <summary>
    /// NVML shim 契约（返回码约定见 NvmlReturnCodes）。
    /// 独立于具体实现定义于此文件，使 x64 验收工具可直接链接源码，
    /// 无需引用 x86 构建的 X15FanCore.dll（本机 nvml.dll 仅 64 位）。
    /// </summary>
    public interface INvmlShim
    {
        bool IsAvailable();
        /// <summary>最近一次调用的原始 nvmlReturn_t（0 = NVML_SUCCESS）。</summary>
        int LastNvmlReturnCode { get; }
        /// <summary>初始化详情（含原始返回码），供日志/报告输出。</summary>
        string InitDetail { get; }
        int GetPowerManagementLimit(out int limitMilliWatts);
        int GetDefaultPowerManagementLimit(out int defaultLimitMilliWatts);
        int GetMinPowerManagementLimit(out int minLimitMilliWatts);
        int GetMaxPowerManagementLimit(out int maxLimitMilliWatts);
        int GetGpuIdentity(out string identity);
        int SetPowerManagementLimit(int limitMilliWatts);
    }

    /// <summary>
    /// NVIDIA NVML 返回码（nvmlReturn_t）常量。原始返回码必须保留并输出，
    /// 不得把 NOT_SUPPORTED / NO_PERMISSION / GPU_IS_LOST / UNKNOWN 混成
    /// 普通失败。数值与 nvml.h nvmlReturn_enum 一致：
    /// NOT_SUPPORTED=3 / NO_PERMISSION=4 / GPU_IS_LOST=15 / UNKNOWN=999。
    /// </summary>
    public static class NvmlReturnCodes
    {
        public const int Success = 0;
        public const int ErrorUninitialized = 1;
        public const int ErrorInvalidArgument = 2;
        public const int ErrorNotSupported = 3;
        public const int ErrorNoPermission = 4;
        public const int ErrorAlreadyInitialized = 5;
        public const int ErrorNotFound = 6;
        public const int ErrorInsufficientSize = 7;
        public const int ErrorInsufficientPower = 8;
        public const int ErrorDriverNotLoaded = 9;
        public const int ErrorTimeout = 10;
        public const int ErrorIrqIssue = 11;
        public const int ErrorLibraryNotFound = 12;
        public const int ErrorFunctionNotFound = 13;
        public const int ErrorCorruptedInforom = 14;
        public const int ErrorGpuIsLost = 15;
        public const int ErrorResetRequired = 16;
        public const int ErrorOperatingSystem = 17;
        public const int ErrorLibRmVersionMismatch = 18;
        public const int ErrorInUse = 19;
        public const int ErrorMemory = 20;
        public const int ErrorNoData = 21;
        public const int ErrorVgpusEexist = 22;
        public const int ErrorVgpuNotFound = 23;
        public const int ErrorInvalidDevice = 24;
        public const int ErrorInvalidHandle = 25;
        public const int ErrorUnknown = 999;

        public static string Describe(int code)
        {
            switch (code)
            {
                case Success: return "NVML_SUCCESS";
                case ErrorUninitialized: return "NVML_ERROR_UNINITIALIZED";
                case ErrorInvalidArgument: return "NVML_ERROR_INVALID_ARGUMENT";
                case ErrorNotSupported: return "NVML_ERROR_NOT_SUPPORTED";
                case ErrorNoPermission: return "NVML_ERROR_NO_PERMISSION";
                case ErrorAlreadyInitialized: return "NVML_ERROR_ALREADY_INITIALIZED";
                case ErrorNotFound: return "NVML_ERROR_NOT_FOUND";
                case ErrorInsufficientSize: return "NVML_ERROR_INSUFFICIENT_SIZE";
                case ErrorInsufficientPower: return "NVML_ERROR_INSUFFICIENT_POWER";
                case ErrorDriverNotLoaded: return "NVML_ERROR_DRIVER_NOT_LOADED";
                case ErrorTimeout: return "NVML_ERROR_TIMEOUT";
                case ErrorIrqIssue: return "NVML_ERROR_IRQ_ISSUE";
                case ErrorLibraryNotFound: return "NVML_ERROR_LIBRARY_NOT_FOUND";
                case ErrorFunctionNotFound: return "NVML_ERROR_FUNCTION_NOT_FOUND";
                case ErrorCorruptedInforom: return "NVML_ERROR_CORRUPTED_INFOROM";
                case ErrorGpuIsLost: return "NVML_ERROR_GPU_IS_LOST";
                case ErrorResetRequired: return "NVML_ERROR_RESET_REQUIRED";
                case ErrorOperatingSystem: return "NVML_ERROR_OPERATING_SYSTEM";
                case ErrorLibRmVersionMismatch: return "NVML_ERROR_LIB_RM_VERSION_MISMATCH";
                case ErrorInUse: return "NVML_ERROR_IN_USE";
                case ErrorMemory: return "NVML_ERROR_MEMORY";
                case ErrorNoData: return "NVML_ERROR_NO_DATA";
                case ErrorVgpusEexist: return "NVML_ERROR_VGPUS_EEXIST";
                case ErrorVgpuNotFound: return "NVML_ERROR_VGPU_NOT_FOUND";
                case ErrorInvalidDevice: return "NVML_ERROR_INVALID_DEVICE";
                case ErrorInvalidHandle: return "NVML_ERROR_INVALID_HANDLE";
                case ErrorUnknown: return "NVML_ERROR_UNKNOWN";
                default: return "nvmlReturn_t=" + code;
            }
        }
    }

    /// <summary>
    /// 真实 NVML shim：直接调用本机 nvml.dll。
    ///
    /// 安全约束：
    /// - 按 GPU UUID 获取设备句柄，绝不按易变化的序号盲选；
    /// - 每次调用保存原始 nvmlReturn_t（LastNvmlReturnCode），调用方必须
    ///   原样输出，不得把 NOT_SUPPORTED / NO_PERMISSION / GPU_IS_LOST /
    ///   UNKNOWN 混成普通失败；
    /// - 默认写禁用：SetPowerManagementLimit 在 EnableWrites() 之前一律
    ///   返回 NVML_ERROR_NO_PERMISSION 且绝不触碰硬件；
    /// - 不用锁频 / P-State / OC 假装 W 级功耗控制（本 shim 只有 power
    ///   limit 原语）。
    /// </summary>
    public sealed class NvmlShim : INvmlShim, IDisposable
    {
        private readonly object _lock = new object();
        private readonly string _requestedUuid;
        private IntPtr _device = IntPtr.Zero;
        private bool _initialized;
        private bool _writeEnabled;              // 默认 false：写禁用
        private int _lastReturnCode = NvmlReturnCodes.ErrorUninitialized;
        private string _identity = string.Empty;
        private string _initFailureDetail = string.Empty;
        private bool _initFailureIsPermanent;    // 已尝试且失败，不再重试

        public NvmlShim(string gpuUuid = null)
        {
            _requestedUuid = gpuUuid;
        }

        /// <summary>最近一次 NVML 调用的原始 nvmlReturn_t（0 = NVML_SUCCESS）。</summary>
        public int LastNvmlReturnCode { get { return _lastReturnCode; } }

        /// <summary>写授权：Phase B 验收批准后由调用方显式开启一次。</summary>
        public void EnableWrites()
        {
            lock (_lock)
            {
                _writeEnabled = true;
            }
        }

        public bool WritesEnabled { get { return _writeEnabled; } }

        /// <summary>NVML 是否可用（可初始化且能定位到 GPU）。</summary>
        public bool IsAvailable()
        {
            lock (_lock)
            {
                return EnsureInitialized() == NvmlReturnCodes.Success;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                ShutdownNvml();
            }
        }

        /// <summary>在无 UUID 参数时枚举设备，返回第一个可枚举 GPU 的 UUID。</summary>
        public static string DiscoverFirstGpuUuid(out string detail)
        {
            IntPtr device = IntPtr.Zero;
            try
            {
                int rc = NativeMethods.nvmlInit_v2();
                if (rc != NvmlReturnCodes.Success)
                {
                    detail = "nvmlInit_v2 返回 " + NvmlReturnCodes.Describe(rc);
                    return null;
                }
                for (uint i = 0; i < 16; i++)
                {
                    rc = NativeMethods.nvmlDeviceGetHandleByIndex_v2(i, out device);
                    if (rc == NvmlReturnCodes.ErrorInvalidArgument)
                        break;   // 索引越界：不再继续
                    if (rc != NvmlReturnCodes.Success)
                        continue;
                    StringBuilder sb = new StringBuilder(256);
                    if (NativeMethods.nvmlDeviceGetUUID(device, sb, 256) == NvmlReturnCodes.Success)
                    {
                        detail = "index=" + i;
                        return sb.ToString();
                    }
                }
                detail = "未在 index 0..15 找到可枚举 GPU";
                return null;
            }
            catch (DllNotFoundException)
            {
                detail = "nvml.dll 未找到（本机驱动未安装 NVML）";
                return null;
            }
            finally
            {
                NativeMethods.nvmlShutdown();
            }
        }

        public int GetPowerManagementLimit(out int limitMilliWatts)
        {
            lock (_lock)
            {
                limitMilliWatts = 0;
                int rc = EnsureInitialized();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    return rc;
                }
                uint limit;
                rc = NativeMethods.nvmlDeviceGetPowerManagementLimit(_device, out limit);
                _lastReturnCode = rc;
                if (rc == NvmlReturnCodes.Success)
                    limitMilliWatts = (int)limit;
                return rc;
            }
        }

        public int GetDefaultPowerManagementLimit(out int defaultLimitMilliWatts)
        {
            lock (_lock)
            {
                defaultLimitMilliWatts = 0;
                int rc = EnsureInitialized();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    return rc;
                }
                uint limit;
                rc = NativeMethods.nvmlDeviceGetPowerManagementDefaultLimit(_device, out limit);
                _lastReturnCode = rc;
                if (rc == NvmlReturnCodes.Success)
                    defaultLimitMilliWatts = (int)limit;
                return rc;
            }
        }

        public int GetMinPowerManagementLimit(out int minLimitMilliWatts)
        {
            lock (_lock)
            {
                minLimitMilliWatts = 0;
                int rc = EnsureInitialized();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    return rc;
                }
                uint min, max;
                rc = NativeMethods.nvmlDeviceGetPowerManagementLimitConstraints(_device, out min, out max);
                _lastReturnCode = rc;
                if (rc == NvmlReturnCodes.Success)
                    minLimitMilliWatts = (int)min;
                return rc;
            }
        }

        public int GetMaxPowerManagementLimit(out int maxLimitMilliWatts)
        {
            lock (_lock)
            {
                maxLimitMilliWatts = 0;
                int rc = EnsureInitialized();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    return rc;
                }
                uint min, max;
                rc = NativeMethods.nvmlDeviceGetPowerManagementLimitConstraints(_device, out min, out max);
                _lastReturnCode = rc;
                if (rc == NvmlReturnCodes.Success)
                    maxLimitMilliWatts = (int)max;
                return rc;
            }
        }

        public int GetGpuIdentity(out string identity)
        {
            lock (_lock)
            {
                identity = string.Empty;
                int rc = EnsureInitialized();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    return rc;
                }
                identity = _identity;   // 初始化时已按 UUID 缓存
                _lastReturnCode = NvmlReturnCodes.Success;
                return NvmlReturnCodes.Success;
            }
        }

        public int SetPowerManagementLimit(int limitMilliWatts)
        {
            lock (_lock)
            {
                if (!_writeEnabled)
                {
                    // 本地写禁用：不得触碰硬件。返回 NO_PERMISSION 并保存原码。
                    _lastReturnCode = NvmlReturnCodes.ErrorNoPermission;
                    return NvmlReturnCodes.ErrorNoPermission;
                }
                int rc = EnsureInitialized();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    return rc;
                }
                rc = NativeMethods.nvmlDeviceSetPowerManagementLimit(_device, (uint)limitMilliWatts);
                _lastReturnCode = rc;
                return rc;
            }
        }

        /// <summary>初始化详情（供日志/报告输出，含原始返回码）。</summary>
        public string InitDetail
        {
            get
            {
                if (_initFailureIsPermanent)
                    return "NVML 初始化失败：" + _initFailureDetail;
                if (!_initialized)
                    return "NVML 尚未初始化";
                return "NVML 已初始化，device=" + _device + "，identity=" + _identity;
            }
        }

        private int EnsureInitialized()
        {
            if (_initialized)
                return NvmlReturnCodes.Success;
            if (_initFailureIsPermanent)
                return _lastReturnCode;

            try
            {
                int rc = NativeMethods.nvmlInit_v2();
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    _initFailureDetail = "nvmlInit_v2 返回 " + NvmlReturnCodes.Describe(rc);
                    _initFailureIsPermanent = true;
                    return rc;
                }

                // 按 UUID 定位设备：优先请求的 UUID；否则枚举发现（绝不用
                // 易变化的序号盲选）。
                IntPtr handle = IntPtr.Zero;
                if (!string.IsNullOrEmpty(_requestedUuid))
                {
                    rc = NativeMethods.nvmlDeviceGetHandleByUUID(_requestedUuid, out handle);
                    if (rc != NvmlReturnCodes.Success)
                    {
                        _lastReturnCode = rc;
                        _initFailureDetail = "nvmlDeviceGetHandleByUUID(" + _requestedUuid + ") 返回 "
                            + NvmlReturnCodes.Describe(rc);
                        _initFailureIsPermanent = true;
                        NativeMethods.nvmlShutdown();
                        return rc;
                    }
                }
                else
                {
                    for (uint i = 0; i < 16; i++)
                    {
                        rc = NativeMethods.nvmlDeviceGetHandleByIndex_v2(i, out handle);
                        if (rc == NvmlReturnCodes.ErrorInvalidArgument)
                        {
                            handle = IntPtr.Zero;
                            break;   // 索引越界：不再继续
                        }
                        if (rc != NvmlReturnCodes.Success)
                            continue;
                        break;
                    }
                    if (handle == IntPtr.Zero)
                    {
                        _lastReturnCode = NvmlReturnCodes.ErrorInvalidArgument;
                        _initFailureDetail = "未找到任何 GPU 设备";
                        _initFailureIsPermanent = true;
                        NativeMethods.nvmlShutdown();
                        return _lastReturnCode;
                    }
                }

                // 校验并缓存 identity（UUID 文本）。
                StringBuilder sb = new StringBuilder(256);
                rc = NativeMethods.nvmlDeviceGetUUID(handle, sb, 256);
                if (rc != NvmlReturnCodes.Success)
                {
                    _lastReturnCode = rc;
                    _initFailureDetail = "nvmlDeviceGetUUID 返回 " + NvmlReturnCodes.Describe(rc);
                    _initFailureIsPermanent = true;
                    NativeMethods.nvmlShutdown();
                    return rc;
                }
                _identity = sb.ToString();
                _device = handle;
                _initialized = true;
                _lastReturnCode = NvmlReturnCodes.Success;
                return NvmlReturnCodes.Success;
            }
            catch (DllNotFoundException)
            {
                _lastReturnCode = NvmlReturnCodes.ErrorUninitialized;
                _initFailureDetail = "nvml.dll 未找到（驱动未安装 NVML）";
                _initFailureIsPermanent = true;
                return _lastReturnCode;
            }
            catch (EntryPointNotFoundException)
            {
                _lastReturnCode = NvmlReturnCodes.ErrorUninitialized;
                _initFailureDetail = "nvml.dll 缺少所需入口点（版本过旧）";
                _initFailureIsPermanent = true;
                return _lastReturnCode;
            }
        }

        private void ShutdownNvml()
        {
            if (_initialized)
            {
                try
                {
                    NativeMethods.nvmlShutdown();
                }
                catch (Exception)
                {
                    // 释放路径不抛异常。
                }
                _initialized = false;
                _device = IntPtr.Zero;
            }
        }

        private static class NativeMethods
        {
            [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlInit_v2();

            [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlShutdown();

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByUUID", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceGetHandleByUUID(
                [MarshalAs(UnmanagedType.LPStr)] string uuid,
                out IntPtr device);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUUID", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceGetUUID(IntPtr device, StringBuilder uuid, uint length);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint defaultLimit);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minLimit, out uint maxLimit);

            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint limit);
        }
    }
}
