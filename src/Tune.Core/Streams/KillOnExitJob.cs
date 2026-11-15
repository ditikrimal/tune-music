using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tune.Core.Streams;

/// <summary>A Windows job object that kills its processes when Tune exits — even if Tune itself is killed.</summary>
internal static class KillOnExitJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    private static readonly nint Job = Create();

    public static void Add(Process p)
    {
        if (Job != 0) _ = AssignProcessToJobObject(Job, p.Handle);
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var job = CreateJobObject(0, null);
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        _ = SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>());
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
}
