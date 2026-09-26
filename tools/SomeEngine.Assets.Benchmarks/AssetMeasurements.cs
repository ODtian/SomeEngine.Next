using System.Diagnostics;
using System.Globalization;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using EngineTexture = SomeEngine.Resources.Texture;

/// <summary>Public-API measurements of real texture assets, rather than stand-in byte containers.</summary>
internal static class AssetMeasurements
{
    private const byte InitialPixel = 71;
    private const byte ChangedPixel = 42;
    private const int LookupCount = 10000;
    private static readonly byte[] Change = [ChangedPixel];

    private static async Task<int> Main(string[] args)
    {
        int samples;
        if (args.Length == 0) samples = 31;
        else if (args.Length == 2 && args[0] == "--samples"
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            && count is >= 3 and <= 301) samples = count;
        else
        {
            Console.Error.WriteLine("Usage: dotnet run -c Release --project tools/SomeEngine.Assets.Benchmarks -- [--samples 3..301]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string directory = Path.Combine(Path.GetTempPath(), $"someengine-asset-measurements-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"asset-measurements: samples={samples} dotnet={Environment.Version} processors={Environment.ProcessorCount} process-64bit={Environment.Is64BitProcess} file-cache=uncontrolled allocation-counter=whole-process"));
            Console.WriteLine("Each series excludes one full warm-up. Timings exclude checks unless the check belongs to the public operation; file writes include the asset's durable save. No GPU is used.");
            foreach (int pageBytes in new[] { 4096, 16384, 65536 })
            {
                foreach (var size in new (uint Width, uint Height)[] { (64, 64), (2048, 1024), (4096, 4096) })
                    await RunSeriesAsync(directory, pageBytes, size.Width, size.Height, samples, cancellation.Token).ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Asset measurements canceled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static PageArena CreateArena(int pageBytes) => new(
        new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = 256L * 1024 * 1024 }),
        new PageArenaOptions
        {
            MinimumPageSize = 256,
            MaximumPageSize = pageBytes,
            AllocationSize = 256 * 1024,
            RetainedEmptyAllocationsPerSize = 1,
        });

    private static async Task RunSeriesAsync(string directory, int pageBytes, uint width, uint height,
        int sampleCount, CancellationToken cancellationToken)
    {
        long length = checked((long)width * height);
        string sourcePath = Path.Combine(directory, $"input-{pageBytes}-{length}.texture.asset");
        string savedPath = Path.Combine(directory, $"saved-{pageBytes}-{length}.texture.asset");
        using var arena = CreateArena(pageBytes);
        await CreateInputAsync(sourcePath, width, height, arena, cancellationToken).ConfigureAwait(false);
        RequireEmpty(arena);
        _ = await MeasureRoundAsync(sourcePath, savedPath, arena, length, cancellationToken).ConfigureAwait(false);
        var values = new RoundResult[sampleCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[sample] = await MeasureRoundAsync(sourcePath, savedPath, arena, length, cancellationToken).ConfigureAwait(false);
        }
        ReportLatency("open", values, static value => value.Open, pageBytes, length);
        ReportLatency("duplicate-metadata", values, static value => value.Duplicate, pageBytes, length);
        ReportLatency("save-unloaded", values, static value => value.Save, pageBytes, length);
        ReportLatency("acquire-pixels", values, static value => value.Acquire, pageBytes, length);
        ReportLatency("first-write", values, static value => value.Write, pageBytes, length);
        ReportLatency("create-ready", values, static value => value.Create, pageBytes, length);
        double[] lookups = values.Select(static value => value.Lookup.Nanoseconds / LookupCount).Order().ToArray();
        long[] allocations = values.Select(static value => value.Lookup.Allocation).Order().ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"asset-block-query: page={pageBytes} pixels={length} queries-per-sample={LookupCount} p50={Percentile(lookups, .50):F2}ns p95={Percentile(lookups, .95):F2}ns allocation-per-{LookupCount}-queries={allocations[allocations.Length / 2]}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"asset-content: page={pageBytes} pixels={length} open-decoded-max={values.Max(static value => value.OpenDecodedBytes)} open-active-max={values.Max(static value => value.OpenActiveBytes)} metadata-save-extra-decoded-max={values.Max(static value => value.SaveDecodedBytes)} first-write-copy-max={values.Max(static value => value.WriteCopiedBytes)} ready-active-max={values.Max(static value => value.ReadyActiveBytes)} ready-backing-max={values.Max(static value => value.ReadyBackingBytes)} after-release=0"));
        File.Delete(sourcePath);
        File.Delete(savedPath);
    }

