using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Materials;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterAssetLoaderTests
{
    [Fact]
    public async Task ClusterRenderAssetRequestsEveryShaderThroughTheSameGenericLoader()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Guid[] shaderGuids = new Guid[roles.Length];
        for (int index = 0; index < shaderGuids.Length; index++)
        {
            using ShaderAsset shader = CreateShaderData($"cluster-{index}");
            shaderGuids[index] = project.RegisterAsset(await shader.SaveAsync(
                Path.Combine(project.RootPath, $"assets/shaders/cluster-{index}.shader.asset")));
        }

        using ClusterShadersAsset cluster = CreateClusterRoot(shaderGuids);
        Guid clusterGuid = project.RegisterAsset(await cluster.SaveAsync(
            Path.Combine(project.RootPath, "assets/pipelines/main.clusterrender.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());

        ClusterShadersAsset[] opened = await Task.WhenAll(
            ClusterShadersAsset.OpenAsync(loader, new AssetId<ClusterShadersAsset>(clusterGuid)).AsTask(),
            ClusterShadersAsset.OpenAsync(loader, new AssetId<ClusterShadersAsset>(clusterGuid)).AsTask());
        await using ClusterShadersAsset first = opened[0];
        await using ClusterShadersAsset second = opened[1];
        Assert.NotSame(first, second);
        Assert.Equal(shaderGuids.Order(), first.Describe().Dependencies.Order());
        using ClusterShaders shaders = await first.GetBlock<ClusterShadersBlock>().CreateAsync(first.Name, loader);
        using ClusterShaders other = await second.GetBlock<ClusterShadersBlock>().CreateAsync(second.Name, loader);
        Assert.NotSame(shaders, other);
        Assert.Equal("MainCluster", shaders.Name);
        Assert.Equal(roles.Length, shaders.Operations?.Count);
        for (int index = 0; index < roles.Length; index++)
        {
            ShaderOperation operation = shaders.Operations![index];
            Assert.Equal(roles[index], operation.Role);
            ShaderEntry shader = operation.Shaders[0];
            Guid shaderGuid = first.GetBlock<ClusterShadersBlock>().Operations[index].Shaders![0].Asset.Value;
            Assert.Equal($"cluster-{index}", shader.Shader.Name);
            Assert.NotSame(shader.Shader, other.Operations[index].Shaders[0].Shader);
            Assert.Equal(shaderGuids[index], shaderGuid);
        }
    }

    [Fact]
    public async Task ClusterRenderAssetWithEmptyLastShaderReferenceFailsBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        Guid clusterGuid = Guid.NewGuid();
        string relativePath = "assets/pipelines/invalid.clusterrender.asset";
        string fullPath = Path.Combine(directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Guid valid = Guid.NewGuid();
        int operationCount = Enum.GetValues<ClusterShaderOperationRole>()
            .Count(static role => role != ClusterShaderOperationRole.None);
        using ClusterShadersAsset root = CreateClusterRoot(
            Enumerable.Repeat(valid, operationCount).ToArray());
        ClusterShadersBlock settings = root.GetBlock<ClusterShadersBlock>();
        ClusterShaderOperation last = settings.Operations[^1];
        settings.Operations[^1] = last with { Shaders = last.Shaders!.SetItem(0, last.Shaders[0] with { Asset = default }) };
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => root.SaveAsync(fullPath).AsTask());
        Assert.Contains("Asset", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fullPath));
    }
    private static ShaderAsset CreateShaderData(string name)
    {
        var program = CookedShaderTestProgram.Create();
        using var output = new MemoryStream();
        program.Write(output);
        byte[] bytes = output.ToArray();
        var asset = new ShaderAsset { Name = name };
        using var code = new ShaderBlock
        {
            Programs = [new CookedShaderProgram { Backend = program.Backend, DataChunkKey = ShaderBlock.BytecodeChunkKey(0),
                DataDecodedLength = (ulong)bytes.Length }],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "spirv",
                    EntryPoint = "VSMain",
                    Stage = SomeEngine.Resources.ShaderStage.Vertex,
                },
            ],
        };
        try { code.Chunks.Set(code.Programs[0].DataChunkKey, bytes); asset.SetBlock(code); return asset; }
        catch { asset.Dispose(); throw; }
    }

    private static ClusterShadersAsset CreateClusterRoot(IReadOnlyList<Guid> shaders)
    {
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Assert.Equal(roles.Length, shaders.Count);
        var asset = new ClusterShadersAsset { Name = "MainCluster" };
        using var settings = new ClusterShadersBlock
        {
            Operations = roles
                .Select((role, index) => Operation(role, shaders[index]))
                .ToArray(),
        };
        asset.SetBlock(settings);
        return asset;
    }

    private static ClusterShaderOperation Operation(
        ClusterShaderOperationRole role,
        Guid guid)
    {
        var result = new ClusterShaderOperation
        {
            Role = role,
        };
        if (role is ClusterShaderOperationRole.HardwareVisibilityRaster
            or ClusterShaderOperationRole.VisibilityResolveImage
            or ClusterShaderOperationRole.VisibilityResolveBuffer)
        {
            result = result with { Shaders =
            [
                new ShaderRef
                {
                    Asset = new(guid),
                    EntryPoint = "vertex",
                    Stage = ShaderStage.Vertex,
                },
                new ShaderRef
                {
                    Asset = new(guid),
                    EntryPoint = "pixel",
                    Stage = ShaderStage.Pixel,
                },
            ] };
        }
        else
        {
            result = result with { Shaders =
            [
                new ShaderRef
                {
                    Asset = new(guid),
                    EntryPoint = "compute",
                    Stage = ShaderStage.Compute,
                },
            ] };
        }
        return result;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-ClusterRuntime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
