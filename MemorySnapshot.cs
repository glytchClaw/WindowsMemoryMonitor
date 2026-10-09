using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace MemoryWidget;

internal sealed record AppMemory(string Name, ulong PrivateWorkingSet);

/// <summary>One reading of RAM, GPU/NPU memory, and the biggest apps, plus how to show it on the card.</summary>
internal sealed class MemorySnapshot
{
    public ulong TotalRam { get; private init; }
    public ulong AvailableRam { get; private init; }
    public ulong CommitUsed { get; private init; }
    public ulong CommitLimit { get; private init; }
    public IReadOnlyList<AdapterLimit> Adapters { get; private init; } = [];
    public ulong GpuInUse { get; private init; }
    public ulong NpuInUse { get; private init; }
    public IReadOnlyList<AppMemory> TopApps { get; private init; } = [];
    public DateTime Taken { get; private init; }

    public static MemorySnapshot Capture(IReadOnlyList<AdapterLimit> adapters, bool includeTopApps)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref status);

        var usage = GpuMemoryUsage.Read();
        ulong UsageFor(bool npu) => adapters.Where(a => a.IsNpu == npu)
            .Aggregate(0UL, (sum, a) => sum + usage.Where(u => u.Key.StartsWith(a.CounterInstancePrefix, StringComparison.OrdinalIgnoreCase)).Aggregate(0UL, (s, u) => s + u.Value));

        return new MemorySnapshot
        {
            TotalRam = status.ullTotalPhys,
            AvailableRam = status.ullAvailPhys,
            CommitLimit = status.ullTotalPageFile,
            CommitUsed = status.ullTotalPageFile - status.ullAvailPageFile,
            Adapters = adapters,
            GpuInUse = UsageFor(npu: false),
            NpuInUse = UsageFor(npu: true),
            TopApps = includeTopApps ? ProcessMemory.Top(5) : [],
            Taken = DateTime.Now,
        };
    }

    /// <summary>The highest GPU/NPU shared limit across adapters, or null if it couldn't be read.</summary>
    ulong? MaxLimit => Adapters.Count == 0 ? null : Adapters.Max(a => a.SharedLimitBytes);

    public JsonObject ToCardData(string size, WidgetSettings settings)
    {
        ulong used = TotalRam - AvailableRam;
        int pct = (int)Math.Round(100.0 * used / TotalRam);
        var (ramColor, ramHex) = pct >= 90 ? ("attention", RedHex) : pct >= 75 ? ("warning", AmberHex) : ("good", GreenHex);

        var gpu = Adapters.Where(a => !a.IsNpu).ToList();
        var npu = Adapters.Where(a => a.IsNpu).ToList();
        ulong maxLimit = MaxLimit ?? 0;
        bool known = MaxLimit != null;
        bool limitOk = known && maxLimit <= settings.TargetBytes;
        bool sameLimit = Adapters.Select(a => a.SharedLimitBytes).Distinct().Count() <= 1;

        string limitText = !known ? "Unknown" : (limitOk ? "✓ " : "⚠ ") + Gb(maxLimit, 2) + " GB";
        string split = sameLimit ? "" :
            string.Join(" · ", gpu.Select(a => $"GPU {Gb(a.SharedLimitBytes, 2)} GB").Concat(npu.Select(a => $"NPU {Gb(a.SharedLimitBytes, 2)} GB"))) + ". ";
        string limitDetail = !known ? "Couldn't read the GPU/NPU limit."
            : limitOk ? $"{split}Under your {settings.TargetText} GB target."
            : $"{split}Over your {settings.TargetText} GB target. A driver update may have reset it.";

        ulong gpuNpuUsed = GpuInUse + NpuInUse;
        ulong gpuLimit = gpu.Count == 0 ? 0 : gpu.Max(a => a.SharedLimitBytes);

        return new JsonObject
        {
            ["size"] = size,
            ["ramPct"] = pct,
            ["ramUsed"] = Gb(used, 1),
            ["ramTotal"] = Gb(TotalRam, 1),
            ["ramAvail"] = Gb(AvailableRam, 1),
            ["ramColor"] = ramColor,
            ["ramBar"] = Bar(used, TotalRam, ramHex),
            ["commitUsed"] = Gb(CommitUsed, 1),
            ["commitLimit"] = Gb(CommitLimit, 1),
            ["limitOk"] = limitOk,
            ["limitText"] = limitText,
            ["limitShort"] = !known ? "GPU/NPU limit unknown" : limitOk ? $"✓ GPU/NPU limit {Gb(maxLimit, 2)} GB" : $"⚠ GPU/NPU limit {Gb(maxLimit, 2)} GB",
            ["limitColor"] = !known ? "warning" : limitOk ? "good" : "attention",
            ["limitDetail"] = limitDetail,
            ["gpuUse"] = Gb(GpuInUse, 2),
            ["npuUse"] = Gb(NpuInUse, 2),
            ["hasNpu"] = npu.Count > 0,
            ["gpuBar"] = Bar(gpuNpuUsed, Math.Max(gpuLimit, 1), AccentHex),
            ["topApps"] = new JsonArray(TopApps.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["gb"] = Gb(a.PrivateWorkingSet, 2) }).ToArray()),
            ["updated"] = Taken.ToString("h:mm:ss tt", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>Data for the settings card shown from the widget's "..." > Customize menu.</summary>
    public JsonObject ToSettingsData(string size, WidgetSettings settings, string error)
    {
        int PercentOfRam(ulong bytes) => (int)Math.Floor(100.0 * bytes / TotalRam);

        string hint = MaxLimit is not { } limit
            ? "Couldn't read the current GPU/NPU limit."
            : $"Current limit: {Gb(limit, 2)} GB ({(int)Math.Round(100.0 * limit / TotalRam)}% of your {Gb(TotalRam, 1)} GB RAM).";
        if (settings.TargetBytes < TotalRam)
            hint += $" {settings.TargetText} GB is {PercentOfRam(settings.TargetBytes)}% of RAM.";

        return new JsonObject
        {
            ["size"] = size,
            ["targetGb"] = settings.TargetGb,
            ["hint"] = hint,
            ["error"] = error,
        };
    }

    const string GreenHex = "#2EA043", AmberHex = "#D29922", RedHex = "#E5534B", AccentHex = "#4C9AFF";

    static string Gb(ulong bytes, int decimals) =>
        (bytes / (double)(1UL << 30)).ToString("F" + decimals, CultureInfo.CurrentCulture);

    /// <summary>A thin rounded progress bar as an SVG data URI; Adaptive Cards has no progress element.</summary>
    static string Bar(ulong value, ulong max, string fill)
    {
        const int w = 300, h = 8;
        double fraction = Math.Clamp(value / (double)max, 0, 1);
        int filled = (int)Math.Round(w * fraction);
        if (filled > 0 && filled < h) filled = h; // keep the rounded end visible for tiny values
        string svg =
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">" +
            $"<rect width=\"{w}\" height=\"{h}\" rx=\"4\" fill=\"#808080\" fill-opacity=\"0.3\"/>" +
            (filled > 0 ? $"<rect width=\"{filled}\" height=\"{h}\" rx=\"4\" fill=\"{fill}\"/>" : "") +
            "</svg>";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}

/// <summary>Current GPU/NPU memory use from the "GPU Adapter Memory" performance counters, keyed by counter instance.</summary>
internal static class GpuMemoryUsage
{
    public static Dictionary<string, ulong> Read()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var data = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            foreach (var counter in new[] { "dedicated usage", "shared usage" })
            {
                if (data[counter] is not { } instances) continue;
                foreach (InstanceData instance in instances.Values)
                    result[instance.InstanceName] = result.GetValueOrDefault(instance.InstanceName) + (ulong)Math.Max(0, instance.RawValue);
            }
        }
        catch (Exception)
        {
            // Counters can be briefly unavailable (e.g. during a driver update); show zero use rather than failing the card.
        }
        return result;
    }
}

