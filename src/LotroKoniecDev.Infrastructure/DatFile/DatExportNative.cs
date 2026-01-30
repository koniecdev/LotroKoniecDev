using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace LotroKoniecDev.Infrastructure.DatFile;

/// <summary>
/// P/Invoke wrapper for the native datexport.dll library.
/// This class provides low-level interop with the native LOTRO DAT file library.
/// </summary>
internal static partial class DatExportNative
{
    private const string DllName = "datexport.dll";

    /// <summary>
    /// Flags for opening DAT files: Read (2) + Write (128) = 130.
    /// </summary>
    public const uint OpenFlagsReadWrite = 130;

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int OpenDatFileEx2(
        int handle,
        [MarshalAs(UnmanagedType.LPStr)] string fileName,
        uint flags,
        out int didMasterMap,
        out int blockSize,
        out int vnumDatFile,
        out int vnumGameData,
        out uint datFileId,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] datIdStamp,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] firstIterGuid);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetNumSubfiles(int handle);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void GetSubfileSizes(
        int handle,
        [Out, MarshalAs(UnmanagedType.LPArray)] int[] fileIds,
        [Out, MarshalAs(UnmanagedType.LPArray)] int[] sizes,
        [Out, MarshalAs(UnmanagedType.LPArray)] int[] iterations,
        int offset,
        int count);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetSubfileVersion(int handle, int fileId);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void GetSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,
        out int version);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int PurgeSubfileData(int handle, int fileId);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int PutSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,
        int size,
        int version,
        int iteration,
        byte unknown2);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Flush(int handle);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void CloseDatFile(int handle);
}
