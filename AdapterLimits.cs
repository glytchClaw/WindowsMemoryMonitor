using System.Runtime.InteropServices;

namespace MemoryWidget;

/// <summary>A GPU or NPU and the most system RAM Windows lets it borrow ("Shared GPU memory" in Task Manager).</summary>
internal sealed record AdapterLimit(string Name, bool IsNpu, ulong SharedLimitBytes, string CounterInstancePrefix);

/// <summary>Reads per-adapter shared memory limits from the graphics kernel. Unlike DXGI, this also sees the NPU.</summary>
internal static class AdapterLimits
{
    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ENUMADAPTERS3 { public ulong Filter; public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_SEGMENTSIZEINFO { public ulong DedicatedVideoMemorySize; public ulong DedicatedSystemMemorySize; public ulong SharedSystemMemorySize; }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters3(ref D3DKMT_ENUMADAPTERS3 args);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref uint hAdapter);

    const ulong FILTER_INCLUDE_COMPUTE_ONLY = 0x1;
    const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    const int KMTQAITYPE_ADAPTERTYPE = 15;
    const int KMTQAITYPE_DRIVER_DESCRIPTION = 65;
    const uint ADAPTERTYPE_SOFTWARE_DEVICE = 0x4;
    const uint ADAPTERTYPE_COMPUTE_ONLY = 0x800;

    public static IReadOnlyList<AdapterLimit> Query()
    {
        const int max = 32;
        var result = new List<AdapterLimit>();
        int infoSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        var args = new D3DKMT_ENUMADAPTERS3 { Filter = FILTER_INCLUDE_COMPUTE_ONLY, NumAdapters = max, pAdapters = Marshal.AllocHGlobal(infoSize * max) };
        try
        {
            if (D3DKMTEnumAdapters3(ref args) != 0) return result;
            for (int i = 0; i < args.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(args.pAdapters + i * infoSize);
                try
                {
                    uint type = Query<uint>(info.hAdapter, KMTQAITYPE_ADAPTERTYPE);
                    if ((type & ADAPTERTYPE_SOFTWARE_DEVICE) != 0) continue; // Microsoft Basic Render Driver
                    var segments = Query<D3DKMT_SEGMENTSIZEINFO>(info.hAdapter, KMTQAITYPE_GETSEGMENTSIZE);
                    result.Add(new AdapterLimit(
                        DriverDescription(info.hAdapter),
                        (type & ADAPTERTYPE_COMPUTE_ONLY) != 0,
                        segments.SharedSystemMemorySize,
                        $"luid_0x{info.LuidHigh:x8}_0x{info.LuidLow:x8}"));
                }
                finally
                {
                    uint h = info.hAdapter;
                    D3DKMTCloseAdapter(ref h);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(args.pAdapters);
        }
        return result;
    }

    static T Query<T>(uint hAdapter, int type) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(default(T), buffer, false);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = hAdapter, Type = type, pPrivateDriverData = buffer, PrivateDriverDataSize = (uint)size };
            D3DKMTQueryAdapterInfo(ref q);
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static string DriverDescription(uint hAdapter)
    {
        const int size = 4096 * sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = hAdapter, Type = KMTQAITYPE_DRIVER_DESCRIPTION, pPrivateDriverData = buffer, PrivateDriverDataSize = size };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.PtrToStringUni(buffer) ?? "" : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