/// <summary>Per-app private working set — the "Memory" column in Task Manager — grouped by executable.</summary>
internal static class ProcessMemory
{
    const int SystemProcessInformation = 5;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION field offsets on x64.
    const int OffsetNextEntry = 0, OffsetPrivateWorkingSet = 8, OffsetImageNameLength = 56, OffsetImageNameBuffer = 64, OffsetProcessId = 80;

    static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AppMemory> Top(int count)
    {
        int size = 1 << 20;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != STATUS_INFO_LENGTH_MISMATCH) return [];
                size = Math.Max(size * 2, needed + 65536);
            }

            var byExe = new Dictionary<string, (ulong Bytes, List<uint> Pids)>(StringComparer.OrdinalIgnoreCase);
            for (int offset = 0; ;)
            {
                IntPtr entry = buffer + offset;
                int nameBytes = (ushort)Marshal.ReadInt16(entry, OffsetImageNameLength);
                IntPtr namePtr = Marshal.ReadIntPtr(entry, OffsetImageNameBuffer);
                if (nameBytes > 0 && namePtr != IntPtr.Zero)
                {
                    string exe = Marshal.PtrToStringUni(namePtr, nameBytes / 2);
                    if (!byExe.TryGetValue(exe, out var group)) group = (0, new List<uint>());
                    group.Pids.Add((uint)Marshal.ReadIntPtr(entry, OffsetProcessId));
                    byExe[exe] = (group.Bytes + (ulong)Marshal.ReadInt64(entry, OffsetPrivateWorkingSet), group.Pids);
                }
                int next = Marshal.ReadInt32(entry, OffsetNextEntry);
                if (next == 0) break;
                offset += next;
            }

            return byExe.OrderByDescending(g => g.Value.Bytes).Take(count)
                .Select(g => new AppMemory(FriendlyName(g.Key, g.Value.Pids), g.Value.Bytes)).ToList();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The file description (e.g. "Microsoft Edge" for msedge.exe), cached per executable name.</summary>
    static string FriendlyName(string exe, IEnumerable<uint> pids)
    {
        lock (FriendlyNames)
        {
            if (FriendlyNames.TryGetValue(exe, out var cached)) return cached;
        }

        string fallback = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        string? path = pids.Select(ImagePath).FirstOrDefault(p => p != null);
        if (path == null) return fallback; // don't cache: a later instance may be readable

        string name = fallback;
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 40) name = description;
        }
        catch (Exception)
        {
            // Keep the executable name.
        }

        lock (FriendlyNames) FriendlyNames[exe] = name;
        return name;
    }

    static string? ImagePath(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
