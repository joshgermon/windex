// This process's dedicated GPU memory, from the "GPU Process Memory" performance counters (the
// numbers Task Manager shows), for the lifecycle check.
using System.Runtime.InteropServices;

namespace TerminalLab;

internal static partial class GpuMemory
{
    private const uint PdhFmtLarge = 0x00000400;
    // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, status (+4 padding), 8-byte value.
    private const int ItemSize = 24, ValueOffset = 16;

    /// <summary>Dedicated GPU memory in use by this process across all adapters, in bytes; null when the counters are unavailable.</summary>
    public static long? DedicatedBytes()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return null;
        try
        {
            string path = $@"\GPU Process Memory(pid_{Environment.ProcessId}_*)\Dedicated Usage";
            if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var counter) != 0 || PdhCollectQueryData(query) != 0) return null;
            uint size = 0;
            PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, out _, IntPtr.Zero);
            if (size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, out uint count, buffer) != 0) return null;
                long total = 0;
                for (int i = 0; i < count; i++) total += Marshal.ReadInt64(buffer, i * ItemSize + ValueOffset);
                return total;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);
}
