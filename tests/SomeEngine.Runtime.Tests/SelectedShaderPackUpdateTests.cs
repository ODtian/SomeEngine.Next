using System.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Runtime.Tests;

public sealed class SelectedShaderPackUpdateTests
{
    [Fact]
    public async Task UnchangedSelectionKeepsThePackWithoutReadingOtherPayloads()
    {
        using var files = new ShaderFiles();
        AssetEntry selected = await files.SaveAsync("selected", "selected");
        AssetEntry other = await files.SaveAsync("other", "other");
        await files.PackAsync(selected, other);
        byte[] bytes = await File.ReadAllBytesAsync(files.PackPath);
        byte[] otherBytes = await File.ReadAllBytesAsync(Path.Combine(files.Root, other.Path));
        int offset = bytes.AsSpan().IndexOf(otherBytes);
        Assert.True(offset >= 0);
        // AddAsset writes these public document bytes without compression.
        // Damage only the unrelated payload; opening the catalog still succeeds.
        bytes[offset + otherBytes.Length - 1] ^= 0x5A;
        await File.WriteAllBytesAsync(files.PackPath, bytes);
        DateTime time = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(files.PackPath, time);
        Guid generation = await ReadGenerationAsync(files.PackPath);
        await using (var damaged = await AssetPack.OpenAsync(files.PackPath))
            Assert.False(await damaged.ContentEqualsAsync(other.AssetGuid, other.AssetType,
                otherBytes, other.SchemaFingerprint));

        var result = await UpdateAsync(files, selected.AssetGuid);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.Equal(generation, await ReadGenerationAsync(files.PackPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(files.PackPath));
        Assert.Equal(time, File.GetLastWriteTimeUtc(files.PackPath));
    }

    [Fact]
    public async Task ChangedSelectionWritesANewPackAndPreservesOtherAssets()
    {
        using var files = new ShaderFiles();
        AssetEntry selected = await files.SaveAsync("selected", "before");
        AssetEntry other = await files.SaveAsync("other", "other");
        await files.PackAsync(selected, other);
        Guid generation = await ReadGenerationAsync(files.PackPath);
        byte[] otherBytes = await File.ReadAllBytesAsync(Path.Combine(files.Root, other.Path));
        selected = await files.SaveAsync("selected", "after", selected.AssetGuid);
        byte[] selectedBytes = await File.ReadAllBytesAsync(Path.Combine(files.Root, selected.Path));

        var result = await UpdateAsync(files, selected.AssetGuid);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        await using var pack = await AssetPack.OpenAsync(files.PackPath);
        Assert.NotEqual(generation, pack.Generation);
        Assert.Equal(2, pack.Count);
        Assert.True(pack.TryGetEntry(selected.AssetGuid, out AssetPackEntry? stored));
        Assert.Equal(selected.Version, stored!.DocumentVersion);
        Assert.True(await pack.ContentEqualsAsync(selected.AssetGuid, selected.AssetType,
            selectedBytes, selected.SchemaFingerprint));
        Assert.True(await pack.ContentEqualsAsync(other.AssetGuid, other.AssetType,
            otherBytes, other.SchemaFingerprint));
    }

    [Fact]
    public async Task EverySelectedSourceIsValidatedBeforeAChangedPackIsWritten()
    {
        using var files = new ShaderFiles();
        // The public catalog lists paths in ordinal order, so the changed input is first.
        AssetEntry first = await files.SaveAsync("a", "before");
        AssetEntry last = await files.SaveAsync("z", "valid");
        await files.PackAsync(first, last);
        byte[] before = await File.ReadAllBytesAsync(files.PackPath);
        Guid generation = await ReadGenerationAsync(files.PackPath);
        await files.SaveAsync("a", "after", first.AssetGuid);
        File.Delete(Path.Combine(files.Root, last.Path));
        using (var wrong = new ShaderAsset(Guid.NewGuid()) { Name = "wrong identity" })
            await wrong.SaveAsync(Path.Combine(files.Root, last.Path));

        var result = await UpdateAsync(files, first.AssetGuid, last.AssetGuid);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(generation, await ReadGenerationAsync(files.PackPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(files.PackPath));
    }

    private static async Task<Guid> ReadGenerationAsync(string path)
    {
        await using var pack = await AssetPack.OpenAsync(path);
        return pack.Generation;
    }

    private static async Task<(int ExitCode, string Diagnostics)> UpdateAsync(ShaderFiles files, params Guid[] ids)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "SomeEngine.slnx")))
            repository = repository.Parent;
        if (repository is null) throw new DirectoryNotFoundException("The test output is outside its repository.");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string builder = Path.Combine(repository.FullName, "templates", "DefaultProject", "Game.Build",
            "bin", configuration, "net10.0", "Game.Build.dll");
        Assert.True(File.Exists(builder), $"Build the referenced Game.Build project before running this test: {builder}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = repository.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment.Remove("SOMEENGINE_SHADER_MEASUREMENTS");
        start.ArgumentList.Add(builder);
        start.ArgumentList.Add("--update-pack");
        start.ArgumentList.Add(files.Root);
        start.ArgumentList.Add(files.PackPath);
        foreach (Guid id in ids)
        {
            start.ArgumentList.Add("--asset");
            start.ArgumentList.Add(id.ToString());
        }
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(limit.Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        return (process.ExitCode, await output + await errors);
    }

    private sealed class ShaderFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"SomeEngine-SelectedShaders-{Guid.NewGuid():N}");
        internal string PackPath => Path.Combine(Root, "shared.assetpack");
        private readonly AssetProject _project;

        internal ShaderFiles()
        {
            Directory.CreateDirectory(Root);
            _project = new AssetProject(Root, []);
        }

        internal async Task<AssetEntry> SaveAsync(string fileName, string name, Guid id = default)
        {
            using var shader = new ShaderAsset(id) { Name = name };
            AssetEntry entry = await shader.SaveAsync(Path.Combine(Root, fileName + ".shader.asset"));
            _project.RegisterAsset(entry);
            return _project.Manifest.Assets[entry.AssetGuid];
        }

        internal async Task PackAsync(params AssetEntry[] entries)
        {
            var builder = new AssetPackBuilder();
            foreach (AssetEntry entry in entries)
                builder.AddAsset(entry.AssetGuid, entry.AssetType,
                    await File.ReadAllBytesAsync(Path.Combine(Root, entry.Path)),
                    entry.SchemaFingerprint, ChunkCompression.None);
            await builder.PublishAsync(PackPath);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
