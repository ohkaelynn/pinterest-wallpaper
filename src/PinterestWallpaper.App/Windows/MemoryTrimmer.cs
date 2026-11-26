using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace PinterestWallpaper.App.Windows;

public static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minSize, nint maxSize);

    public static void TrimIdleMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, -1, -1);
        }
        catch
        {
            // Best-effort working set trim.
        }
    }
}
