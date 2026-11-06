using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CloudFlow.Azure.Identity.AzureCli;

/// <summary>
/// 把外部的 Azure CLI 子进程树绑定到 Windows Job Object，并设置
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>：Job 句柄一旦关闭（含持有它的进程被强杀、
/// 崩溃或断电级退出），内核即回收整个子树。
///
/// 必要性：<see cref="AzureCliProcessRunner"/> 在超时/取消时会显式终止进程树，
/// 但 CloudFlow 自身被强杀时不执行任何托管代码，无法自行清理，
/// 会遗留 `az login` 孤儿进程与悬挂的设备码会话。
///
/// 绑定是尽力而为的加固：若目标进程已处于不兼容的 Job 中导致绑定失败，
/// 不抛异常（CLI 调用本身必须照常进行），超时/取消的显式终止路径仍然有效。
/// </summary>
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    public static IntPtr TryAttach(Process process)
    {
        IntPtr jobHandle = IntPtr.Zero;
        try
        {
            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var limits = new JobObjectExtendedLimitInformationStructure
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStructure>();
            var limitsPointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, limitsPointer, fDeleteOld: false);
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, limitsPointer, (uint)size))
                {
                    CloseHandle(jobHandle);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(limitsPointer);
            }

            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                CloseHandle(jobHandle);
                return IntPtr.Zero;
            }

            return jobHandle;
        }
        catch
        {
            // 加固失败不得影响 CLI 调用；由调用方的超时/取消路径兜底
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
            }

            return IntPtr.Zero;
        }
    }

    public static void Close(IntPtr jobHandle)
    {
        if (jobHandle != IntPtr.Zero)
        {
            CloseHandle(jobHandle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStructure
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
