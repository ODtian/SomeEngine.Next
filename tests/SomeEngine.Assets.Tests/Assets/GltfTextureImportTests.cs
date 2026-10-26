using Microsoft.Extensions.DependencyInjection;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Job;
using TextureAsset = SomeEngine.Assets.Schema.TextureAsset;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class GltfTextureImportTests
{
    private const string WhitePng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4x8AAAAASUVORK5CYII=";

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task IndependentTexturesRetainPixelsAfterImportAndReopen(int workerCount)
    {
        string directory = Directory.CreateTempSubdirectory("texture-import-").FullName;
        try
        {
            ModelRoot model = await ReadModelAsync(directory, Convert.FromBase64String(WhitePng));
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = workerCount });
            var result = new ImportData();
            scheduler.Schedule(new CreateImport(result)).Complete();
            await using ImportContext context = result.Context!;
            Assert.Same(scheduler, context.Scheduler);
            await using var other = new ImportContext(scheduler: scheduler);
            Assert.Same(context.DecodingCategory, other.DecodingCategory);
            RegisterTextures(context, model);
            TextureAsset[] textures = await Task.WhenAll(model.LogicalTextures.Select(source =>
                context.GetAssetAsync<SharpGLTF.Schema2.Texture, TextureAsset>(source).AsTask()))
                .WaitAsync(TimeSpan.FromSeconds(10));
            for (int index = 0; index < textures.Length; index++)
            {
                string saved = Path.Combine(directory, $"texture-{index}.texture.asset");
                await textures[index].SaveAsync(saved);
                using TextureAsset reopened = await TextureAsset.OpenAsync(saved);
                Texture texture = await reopened.GetBlock<TextureBlock>().CreateAsync(firstMip: 0);
                Assert.Equal(1u, texture.Width);
                Assert.Equal(1u, texture.Height);
                Assert.True(texture.TryGetTile(texture.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> bytes));
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, bytes.ToArray());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InvalidImageCompletesWithItsDecodeFailure()
    {
        string directory = Directory.CreateTempSubdirectory("texture-failure-").FullName;
        try
        {
            ModelRoot model = await ReadModelAsync(directory, Convert.FromBase64String(WhitePng)[..33]);
            await using var context = new ImportContext();
            RegisterTextures(context, model);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.GetAssetAsync<SharpGLTF.Schema2.Texture, TextureAsset>(model.LogicalTextures[0]).AsTask());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancelledImportFinishesItsSourceReadBeforeCleanup()
    {
        string directory = Directory.CreateTempSubdirectory("texture-cancel-").FullName;
        try
        {
            ModelRoot model = await ReadModelAsync(directory, Convert.FromBase64String(WhitePng));
            using var cancellation = new CancellationTokenSource();
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var context = new ImportContext(cancellation.Token, scheduler))
            {
                RegisterTextures(context, model);
                JobHandle blocked = scheduler.Schedule(new HoldDecode(started, release),
                    new JobScheduleOptions { Category = context.DecodingCategory });
                await started.Task;
                try
                {
                    Task<TextureAsset> import = context.GetAssetAsync<SharpGLTF.Schema2.Texture, TextureAsset>(model.LogicalTextures[0]).AsTask();
                    cancellation.Cancel();
                    release.Set();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
                }
                finally { release.Set(); blocked.Complete(); }
            }
            File.Delete(Path.Combine(directory, "white.png"));
            Assert.False(File.Exists(Path.Combine(directory, "white.png")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<ModelRoot> ReadModelAsync(string directory, byte[] sourceBytes)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, "white.png"), sourceBytes);
        string path = Path.Combine(directory, "textures.gltf");
        await File.WriteAllTextAsync(path, $$"""
            {"asset":{"version":"2.0"},"images":[{"uri":"white.png"},{"uri":"data:image/png;base64,{{Convert.ToBase64String(sourceBytes)}}"}],"textures":[{"source":0},{"source":1}]}
            """);
        return await new GltfSourceImporter(new ServiceCollection(), "texture-tests").ReadAsync(path, CancellationToken.None);
    }

    private static void RegisterTextures(ImportContext context, ModelRoot model)
    {
        context.EnableImporter<SharpGLTF.Schema2.Texture, TextureAsset>(Guid.NewGuid(), [TextureBlock.BlockTypeId],
            (source, imports, output, token) => new GltfTextureImporter().ImportAsync(source, new(), imports, output, token));
        foreach (SharpGLTF.Schema2.Texture source in model.LogicalTextures)
        {
            string key = $"texture/{source.LogicalIndex}";
            Guid id = context.GetAssetId(key);
            context.AddAsset(source, new TextureAsset(id), key, $"{id}.texture.asset");
        }
    }

    private sealed class ImportData { internal ImportContext? Context; }
    private readonly struct CreateImport(ImportData result) : IJob
    {
        public void Execute() => result.Context = new ImportContext();
    }
    private readonly struct HoldDecode(TaskCompletionSource started, ManualResetEventSlim release) : IJob
    {
        public void Execute() { started.SetResult(); release.Wait(); }
    }
}