    private static async Task CreateInputAsync(string path, uint width, uint height, PageArena arena,
        CancellationToken cancellationToken)
    {
        using var asset = new TextureAsset(arena) { Name = "measured texture" };
        TextureBlock block = asset.GetBlock<TextureBlock>();
        block.Dimension = TextureDimension.Texture2D;
        block.Width = width;
        block.Height = height;
        block.Depth = 1;
        block.ArrayLayerCount = 1;
        block.MipLevelCount = 1;
        block.Format = Format.R8UNorm;
        block.SampledFormat = Format.R8UNorm;
        block.SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D;
        long length = checked((long)width * height);
        ulong key = TextureTiles.ChunkKey(0, 0, 0);
        block.MipTiles = [new TextureMipTile
        {
            Width = width, Height = height, RowPitch = width, SlicePitch = checked((ulong)length),
            ChunkKey = key, DecodedLength = checked((ulong)length),
        }];
        using var pixels = new PageBuffer(arena, length);
        pixels.Write(0, length, static (_, bytes) => bytes.Fill(InitialPixel));
        block.Chunks.Set(key, pixels);
        await asset.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RoundResult> MeasureRoundAsync(string sourcePath, string savedPath,
        PageArena arena, long pixelBytes, CancellationToken cancellationToken)
    {
        TextureAsset? opened = null;
        TextureAsset? edited = null;
        EngineTexture? texture = null;
        var result = new RoundResult();
        try
        {
            long allocation = Allocated();
            long started = Stopwatch.GetTimestamp();
            opened = await TextureAsset.OpenAsync(sourcePath, arena, cancellationToken).ConfigureAwait(false);
            result.Open = Finish(started, allocation);
            TextureBlock block = await opened.GetBlockAsync<TextureBlock>(cancellationToken).ConfigureAwait(false);
            ulong key = TextureTiles.ChunkKey(0, 0, 0);
            if (block.Chunks.Contains(key)) throw new InvalidOperationException("Opening the texture unnecessarily acquired its pixel chunk.");
            result.OpenDecodedBytes = opened.StreamingMetrics?.Snapshot().DecodedBytesLoaded ?? 0;
            result.OpenActiveBytes = arena.Statistics.ActiveBytes;
            if (pixelBytes >= 2 * 1024 * 1024 && result.OpenDecodedBytes >= pixelBytes)
                throw new InvalidOperationException("Opening texture metadata decoded its complete pixel payload.");

            allocation = Allocated();
            started = Stopwatch.GetTimestamp();
            using (TextureAsset copy = opened.Duplicate()) copy.Name = "metadata branch";
            result.Duplicate = Finish(started, allocation);

            allocation = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            int found = 0;
            for (int query = 0; query < LookupCount; query++)
                if (ReferenceEquals(block, opened.GetBlock<TextureBlock>())) found++;
            result.Lookup = new Timing(Stopwatch.GetElapsedTime(started).TotalNanoseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocation);
            if (found != LookupCount) throw new InvalidOperationException("Typed block lookup changed the selected block.");

            long decodedBeforeSave = opened.StreamingMetrics?.Snapshot().DecodedBytesLoaded ?? 0;
            using (TextureAsset copy = opened.Duplicate())
            {
                copy.Name = "metadata-only saved change";
                allocation = Allocated();
                started = Stopwatch.GetTimestamp();
                await copy.SaveAsync(savedPath, cancellationToken).ConfigureAwait(false);
                result.Save = Finish(started, allocation);
            }
            result.SaveDecodedBytes = (opened.StreamingMetrics?.Snapshot().DecodedBytesLoaded ?? 0) - decodedBeforeSave;
            if (result.SaveDecodedBytes != 0 || block.Chunks.Contains(key))
                throw new InvalidOperationException("Saving a metadata edit decoded untouched pixels.");
            using (TextureAsset saved = await TextureAsset.OpenAsync(savedPath, arena, cancellationToken).ConfigureAwait(false))
            {
                if (saved.Id != opened.Id || saved.Name != "metadata-only saved change")
                    throw new InvalidDataException("Metadata-only saving changed identity or lost the edit.");
            }

            allocation = Allocated();
            started = Stopwatch.GetTimestamp();
            await block.AcquireChunkForWriteAsync(key, cancellationToken).ConfigureAwait(false);
            result.Acquire = Finish(started, allocation);
            edited = opened.Duplicate();
            TextureBlock changed = edited.GetBlock<TextureBlock>();
            long offset = pixelBytes / 2;
            long copiedBefore = arena.Statistics.CopiedBytes;
            allocation = Allocated();
            started = Stopwatch.GetTimestamp();
            changed.Chunks.Write(key, offset, Change);
            result.Write = Finish(started, allocation);
            result.WriteCopiedBytes = arena.Statistics.CopiedBytes - copiedBefore;
            if (result.WriteCopiedBytes <= 0 || result.WriteCopiedBytes > arena.MaximumPageSize)
                throw new InvalidOperationException("The local edit did not isolate exactly its containing page.");
            using (PageBuffer original = block.Chunks.DuplicateBuffer(key))
                if (original.Read<byte>(offset) != InitialPixel)
                    throw new InvalidDataException("Editing the duplicate changed the original texture.");

            allocation = Allocated();
            started = Stopwatch.GetTimestamp();
            texture = await changed.CreateAsync(edited.Name, cancellationToken).ConfigureAwait(false);
            result.Create = Finish(started, allocation);
            await edited.DisposeAsync().ConfigureAwait(false);
            edited = null;
            await opened.DisposeAsync().ConfigureAwait(false);
            opened = null;
            arena.Trim();
            result.ReadyActiveBytes = arena.Statistics.ActiveBytes;
            result.ReadyBackingBytes = arena.Statistics.BackingBytes;
            if (!texture.HasBootstrap || !texture.TryRetainTile(key, out PageRange<byte>? read))
                throw new InvalidOperationException("Created texture lost its ready pixels when the asset closed.");
            using (read)
            {
                if (read.Sequence.Length != pixelBytes || ReadByte(read, 0) != InitialPixel
                    || ReadByte(read, offset) != ChangedPixel || ReadByte(read, pixelBytes - 1) != InitialPixel)
                    throw new InvalidDataException("The created texture did not retain the edited content.");
            }
        }
        finally
        {
            try { if (texture is not null) await texture.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (edited is not null) await edited.DisposeAsync().ConfigureAwait(false); }
                finally { if (opened is not null) await opened.DisposeAsync().ConfigureAwait(false); }
            }
        }
        RequireEmpty(arena);
        return result;
    }

