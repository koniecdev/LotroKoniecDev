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

    /// <summary>
    /// Opens a specified DAT file with extended configurations and retrieves detailed metadata about the file.
    /// This method establishes a connection to a DAT file, preparing it for reading and writing operations along with metadata extraction.
    /// </summary>
    /// <param name="datFileHandle">
    /// The requested handle for the DAT file to be opened.
    /// This parameter is used to identify the file access context.
    /// </param>
    /// <param name="fileName">
    /// The path to the DAT file that needs to be opened.
    /// This must be a valid, accessible file path.
    /// </param>
    /// <param name="flags">
    /// A set of flags determining the mode in which the DAT file is accessed;
    /// for example, read-only or read-write operations.
    /// </param>
    /// <param name="didMasterMap">
    /// Outputs an integer indicating whether the master map of the DAT file was successfully initialized.
    /// </param>
    /// <param name="blockSize">
    /// Outputs the size of the block used for storing data in the DAT file.
    /// </param>
    /// <param name="vnumDatFile">
    /// Outputs the version number of the DAT file format being accessed.
    /// </param>
    /// <param name="vnumGameData">
    /// Outputs the version number of the game data stored in the DAT file.
    /// </param>
    /// <param name="datFileId">
    /// Outputs the unique identifier associated with the opened DAT file.
    /// </param>
    /// <param name="datIdStamp">
    /// Outputs an array of bytes representing the DAT file's unique identifier stamp.
    /// </param>
    /// <param name="firstIterGuid">
    /// Outputs an array of bytes representing the GUID associated with the first iteration of the DAT file.
    /// </param>
    /// <returns>
    /// An integer representing the handle of the opened DAT file if successful.
    /// A different value than the requested handle indicates failure.
    /// </returns>
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
    

    /// <summary>
    /// Retrieves the total number of subfiles present in the specified DAT file.
    /// This method queries the DAT file for the count of subfiles available for further operations.
    /// </summary>
    /// <param name="datFileHandle">The handle to the DAT file whose subfile count is being retrieved.</param>
    /// <returns>
    /// An integer representing the total number of subfiles within the specified DAT file.
    /// A return value of zero or less indicates either an empty file or an error in the read operation.
    /// </returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetNumSubfiles(int datFileHandle);

    /// <summary>
    /// Retrieves the sizes and iteration counts of subfiles within the DAT file.
    /// This method outputs information for a specified range of subfiles, including their sizes and iteration numbers.
    /// </summary>
    /// <param name="datFileHandle">The handle to the DAT file from which subfile information is being retrieved.</param>
    /// <param name="fileIds">An output array that receives the identifiers of the subfiles being queried.</param>
    /// <param name="sizes">An output array that receives the sizes of the corresponding subfiles, in bytes.</param>
    /// <param name="iterations">An output array that receives the iteration numbers of the corresponding subfiles.</param>
    /// <param name="offset">The starting index of the subfiles to retrieve information for, based on their internal listing.</param>
    /// <param name="count">The number of subfiles to include in the retrieval starting from the offset.</param>
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

    /// <summary>
    /// Retrieves the version of a specific subfile within the DAT file.
    /// This operation returns the version number associated with the specified subfile.
    /// </summary>
    /// <param name="datFileHandle">A handle to the DAT file from which the subfile version is retrieved.</param>
    /// <param name="fileId">The identifier of the subfile whose version is being queried.</param>
    /// <returns>The version number of the specified subfile.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetSubfileVersion(int datFileHandle, int fileId);

    /// <summary>
    /// Retrieves the data associated with a specific subfile within the DAT file.
    /// This operation extracts the subfile's content based on its identifier.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void GetSubfileData(
        int datFileHandle,
        int fileId,
        IntPtr buffer,
        int unknown,
        out int version);

    /// <summary>
    /// Removes the data associated with a specified subfile within the DAT file.
    /// This operation erases the subfile content but retains the subfile's metadata entry.
    /// </summary>
    /// <param name="datFileHandle">A handle representing the open DAT file containing the subfile.</param>
    /// <param name="fileId">The unique identifier of the subfile whose data is to be removed.</param>
    /// <returns>An integer representing the success or failure of the operation.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int PurgeSubfileData(int datFileHandle, int fileId);

    /// <summary>
    /// Writes data into a subfile within the DAT file. This operation replaces the content of the subfile
    /// with the specified data and updates its properties, including size, version, and iteration.
    /// </summary>
    /// <param name="datFileHandle">The handle of the open DAT file where the subfile is located.</param>
    /// <param name="fileId">The unique identifier of the subfile being written to.</param>
    /// <param name="buffer">A pointer to the memory containing the data to be written to the subfile.</param>
    /// <param name="unknown">An unknown parameter used for internal purposes by the native library.</param>
    /// <param name="size">The size, in bytes, of the new data being written to the subfile.</param>
    /// <param name="version">The version number to assign to the subfile after the write operation.</param>
    /// <param name="iteration">The iteration number to assign to the subfile after the write operation.</param>
    /// <param name="unknown2">An additional unknown parameter used for internal purposes by the native library.</param>
    /// <returns>An integer indicating the result of the operation. A non-zero value typically indicates success, while zero indicates failure.</returns>
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

    /// <summary>
    /// Forces the operating system to flush any pending writes buffered for the specified DAT file to disk.
    /// This ensures that all changes made are persisted and the file is in a consistent state.
    /// </summary>
    /// <param name="datFileHandle">The handle associated with the open DAT file that should be flushed.</param>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Flush(int datFileHandle);

    /// <summary>
    /// Closes an open DAT file associated with the specified handle.
    /// </summary>
    /// <param name="datFileHandle">The handle associated with the open DAT file that needs to be closed.</param>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void CloseDatFile(int datFileHandle);
}
