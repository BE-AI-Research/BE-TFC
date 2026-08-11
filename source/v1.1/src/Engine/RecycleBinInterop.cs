using System.Runtime.InteropServices;

namespace BETFC.Engine;

/// <summary>
/// shell32 interop for the Recycle Bin. The bin is not a plain directory
/// ($Recycle.Bin has per-SID subfolders with metadata); the shell API is the
/// only correct way to size and empty it without corrupting bin state.
/// </summary>
internal static class RecycleBinInterop
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI   = 0x2;
    private const uint SHERB_NOSOUND        = 0x4;

    /// <summary>Total bytes and item count across all drives' bins. (0,0) on failure.</summary>
    public static (long bytes, long items) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0
            ? (info.i64Size, info.i64NumItems)
            : (0, 0);
    }

    /// <summary>Empty the bin on all drives. Returns true on success or already-empty.</summary>
    public static bool Empty()
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // Any non-negative HRESULT (S_OK, S_FALSE/"already empty") is success.
        return hr >= 0;
    }
}
