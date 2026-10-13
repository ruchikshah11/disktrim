using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace DiskTrim.Core;

internal static class Native
{
    // ---- Recycle Bin -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;
    const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Size and item count of the Recycle Bin on all drives.</summary>
    public static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin of all drives. Returns the HRESULT.</summary>
    public static int EmptyRecycleBin() =>
        SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    /// <summary>Moves files or folders to the Recycle Bin. Returns 0 on success, otherwise the shell error code.</summary>
    public static int MoveToRecycleBin(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return 0;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        int rc = SHFileOperation(ref op);
        return rc == 0 && op.fAnyOperationsAborted ? -1 : rc;
    }

    // ---- File identity (hard links) ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>Volume + file index: identical for every hard link of the same file. Null if unreadable.</summary>
    public static string? FileId(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(h, out var i)
                ? $"{i.VolumeSerialNumber:X8}-{i.FileIndexHigh:X8}{i.FileIndexLow:X8}"
                : null;
        }
        catch { return null; }
    }

    // ---- Restart Manager ("which program has this file open?") --------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    const int ERROR_MORE_DATA = 234;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    /// <summary>Processes that have any of these files open (the API installers use).</summary>
    public static List<(int Pid, string AppName)> ProcessesLocking(string[] files)
    {
        var result = new List<(int, string)>();
        if (files.Length == 0) return result;
        if (RmStartSession(out uint session, 0, new StringBuilder(64)) != 0) return result;
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return result;
            uint needed, count = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == ERROR_MORE_DATA && needed > 0)
            {
                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, ref reasons) == 0)
                    for (int i = 0; i < count; i++)
                        result.Add((infos[i].Process.dwProcessId, infos[i].strAppName));
            }
        }
        finally { RmEndSession(session); }
        return result;
    }
}