    private static long Allocated() => GC.GetTotalAllocatedBytes(precise: true);

    private static Timing Finish(long started, long allocation)
    {
        double elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
        return new(elapsed, Allocated() - allocation);
    }

    private static byte ReadByte(PageRange<byte> bytes, long position)
        => bytes.Sequence.Slice(position, 1).FirstSpan[0];

    private static void RequireEmpty(PageArena arena)
    {
        arena.Trim();
        PageArenaStatistics usage = arena.Statistics;
        if (usage.ActiveBytes != 0 || usage.AllocatedBytes != 0 || usage.BackingBytes != 0)
            throw new InvalidOperationException("Closing all measured objects retained page storage.");
    }

    private static void ReportLatency(string operation, RoundResult[] values, Func<RoundResult, Timing> select,
        int pageBytes, long pixelBytes)
    {
        Timing[] readings = values.Select(select).ToArray();
        double[] nanoseconds = readings.Select(static value => value.Nanoseconds).Order().ToArray();
        long[] allocations = readings.Select(static value => value.Allocation).Order().ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"asset-{operation}: page={pageBytes} pixels={pixelBytes} p50={Percentile(nanoseconds, .50) / 1000:F2}us p95={Percentile(nanoseconds, .95) / 1000:F2}us allocation-p50={allocations[allocations.Length / 2]}"));
    }

    private static double Percentile(double[] sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];

    private readonly record struct Timing(double Nanoseconds, long Allocation);

    private sealed class RoundResult
    {
        public Timing Open, Duplicate, Lookup, Save, Acquire, Write, Create;
        public long OpenDecodedBytes, OpenActiveBytes, SaveDecodedBytes, WriteCopiedBytes, ReadyActiveBytes, ReadyBackingBytes;
    }
}
