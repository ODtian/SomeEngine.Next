using System.Diagnostics;
using System.Text.Json;

namespace SomeEngine.Tools;

internal sealed class LinuxWorkDirectory : IDisposable
{
    public const int DirectoryCount = 3;
    public const long DirectoryBytes = 8L * 1024 * 1024 * 1024;
    private readonly FileStream file;
    public string Path { get; }
    public string StatePath { get; }
    public int[] Processors { get; }
    public GpuDevice? Gpu { get; }

    private LinuxWorkDirectory(string root, int index, FileStream file, int[] processors, GpuDevice? gpu)
    {
        this.file = file;
        Path = System.IO.Path.Combine(root, "work-" + index);
        StatePath = System.IO.Path.Combine(root, "work-" + index + ".json");
        Processors = processors;
        Gpu = gpu;
    }

    public static LinuxWorkDirectory? Acquire(string root, QueueSettings settings, CommandKind kind)
    {
        int coreCount = settings.CpuCoreCount, memoryMiB = settings.MemoryMiB;
        using var shared = OpenLock(System.IO.Path.Combine(root, "resources.lock"), wait: true)!;
        var used = new HashSet<int>();
        var usedGpus = new HashSet<string>();
        var available = new List<(int Index, FileStream File)>();
        for (int index = 0; index < DirectoryCount; index++)
        {
            FileStream? entry = OpenLock(System.IO.Path.Combine(root, "work-" + index + ".lock"), wait: false);
            if (entry is not null) available.Add((index, entry));
            else
            {
                string state = System.IO.Path.Combine(root, "work-" + index + ".json");
                if (File.Exists(state))
                {
                    var resources = JsonSerializer.Deserialize<WorkResources>(File.ReadAllText(state))!;
                    used.UnionWith(resources.Processors);
                    if (resources.GpuId is not null) usedGpus.Add(resources.GpuId);
                }
            }
        }
        try
        {
            GpuDevice? gpu = null;
            if (kind == CommandKind.Gpu)
            {
                GpuDevice[] choices = GpuDevices.Read().Where(device => !usedGpus.Contains(device.Id)
                    && device.Utilization <= 10 && device.FreeMiB >= settings.GpuMemoryMiB + 512).ToArray();
                if (choices.Length < settings.MinimumIdleGpuCount) return null;
                gpu = choices[0];
                GpuDevices.Select(gpu);
            }
            long freeMemory = long.Parse(File.ReadLines("/proc/meminfo").First(line => line.StartsWith("MemAvailable:"))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
            if (available.Count == 0 || freeMemory < ((long)memoryMiB + 2048) * 1024 * 1024) return null;
            var before = ReadCpuTimes();
            Task.Delay(250).GetAwaiter().GetResult();
            var after = ReadCpuTimes();
            int[][] cores = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
                .Select(path => System.IO.Path.Combine(path, "topology", "thread_siblings_list"))
                .Where(File.Exists).Select(File.ReadAllText).Distinct().Select(ParseProcessors)
                .Where(group => group.All(cpu => !used.Contains(cpu) && after.ContainsKey(cpu)))
                .Where(group => group.Average(cpu =>
                {
                    var old = before[cpu]; var current = after[cpu];
                    return 1 - (double)(current.Idle - old.Idle) / Math.Max(1, current.Total - old.Total);
                }) < .2)
                .OrderBy(group => group[0])
                .GroupBy(group => File.ReadAllText($"/sys/devices/system/cpu/cpu{group[0]}/cache/index3/id").Trim())
                .Where(group => group.Count() >= coreCount)
                .Select(group => group.Take(coreCount).ToArray()).FirstOrDefault() ?? [];
            // Every core contributes all of its available hardware threads.
            if (cores.Length < coreCount) return null;
            int[] selected = cores.SelectMany(group => group).ToArray();
            var chosen = available[0]; available.RemoveAt(0);
            var directory = new LinuxWorkDirectory(root, chosen.Index, chosen.File, selected, gpu);
            File.WriteAllText(directory.StatePath, JsonSerializer.Serialize(new WorkResources(selected, gpu?.Id)));
            directory.Mount(root);
            return directory;
        }
        finally { foreach (var entry in available) entry.File.Dispose(); }
    }

    private void Mount(string root)
    {
        Directory.CreateDirectory(Path);
        string image = Path + ".img";
        if (!File.Exists(image))
        {
            using (var disk = File.Create(image)) disk.SetLength(DirectoryBytes);
            Run("mke2fs", "-q", "-t", "ext4", "-O", "^has_journal", "-F", "-m", "0", image);
        }
        bool mounted = File.ReadLines("/proc/self/mountinfo").Any(line => line.Split(' ')[4] == Path);
        if (!mounted) Run(System.IO.Path.Combine(root, "fuse2fs"), "-o", "fakeroot", image, Path);
        var mounting = Stopwatch.StartNew();
        while (mounting.Elapsed.TotalSeconds < 5 && !File.ReadLines("/proc/self/mountinfo").Any(line => line.Split(' ')[4] == Path))
            Task.Delay(50).GetAwaiter().GetResult();
        if (!File.ReadLines("/proc/self/mountinfo").Any(line => line.Split(' ')[4] == Path))
            throw new IOException("The bounded work filesystem could not be mounted.");
    }

    private static void Run(string executable, params string[] args)
    {
        var result = ProcessCommands.Run(ProcessCommands.StartInfo(executable, args));
        if (result.ExitCode != 0) throw new IOException(result.Errors + result.Output);
    }

    private static Dictionary<int, (long Idle, long Total)> ReadCpuTimes() => File.ReadLines("/proc/stat")
        .Where(line => line.StartsWith("cpu") && line.Length > 3 && char.IsDigit(line[3]))
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToDictionary(values => int.Parse(values[0][3..]), values =>
        {
            long[] times = values.Skip(1).Take(8).Select(long.Parse).ToArray();
            return (times[3] + times[4], times.Sum());
        });

    private static int[] ParseProcessors(string text) => text.Trim().Split(',').SelectMany(part =>
    {
        string[] range = part.Split('-'); int first = int.Parse(range[0]);
        return Enumerable.Range(first, range.Length == 1 ? 1 : int.Parse(range[1]) - first + 1);
    }).ToArray();

    private static FileStream? OpenLock(string path, bool wait)
    {
        while (true)
        {
            try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32)
            {
                if (!wait) return null;
                Task.Delay(20).GetAwaiter().GetResult();
            }
        }
    }

    public void Dispose()
    {
        File.Delete(StatePath);
        file.Dispose();
    }

    private sealed record WorkResources(int[] Processors, string? GpuId);
}
