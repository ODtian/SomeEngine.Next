using System.Reflection;
using System.Security.Cryptography;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetCommandBehaviorTests
{
    [Theory]
    [InlineData("pack")]
    [InlineData("update-pack")]
    [InlineData("refresh-shader")]
    [InlineData("inspect-shader")]
    public void GenericMaintenanceCommandsAreDiscoverable(string command)
        => Assert.True(AssetCommands.Matches([command]));

    [Fact]
    public async Task PackDryRunDoesNotWriteOrImportAnything()
    {
        using var files = new Files();
        await files.AddShaderAsync("first");
        var before = files.Snapshot();
        using var output = new StringWriter();
        await AssetCommands.PackAsync(files.Root, ["pack", "test.assetpack", "--dry-run"], output);
        Assert.Contains("temporaryDiskBudget=", output.ToString());
        Assert.Equal(before, files.Snapshot());
        Assert.False(File.Exists(files.Pack));
    }

    [Fact]
    public async Task SelectedShaderUpdatePreservesUnselectedBytesAndNoOpGeneration()
    {
        using var files = new Files();
        Guid first = await files.AddShaderAsync("first");
        Guid second = await files.AddShaderAsync("second");
        await AssetCommands.PackAsync(files.Root, ["pack", files.Pack], TextWriter.Null);
        byte[] secondBytes = await File.ReadAllBytesAsync(files.AssetPath("second"));
        byte[] original = await File.ReadAllBytesAsync(files.Pack);
        await AssetCommands.PackAsync(files.Root,
            ["update-pack", files.Pack, "--asset", first.ToString()], TextWriter.Null);
        Assert.Equal(original, await File.ReadAllBytesAsync(files.Pack));
        await files.AddShaderAsync("first", first, "changed");
        await AssetCommands.PackAsync(files.Root,
            ["update-pack", files.Pack, "--asset", first.ToString()], TextWriter.Null);
        await using AssetPack updated = await AssetPack.OpenAsync(files.Pack);
        Assert.Equal(2, updated.Count);
        Assert.True(await updated.ContentEqualsAsync(second, ShaderAsset.FileType, secondBytes, ShaderAsset.SchemaHash));
        Assert.True(await updated.ContentEqualsAsync(first, ShaderAsset.FileType,
            await File.ReadAllBytesAsync(files.AssetPath("first")), ShaderAsset.SchemaHash));
    }

    [Fact]
    public async Task DeltaOmitsUnchangedBytesAndDoesNotReplaceItsBase()
    {
        using var files = new Files();
        await files.AddShaderAsync("first");
        await AssetCommands.PackAsync(files.Root, ["pack", files.Pack], TextWriter.Null);
        byte[] before = await File.ReadAllBytesAsync(files.Pack);
        string delta = Path.Combine(files.Root, "delta.assetpack");
        await AssetCommands.PackAsync(files.Root, ["pack", delta, "--base", files.Pack], TextWriter.Null);
        await using AssetPack pack = await AssetPack.OpenAsync(delta);
        Assert.Equal(0, pack.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(files.Pack));
        await Assert.ThrowsAsync<ArgumentException>(() => AssetCommands.PackAsync(files.Root,
            ["pack", files.Pack, "--base", files.Pack], TextWriter.Null).AsTask());
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("update-pack")]
    public async Task UnsignedCommandsCannotReplaceSignedPacks(string command)
    {
        using var files = new Files();
        Guid id = await files.AddShaderAsync("first");
        var builder = new AssetPackBuilder();
        builder.AddAsset(id, ShaderAsset.FileType, await File.ReadAllBytesAsync(files.AssetPath("first")), ShaderAsset.SchemaHash);
        using RSA key = RSA.Create(2048);
        await builder.PublishSignedAsync(files.Pack, key);
        byte[] before = await File.ReadAllBytesAsync(files.Pack);
        await Assert.ThrowsAsync<ArgumentException>(() => AssetCommands.PackAsync(files.Root,
            [command, files.Pack, "--asset", id.ToString()], TextWriter.Null).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(files.Pack));
    }

    [Fact]
    public async Task PackRejectsDuplicateUpdateSelectionAndOutputEscape()
    {
        using var files = new Files();
        Guid id = await files.AddShaderAsync("first");
        await Assert.ThrowsAsync<ArgumentException>(() => AssetCommands.PackAsync(files.Root,
            ["update-pack", files.Pack, "--asset", id.ToString(), "--asset", id.ToString()], TextWriter.Null).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => AssetCommands.PackAsync(files.Root,
            ["pack", Path.Combine(files.Root, "..", "escaped.assetpack"), "--dry-run"], TextWriter.Null).AsTask());
    }

    [Fact]
    public async Task PackRejectsChangedCatalogVersionWithoutPublishing()
    {
        using var files = new Files();
        Guid id = await files.AddShaderAsync("first");
        files.SetEntry("first", id, Guid.NewGuid());
        await Assert.ThrowsAsync<IOException>(() => AssetCommands.PackAsync(files.Root,
            ["pack", files.Pack], TextWriter.Null).AsTask());
        Assert.False(File.Exists(files.Pack));
    }

    [Fact]
    public async Task PackRejectsShaderIdentitySubstitutionWithoutPublishing()
    {
        using var files = new Files();
        Guid id = await files.AddShaderAsync("first");
        Guid version = files.Manifest.Assets[id].Version;
        files.Manifest.RemoveAsset(id);
        files.SetEntry("first", Guid.NewGuid(), version);
        await Assert.ThrowsAsync<InvalidDataException>(() => AssetCommands.PackAsync(files.Root,
            ["pack", files.Pack], TextWriter.Null).AsTask());
        Assert.False(File.Exists(files.Pack));
    }

    [Fact]
    public async Task ShaderRefreshDryRunNeverCallsProjectPreparation()
    {
        using var files = new Files();
        Guid id = await files.AddShaderAsync("first");
        bool called = false;
        var before = files.Snapshot();
        var project = new AssetProject(files.Root, []);
        await AssetCommands.RefreshShaderAsync(project, id, TextWriter.Null, dryRun: true,
            buildShader: (_, _, _) => { called = true; return ValueTask.FromResult(id); });
        Assert.False(called);
        Assert.Equal(before, files.Snapshot());
    }

    [Fact]
    public async Task CancelledPackDoesNotWrite()
    {
        using var files = new Files();
        await files.AddShaderAsync("first");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssetCommands.PackAsync(files.Root,
            ["pack", files.Pack], TextWriter.Null, cancelled.Token).AsTask());
        Assert.False(File.Exists(files.Pack));
    }

    [Fact]
    public async Task SavedOnlyRestoreRejectsMissingSourcesBeforeCallingSlang()
    {
        using var shader = new ShaderAsset(Guid.NewGuid()) { Name = "missing" };
        IGlobalSession global = DispatchProxy.Create<IGlobalSession, RejectSlangCalls>();
        await Assert.ThrowsAsync<InvalidDataException>(() => shader.CreateSavedAsync(global, new SessionDesc()).AsTask());
    }

    [Fact]
    public async Task SavedOnlyRestoreRejectsStaleCodeBeforeCallingSlang()
    {
        using var shader = new ShaderAsset(Guid.NewGuid()) { Name = "stale" };
        ShaderSourceBlock source = shader.GetBlock<ShaderSourceBlock>();
        source.SourceNames.Add("saved.slang");
        source.SetSerializedSource(0, new byte[] { 1, 2, 3 });
        IGlobalSession global = DispatchProxy.Create<IGlobalSession, RejectSlangCalls>();
        await Assert.ThrowsAsync<InvalidDataException>(() => shader.CreateSavedAsync(global, new SessionDesc()).AsTask());
    }

    [Fact]
    public async Task SavedOnlyRestoreHonorsCancellationBeforeCallingSlang()
    {
        using var shader = new ShaderAsset(Guid.NewGuid()) { Name = "cancelled" };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        IGlobalSession global = DispatchProxy.Create<IGlobalSession, RejectSlangCalls>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shader.CreateSavedAsync(global,
            new SessionDesc(), cancellationToken: cancelled.Token).AsTask());
    }

    [Fact]
    public async Task VerifiedReadUsesSelectedPackedBytesAndRejectsVersionMismatch()
    {
        using var files = new Files();
        Guid id = await files.AddShaderAsync("first");
        byte[] expected = await File.ReadAllBytesAsync(files.AssetPath("first"));
        await AssetCommands.PackAsync(files.Root, ["pack", files.Pack], TextWriter.Null);
        AssetEntry packed = files.Manifest.Assets[id] with { PackPath = "test.assetpack" };
        File.Delete(files.AssetPath("first"));
        Assert.Equal(expected, await AssetCommands.ReadAssetBytesAsync(files.Root, packed));
        await Assert.ThrowsAsync<IOException>(() => AssetCommands.ReadAssetBytesAsync(files.Root,
            packed with { Version = Guid.NewGuid() }).AsTask());
    }

    public class RejectSlangCalls : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Saved-only validation must reject these inputs before invoking Slang.");
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SomeEngine-AssetCommands-" + Guid.NewGuid().ToString("N"));
        public AssetManifest Manifest { get; } = new();
        public string Pack => Path.Combine(Root, "test.assetpack");
        public Files() => Directory.CreateDirectory(Root);
        public string AssetPath(string name) => Path.Combine(Root, name + ShaderAsset.PathSuffix);
        public async Task<Guid> AddShaderAsync(string name, Guid? id = null, string? label = null)
        {
            using var shader = new ShaderAsset(id ?? Guid.NewGuid()) { Name = label ?? name };
            await shader.SaveSourceAsync(Path.Combine(Root, name + ".shader"));
            AssetEntry entry = await shader.SaveAsync(AssetPath(name));
            SetEntry(name, shader.Id, entry.Version);
            return shader.Id;
        }
        public void SetEntry(string name, Guid id, Guid version)
        {
            Manifest.AddAsset(id, name, name + ShaderAsset.PathSuffix, ShaderAsset.FileType, ShaderAsset.SchemaHash,
                definitionPath: name + ".shader", version: version);
            Manifest.Save(Path.Combine(Root, "Library", "AssetManifest"));
        }
        public string[] Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
