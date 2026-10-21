using System.Runtime.InteropServices;

namespace UltraLauncher.Core.Settings;

public static partial class SystemMemory
{
    public static int TotalPhysicalMb() => Query(out var total, out _) ? (int)(total / (1024 * 1024)) : 0;

    public static int AvailablePhysicalMb() => Query(out _, out var avail) ? (int)(avail / (1024 * 1024)) : 0;

    private static bool Query(out ulong total, out ulong available)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            total = status.TotalPhys;
            available = status.AvailPhys;
            return true;
        }
        total = available = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
