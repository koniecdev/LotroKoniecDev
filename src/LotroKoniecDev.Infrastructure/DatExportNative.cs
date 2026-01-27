using System.Runtime.InteropServices;

namespace LotroKoniecDev.Infrastructure;

/// <summary>
/// P/Invoke wrapper for the native datexport.dll library.
/// This class provides low-level interop with the native LOTRO DAT file library.
/// </summary>
internal static class DatExportNative
{
    private const string DllName = "datexport.dll";

    /// <summary>
    /// Flags for opening DAT files: Read (2) + Write (128) = 130.
    /// </summary>
    public const uint OpenFlagsReadWrite = 130;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int OpenDatFileEx2(
        int handle,
        [MarshalAs(UnmanagedType.LPStr)] string fileName,
        uint flags,
        out int didMasterMap,
        out int blockSize,
        out int vnumDatFile,
        out int vnumGameData,
        out uint datFileId,
        [MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] datIdStamp,
        [MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] firstIterGuid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNumSubfiles(int handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileSizes(
        int handle,
        [MarshalAs(UnmanagedType.LPArray)] int[] fileIds,
        [MarshalAs(UnmanagedType.LPArray)] int[] sizes,
        [MarshalAs(UnmanagedType.LPArray)] int[] iterations,
        int offset,
        int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSubfileVersion(int handle, int fileId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,
        out int version);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PurgeSubfileData(int handle, int fileId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PutSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,
        int size,
        int version,
        int iteration,
        byte unknown2);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Flush(int handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseDatFile(int handle);
}
