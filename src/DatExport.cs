using System.Runtime.InteropServices;

namespace LotroKoniecDev;

/// <summary>
/// P/Invoke wrapper dla datexport.dll
/// </summary>
public static class DatExport
{
    private const string DLL = "datexport.dll";
    public const uint OpenFlags = 130; // Read + Write

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
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
        [MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] firstIterGuid
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNumSubfiles(int handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileSizes(
        int handle,
        [MarshalAs(UnmanagedType.LPArray)] int[] fileIds,
        [MarshalAs(UnmanagedType.LPArray)] int[] sizes,
        [MarshalAs(UnmanagedType.LPArray)] int[] iterations,
        int offset,
        int count
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSubfileVersion(int handle, int fileId);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,
        out int version
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PurgeSubfileData(int handle, int fileId);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PutSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,
        int size,
        int version,
        int iteration,
        byte unknown2
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Flush(int handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseDatFile(int handle);

    // ===== Helper methods =====

    public static int OpenDatFile(string path)
    {
        const int handle = 0;
        var datIdStamp = new byte[64];
        var firstIterGuid = new byte[64];

        int result = OpenDatFileEx2(
            handle, path, OpenFlags,
            out _, out _, out _, out _, out _,
            datIdStamp, firstIterGuid
        );

        return result == handle ? handle : -1;
    }

    public static Dictionary<int, (int size, int iteration)> GetAllSubfileSizes(int handle)
    {
        int count = GetNumSubfiles(handle);
        var fileIds = new int[count];
        var sizes = new int[count];
        var iterations = new int[count];

        GetSubfileSizes(handle, fileIds, sizes, iterations, 0, count);

        var result = new Dictionary<int, (int, int)>();
        for (int i = 0; i < count; i++)
        {
            result[fileIds[i]] = (sizes[i], iterations[i]);
        }
        return result;
    }

    public static byte[] GetSubfileDataBytes(int handle, int fileId, int size)
    {
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            GetSubfileData(handle, fileId, buffer, 0, out _);
            var data = new byte[size];
            Marshal.Copy(buffer, data, 0, size);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void PutSubfileDataBytes(int handle, int fileId, byte[] data, int version, int iteration)
    {
        var buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buffer, data.Length);
            PurgeSubfileData(handle, fileId);
            PutSubfileData(handle, fileId, buffer, 0, data.Length, version, iteration, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
