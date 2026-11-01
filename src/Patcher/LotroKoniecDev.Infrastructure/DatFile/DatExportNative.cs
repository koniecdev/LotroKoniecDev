using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

// The loader looks for the native DLL only next to the assembly and in the folders the OS considers
// safe, never in the current folder or %PATH%. Otherwise a datexport.dll someone dropped there would
// load with admin rights through the elevated .bat wrappers.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace LotroKoniecDev.Infrastructure.DatFile;

internal static partial class DatExportNative
{
    private const string DllName = "datexport.dll";

    /// <summary>Open flags for read-write access: 2 | 128.</summary>
    public const uint OpenFlagsReadWrite = 130;

    /// <summary>Open flags for read-only access: 2 | ReadOnly (4).</summary>
    // Bit 0x4 is the only flag that changes what access the native library asks the OS for. With it,
    // datexport.dll opens the file as GENERIC_READ | FILE_SHARE_READ. Without it, every open asks for
    // GENERIC_READ | GENERIC_WRITE and fails on a file the caller cannot write. The 2 bit, which both
    // constants have, selects nothing about access (#629).
    // Measured: docs/knowledge-base/datexport-readonly-open-2026-08-07.md.
    public const uint OpenFlagsRead = 6;

    /// <summary>
    /// Opens a DAT file and reads its metadata.
    /// </summary>
    /// <param name="datFileHandle">The handle we ask for. Later calls use it to name this file.</param>
    /// <param name="flags">Read-only or read-write. See <see cref="OpenFlagsRead"/> and <see cref="OpenFlagsReadWrite"/>.</param>
    /// <param name="didMasterMap">Non-zero when the master map was set up.</param>
    /// <param name="blockSize">The block size the file stores data in.</param>
    /// <param name="vnumDatFile">The version number of the DAT file format.</param>
    /// <param name="vnumGameData">The version number of the game data in the file.</param>
    /// <param name="datIdStamp">The file's id stamp, as bytes.</param>
    /// <param name="firstIterGuid">The GUID of the file's first iteration, as bytes.</param>
    /// <returns>The handle of the opened file. Any other value than the one asked for means failure.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int OpenDatFileEx2(
        int datFileHandle,
        [MarshalAs(UnmanagedType.LPStr)] string fileName,
        uint flags,
        out int didMasterMap,
        out int blockSize,
        out int vnumDatFile,
        out int vnumGameData,
        out uint datFileId,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] datIdStamp,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] firstIterGuid);


    /// <returns>
    /// How many subfiles the DAT holds. Zero or less means the file is empty or the read failed.
    /// </returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetNumSubfiles(int datFileHandle);

    /// <summary>
    /// Reads the id, size and iteration number of a range of subfiles.
    /// </summary>
    /// <param name="fileIds">Receives the subfile ids.</param>
    /// <param name="sizes">Receives the subfile sizes, in bytes.</param>
    /// <param name="iterations">Receives the subfile iteration numbers.</param>
    /// <param name="offset">Where to start in the DAT's own subfile listing.</param>
    /// <param name="count">How many subfiles to read from that point.</param>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void GetSubfileSizes(
        int datFileHandle,
        [Out, MarshalAs(UnmanagedType.LPArray)]
        int[] fileIds,
        [Out, MarshalAs(UnmanagedType.LPArray)]
        int[] sizes,
        [Out, MarshalAs(UnmanagedType.LPArray)]
        int[] iterations,
        int offset,
        int count);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetSubfileVersion(int datFileHandle, int fileId);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void GetSubfileData(
        int datFileHandle,
        int fileId,
        IntPtr buffer,
        int unknown,
        out int version);

    /// <summary>
    /// Deletes the content of one subfile. The subfile's metadata entry stays.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int PurgeSubfileData(int datFileHandle, int fileId);

    /// <summary>
    /// Replaces the content of one subfile and updates its size, version and iteration.
    /// </summary>
    /// <param name="unknown">We do not know what the native library does with this.</param>
    /// <param name="size">The size of the new data, in bytes.</param>
    /// <param name="version">The version to give the subfile after the write.</param>
    /// <param name="iteration">The iteration number to give the subfile after the write.</param>
    /// <param name="unknown2">We do not know what the native library does with this either.</param>
    /// <returns>Non-zero on success, zero on failure.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int PutSubfileData(
        int datFileHandle,
        int fileId,
        IntPtr buffer,
        int unknown,
        int size,
        int version,
        int iteration,
        byte unknown2);

    /// <summary>Writes everything still buffered for this DAT file to disk.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Flush(int datFileHandle);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void CloseDatFile(int datFileHandle);
}
