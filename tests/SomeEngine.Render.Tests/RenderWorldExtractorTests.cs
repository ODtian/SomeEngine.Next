using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Systems;
using Entity = SomeEngine.ECS.Entities.Entity;

namespace SomeEngine.Render.Tests;

public sealed class RenderWorldExtractorTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    private readonly TestAssets assets = new();
    [Fact]
    public void SharedMaterialRemainsUntilBothGeometryExtractorsReleaseTheirEntities()
    {
        using World main = new();
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor(), new CustomGeometryExtractor());
        var material = new StandardMaterial();
        Entity mesh = AddMeshSource(main, MeshAsset(5, 1), 0, Vector3.Zero);
        SetSourceBindings(main, mesh, material, default!, 1);
        Entity custom = main.Create(new CustomGeometry(material));
        ExtractAndApply(extraction, main, render);

        main.Destroy(mesh);
        ExtractAndApply(extraction, main, render);
        Assert.Same(material, render.ReadMaterial(material.MaterialId));

        main.Destroy(custom);
        ExtractAndApply(extraction, main, render);
        Assert.Throws<KeyNotFoundException>(() => render.ReadMaterial(material.MaterialId));
    }

    private readonly record struct CustomGeometry(Material Material) : IComponent;

    private sealed class CustomGeometryExtractor : IRenderExtractionSystem
    {
        private readonly List<(Entity Source, Material Material)> _values = [];
        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<CustomGeometry>();
            query.Write<RenderMaterialReference>();
        }
        public void CollectResources(scoped QueryScope source, RenderExtractionResources resources)
        {
            foreach (QueryChunk chunk in source)
                if (chunk.TryRead<CustomGeometry>(out var values))
                    foreach (var value in values) resources.Add(value.Material);
        }
        public void Reset() => _values.Clear();
        public void Collect(QueryChunk chunk)
        {
            if (!chunk.TryRead<CustomGeometry>(out var values)) return;
            for (int index = 0; index < values.Length; index++)
                _values.Add((chunk.Entities[index], values[index].Material));
        }
        public bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context) => false;
        public void ApplyChanges(RenderExtractionContext context) => Apply(context);
        public void Apply(RenderExtractionContext context)
        {
            foreach (var value in _values)
            {
                Entity entity = context.RetainMirror(value.Source);
                context.SetMaterials(entity, [value.Material]);
            }
        }
    }

    [Fact]
    public void MeshPagesApplyTheirRevisionThroughExtraction()
    {
        using World game = new();
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        Mesh complete = MeshAsset(810, 1);
        Mesh mesh = Mesh.Create("streamed mesh", complete.VertexEncoding, complete.PageDigests, complete.Bvh);
        Entity source = AddMeshSource(game, mesh, 0, Vector3.Zero);
        ExtractAndApply(extraction, game, render);
        Entity mirror = extraction.RequireMirror(source);
        Mesh before = render.World.ReadComponentData<RenderMesh>(mirror).Mesh;
        Assert.True(complete.TryGetPage(0, out var page));

        mesh.AddPages([(0, page)], mesh.Revision);
        extraction.Extract(game);
        Assert.Same(before, render.World.ReadComponentData<RenderMesh>(mirror).Mesh);
        graphics.Apply(extraction, render);

        Mesh after = render.World.ReadComponentData<RenderMesh>(mirror).Mesh;
        Assert.Equal(mesh.Revision, after.Revision);
        Assert.True(after.TryGetPage(0, out var supplied));
        Assert.Equal(page.ToArray(), supplied.ToArray());
        Assert.False(before.TryGetPage(0, out _));
    }

    [Fact]
    public void SharedMaterialEditsApplyTheirVersionAtExtraction()
    {
        using World main = new();
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        var material = new StandardMaterial();
        for (int index = 0; index < 128; index++)
        {
            Entity source = AddMeshSource(main, MeshAsset(5, 1), 0, new Vector3(index, 0, 0));
            SetSourceBindings(main, source, material, default!, 1);
        }
        ExtractAndApply(extraction, main, render);
        Material before = render.ReadMaterial(material.MaterialId);
        ulong materialVersion = render.ReadMaterialRevision(material.MaterialId);
        ulong version = render.World.AcquireSystemVersion();
        ulong materialWrites = render.World.ComponentWriteVersion(ComponentId<RenderMaterialReference>.Value);
        material.Roughness = 0.9f;
        extraction.Extract(main);
        Assert.Same(before, render.ReadMaterial(material.MaterialId));
        Assert.Equal(materialVersion, render.ReadMaterialRevision(material.MaterialId));
        graphics.Apply(extraction, render);
        Assert.Same(material, render.ReadMaterial(material.MaterialId));
        Assert.Equal(0.9f, Assert.IsType<StandardMaterial>(render.ReadMaterial(material.MaterialId)).Roughness);
        Assert.Equal(material.Revision, render.ReadMaterialRevision(material.MaterialId));
        Assert.True(render.World.ComponentWriteVersion(ComponentId<RenderMaterialReference>.Value) > materialWrites);
        Query query = render.World.Query(new QueryDescription().With<Buffer<RenderMaterialBinding>>());
        bool bindingsChanged = false;
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows) bindingsChanged |= chunk.WasWritten<Buffer<RenderMaterialBinding>>(version);
        Assert.False(bindingsChanged);
    }

    [Fact]
    public void MaterialReferenceChangesCrossExtractionAndLastRemovalReleasesTheReference()
    {
        using World main = new();
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        var first = new StandardMaterial();
        var second = new StandardMaterial();
        Entity source = AddMeshSource(main, MeshAsset(5, 1), 0, Vector3.Zero);
        SetSourceBindings(main, source, first, default!, 1);
        ExtractAndApply(extraction, main, render);
        Assert.Same(first, render.ReadMaterial(first.MaterialId));
        SetSourceBindings(main, source, second, default!, 1);
        extraction.Extract(main);
        Assert.Same(first, render.ReadMaterial(first.MaterialId));
        graphics.Apply(extraction, render);
        Assert.Same(second, render.ReadMaterial(second.MaterialId));
        Assert.Throws<KeyNotFoundException>(() => render.ReadMaterial(first.MaterialId));
        main.Destroy(source);
        ExtractAndApply(extraction, main, render);
        Assert.Throws<KeyNotFoundException>(() => render.ReadMaterial(second.MaterialId));
        Assert.Equal(0.5f, first.Roughness);
        Assert.Equal(0.5f, second.Roughness);
    }

    [Fact]
    public async Task TextureContentVersionsCrossExtractionWithSharedReferences()
    {
        using World main = new();
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor(), new LightRenderExtractor());
        Texture texture = TextureAsset(91, 1);
        var material = new StandardMaterial { AlbedoMap = texture };
        Entity source = AddMeshSource(main, MeshAsset(5, 1), 0, Vector3.Zero);
        SetSourceBindings(main, source, material, default!, 1);
        main.Add(source, new DirectionalLight(Vector3.UnitZ, Vector3.One, 1));
        main.Add(source, new LightCookie(texture, 1, Vector4.One, Matrix4x4.Identity));
        ExtractAndApply(extraction, main, render);
        Entity mirror = extraction.RequireMirror(source);
        ulong materialWrites = render.World.ComponentWriteVersion(ComponentId<RenderMaterialReference>.Value);
        ulong cookieWrites = render.World.ComponentWriteVersion(ComponentId<RenderLightCookie>.Value);
        ulong materialRevision = material.Revision;
        ulong textureRevision = texture.Revision;
        TextureVersions before = render.ReadTextureVersions(texture);
        await texture.UpdateAsync(TextureAsset(92, 1));
        extraction.Extract(main);
        Assert.Equal(before, render.ReadTextureVersions(texture));
        graphics.Apply(extraction, render);
        Assert.Equal(textureRevision + 1, texture.Revision);
        Assert.Equal(materialRevision, material.Revision);
        Assert.Equal(materialWrites, render.World.ComponentWriteVersion(ComponentId<RenderMaterialReference>.Value));
        Assert.Equal(cookieWrites, render.World.ComponentWriteVersion(ComponentId<RenderLightCookie>.Value));
        Assert.Equal(texture.Versions, render.ReadTextureVersions(texture));
        Assert.Same(material, render.ReadMaterial(material.MaterialId));
        Assert.Same(texture, material.AlbedoMap);
        Assert.Same(texture, render.World.ReadComponentData<RenderLightCookie>(mirror).Texture);
    }

    [Fact]
    public void TextureAllocationChangesCrossTheExistingExtractionBoundary()
    {
        using World game = new();
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        Texture texture = TextureAsset(93, 1);
        var material = new StandardMaterial { AlbedoMap = texture };
        Entity source = AddMeshSource(game, MeshAsset(5, 1), 0, Vector3.Zero);
        SetSourceBindings(game, source, material, default!, 1);
        ExtractAndApply(extraction, game, render);
        TextureVersions before = render.ReadTextureVersions(texture);
        _ = graphics.RenderDevice.GetTexture(texture);
        Assert.True(texture.Versions.Allocation > before.Allocation);
        Assert.Equal(before, render.ReadTextureVersions(texture));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        TextureVersions allocated = render.ReadTextureVersions(texture);
        Assert.Equal(texture.Versions, allocated);
        graphics.RenderDevice.ReleaseTexture(texture);
        Assert.Equal(allocated, render.ReadTextureVersions(texture));
        ExtractAndApply(extraction, game, render);
        Assert.True(render.ReadTextureVersions(texture).Allocation > allocated.Allocation);
        Assert.Equal(before.Content, render.ReadTextureVersions(texture).Content);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(129)]
    [InlineData(1025)]
    public void Extract_MixedMotionAndUnchangedWritesAdvanceHistoryOnce(int count)
    {
        using World mainWorld = new();
        using RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Mesh mesh = MeshAsset(1, 1);
        Entity[] sources = Enumerable.Range(0, count).Select(index =>
            AddMeshSource(mainWorld, mesh, 0, new Vector3(index, 0, 0))).ToArray();
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity[] mirrors = sources.Select(source => extraction.RequireMirror(source)).ToArray();
        Vector3[] positions = Enumerable.Range(0, count).Select(index => new Vector3(index, 0, 0)).ToArray();
        for (int iteration = 0; iteration < 8; iteration++)
        {
            Vector3[] previous = positions.ToArray();
            for (int index = 0; index < count; index++)
            {
                int action = (index + iteration) % 4;
                if (action == 0) positions[index] += new Vector3(0, 1, 2);
                if (action <= 1) Move(mainWorld, sources[index], positions[index]);
            }
            ExtractAndApply(extraction, mainWorld, renderWorld);
            for (int index = 0; index < count; index++)
            {
                Assert.Equal(positions[index], renderWorld.World.ReadComponentData<RenderTransform>(mirrors[index]).Position);
                Assert.Equal(previous[index], renderWorld.World.ReadComponentData<RenderPreviousTransform>(mirrors[index]).Value.Position);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(129)]
    public void Extract_MissingMotionHistoryRejectsChangesBeforeAnyTransformWrite(int count)
    {
        using World mainWorld = new();
        using RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Mesh mesh = MeshAsset(1, 1);
        Entity[] sources = Enumerable.Range(0, count)
            .Select(index => AddMeshSource(mainWorld, mesh, 0, new Vector3(index + 1))).ToArray();
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity[] mirrors = sources.Select(extraction.RequireMirror).ToArray();
        Entity secondMirror = mirrors[^1];
        renderWorld.World.Remove<RenderPreviousTransform>(secondMirror);
        for (int index = 0; index < count; index++) Move(mainWorld, sources[index], new Vector3(index + 3));
        Assert.Throws<InvalidOperationException>(() => ExtractAndApply(extraction, mainWorld, renderWorld));
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(new Vector3(index + 1), renderWorld.World.ReadComponentData<RenderTransform>(mirrors[index]).Position);
            if (index < count - 1)
                Assert.Equal(new Vector3(index + 1), renderWorld.World.ReadComponentData<RenderPreviousTransform>(mirrors[index]).Value.Position);
        }
        renderWorld.World.Add(secondMirror, new RenderPreviousTransform(renderWorld.World.ReadComponentData<RenderTransform>(secondMirror)));
        ExtractAndApply(extraction, mainWorld, renderWorld);
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(new Vector3(index + 3), renderWorld.World.ReadComponentData<RenderTransform>(mirrors[index]).Position);
            Assert.Equal(new Vector3(index + 1), renderWorld.World.ReadComponentData<RenderPreviousTransform>(mirrors[index]).Value.Position);
        }
    }

    [Fact]
    public void Extract_ContinuousMotionAndStoppingAdvanceHistoryOnce()
    {
        using World mainWorld = new();
        using RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(mainWorld, MeshAsset(1, 1), 0, Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity mirror = Mirror(renderWorld, source);
        Vector3 previous = Vector3.Zero;
        Vector3[] positions = [new(1, 2, 3), new(4, 5, 6), new(4, 5, 6), new(4, 5, 6), new(-2, 1, 7)];
        foreach (Vector3 position in positions)
        {
            Move(mainWorld, source, position);
            ExtractAndApply(extraction, mainWorld, renderWorld);
            Assert.Equal(position, renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);
            Assert.Equal(previous, renderWorld.World.ReadComponentData<RenderPreviousTransform>(mirror).Value.Position);
            previous = position;
        }
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(previous, renderWorld.World.ReadComponentData<RenderPreviousTransform>(mirror).Value.Position);
    }

    [Fact]
    public void IncrementalExtractionDeclaresDestinationStorageWhenWorldJobsAreEnabled()
    {
        using var mainWorld = new World(execution: new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default));
        using var renderWorld = new RenderWorld(execution: new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default));
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        var mesh = MeshAsset(812, 1);
        Entity source = AddMeshSource(mainWorld, mesh, 0, Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity mirror = RenderSources(renderWorld, source)[0];
        WorldTransform moved = mainWorld.ReadComponentData<WorldTransform>(source);
        moved.Qvvs.Position = new Vector3(3, 4, 5);
        mainWorld.Set(source, moved);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(new Vector3(3, 4, 5), renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);
    }

    [Fact]
    public void ExplicitPageSourceTravelsThroughTheExistingWorldExtractionPath()
    {
        var mesh = MeshAsset(901, 1);
        using var first = SomeEngine.Assets.Pipeline.MeshPayloadSource.FromMesh(mesh);
        var second = first;
        var game = new SomeEngine.ECS.World();
        var render = new SomeEngine.Render.Systems.RenderWorld();
        using var extraction = new SomeEngine.Render.Systems.RenderExtractionSystems(render, new MeshRenderExtractor(), new LightRenderExtractor());
        var entity = game.Create();
        game.Add(entity, new SomeEngine.Core.ECS.Components.WorldTransform
        {
            Qvvs = new SomeEngine.Core.Math.TransformQvvs(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity),
        });
        game.Add(entity, new SomeEngine.Render.Components.MeshInstance { Mesh = mesh, PageSource = first });
        extraction.Extract(game);
        Assert.True(graphics.Apply(extraction, render));
        var mirror = RenderSources(render, entity)[0];
        Assert.Same(first, render.World.ReadComponentData<SomeEngine.Render.Components.RenderMesh>(mirror).PageSource);
        game.Set(entity, new SomeEngine.Render.Components.MeshInstance { Mesh = mesh, PageSource = second });
        extraction.Extract(game);
        Assert.True(graphics.Apply(extraction, render));
        Assert.Same(second, render.World.ReadComponentData<SomeEngine.Render.Components.RenderMesh>(mirror).PageSource);
    }

    [Fact]
    public void Extract_TransformDeltaMarksOnlyTheChangedMirrorRow()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Mesh mesh = MeshAsset(1, 1);
        Entity[] sources = Enumerable.Range(0, 128).Select(index =>
            AddMeshSource(mainWorld, mesh, 0, new Vector3(index, 0, 0))).ToArray();
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Query query = renderWorld.World.Query(new QueryDescription()
            .With<RenderSource>().With<RenderTransform>().With<RenderPreviousTransform>());
        ulong before = renderWorld.World.Version;
        mainWorld.Set(sources[3], new WorldTransform { Qvvs = new TransformQvvs(new Vector3(3, 1, 0), Quaternion.Identity) });
        ExtractAndApply(extraction, mainWorld, renderWorld);
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows)
            {
                ReadOnlySpan<RenderSource> identities = chunk.Read<RenderSource>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    bool changedRow = identities[row].Entity == sources[3];
                    Assert.Equal(changedRow, chunk.WasWritten<RenderTransform>(row, before));
                    Assert.False(chunk.WasWritten<RenderPreviousTransform>(row, before));
                }
            }
        Entity changed = Mirror(renderWorld, sources[3]);
        Assert.Equal(new Vector3(3, 1, 0), renderWorld.World.ReadComponentData<RenderTransform>(changed).Position);
        Assert.Equal(new Vector3(3, 0, 0), renderWorld.World.ReadComponentData<RenderPreviousTransform>(changed).Value.Position);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(renderWorld.World.ReadComponentData<RenderTransform>(changed),
            renderWorld.World.ReadComponentData<RenderPreviousTransform>(changed).Value);
    }

    [Fact]
    public void Extract_UsesInstalledSystemsAndUpdatesStableMirror()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(1, 1),
            boundsExpansion: 0.25f,
            position: Vector3.One);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.World.IsAlive(mirror));

        Move(mainWorld, source, new Vector3(4, 5, 6));
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(new Vector3(4, 5, 6), renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);
    }

    [Fact]
    public async Task Extract_WaitsForActiveMainWorldMutationBeforeReadingSourceImage()
    {
        using World mainWorld = new(execution: new SomeEngine.ECS.Systems.WorldJobs(SomeEngine.Job.JobSystem.Default));
        using RenderWorld renderWorld = new();
        using var extractionSystems = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(1, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        using ManualResetEventSlim mutationEntered = new();
        using ManualResetEventSlim releaseMutation = new();
        using ManualResetEventSlim extractionEntered = new();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        MeshInstance replacement = new()
        {
            Mesh = MeshAsset(2, 1),
            BoundsExpansion = 3,
        };
        extractionSystems.Add(new MeshReadObserver(value =>
        {
            Assert.Same(replacement.Mesh, value.Mesh);
            Assert.Equal(replacement.BoundsExpansion, value.BoundsExpansion);
            events.Enqueue("extraction observed committed mesh");
        }));
        Task mutation = Task.Run(() =>
        {
            using WorldStructureWriteScope structure = mainWorld.OpenStructureWrite();
            mutationEntered.Set();
            Assert.True(releaseMutation.Wait(TimeSpan.FromSeconds(10)));
            mainWorld.Set(source, replacement);
            events.Enqueue("mesh replacement committed");
        });
        Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(5)));

        Task extraction = Task.Run(() =>
        {
            extractionEntered.Set();
            extractionSystems.Extract(mainWorld);
        });
        try
        {
            Assert.True(extractionEntered.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseMutation.Set();
        }

        await Task.WhenAll(mutation, extraction).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["mesh replacement committed", "extraction observed committed mesh"], events);

        Assert.Empty(RenderSources(renderWorld, source));
        Assert.True(graphics.Apply(extractionSystems, renderWorld));
        Entity mirror = Mirror(renderWorld, source);
        Assert.Same(replacement.Mesh.CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(mirror).Mesh);
        Assert.Equal(replacement.BoundsExpansion, renderWorld.World.ReadComponentData<RenderMesh>(mirror).BoundsExpansion);
    }

    [Fact]
    public async Task NextGameExtractionCompletesWhileCurrentRenderReadRemainsOpen()
    {
        using var mainWorld = new World();
        using var renderWorld = new RenderWorld();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(3, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity mirror = Mirror(renderWorld, source);
        Query renderQuery = renderWorld.World.Query(
            new QueryDescription().With<RenderTransform>());
        using ManualResetEventSlim renderCpuEntered = new();
        using ManualResetEventSlim releaseRenderCpu = new();
        Task renderCpu = Task.CompletedTask;

        try
        {
            renderCpu = Task.Run(() =>
            {
                using QueryScope rows = renderQuery.OpenRead();
                int rowCount = 0;
                foreach (QueryChunk chunk in rows)
                    foreach (RenderTransform transform in chunk.Read<RenderTransform>())
                    {
                        Assert.Equal(Vector3.Zero, transform.Position);
                        rowCount++;
                    }
                Assert.Equal(1, rowCount);
                renderCpuEntered.Set();
                Assert.True(releaseRenderCpu.Wait(TimeSpan.FromSeconds(10)));
            });
            Assert.True(renderCpuEntered.Wait(TimeSpan.FromSeconds(5)));

            Vector3 nextPosition = new(7, 8, 9);
            Move(mainWorld, source, nextPosition);
            Task nextGameExtraction = Task.Run(() => extraction.Extract(mainWorld));
            await nextGameExtraction.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Vector3.Zero, renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);

            releaseRenderCpu.Set();
            await renderCpu;
            Assert.True(graphics.Apply(extraction, renderWorld));
            Assert.Equal(nextPosition, renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);
        }
        finally
        {
            releaseRenderCpu.Set();
            await renderCpu;
        }
    }

    [Fact]
    public void Extract_ApplyFaultKeepsPublishedRootUnchanged()
    {
        var injectedFault = new InvalidOperationException("intentional extraction apply fault");
        World mainWorld = new();
        RenderWorld renderWorld = new();
        var meshWrites = new ExtractionWrites(new MeshRenderExtractor());
        using var extraction = new RenderExtractionSystems(renderWorld, meshWrites, new LightRenderExtractor());
        Mesh firstOldMesh = MeshAsset(11, 1);
        Mesh secondOldMesh = MeshAsset(12, 1);
        Material oldMaterial = MaterialAsset(21, 1);
        Entity firstSource = AddMeshSource(
            mainWorld,
            firstOldMesh,
            boundsExpansion: 1,
            position: Vector3.One);
        Entity secondSource = AddMeshSource(
            mainWorld,
            secondOldMesh,
            boundsExpansion: 2,
            position: new Vector3(2));
        SetSourceBindings(mainWorld, firstSource, oldMaterial, default!, count: 1);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity firstMirror = Mirror(renderWorld, firstSource);
        Entity secondMirror = Mirror(renderWorld, secondSource);
        renderWorld.World.Add(firstMirror, new PipelinePreparedState(47));

        Move(mainWorld, firstSource, new Vector3(10));
        Move(mainWorld, secondSource, new Vector3(20));
        mainWorld.Set(
            firstSource,
            new MeshInstance { Mesh = MeshAsset(31, 2), BoundsExpansion = 3 });
        mainWorld.Set(
            secondSource,
            new MeshInstance { Mesh = MeshAsset(32, 2), BoundsExpansion = 4 });
        SetSourceBindings(
            mainWorld,
            firstSource,
            MaterialAsset(41, 2),
            default!,
            count: 1);

        meshWrites.AfterApply = context =>
        {
            Assert.Same(MeshAsset(31, 2).CaptureData(), context.World.ReadComponentData<RenderMesh>(firstMirror).Mesh);
            Assert.Same(MeshAsset(32, 2).CaptureData(), context.World.ReadComponentData<RenderMesh>(secondMirror).Mesh);
            throw injectedFault;
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));
        Assert.Same(injectedFault, error);

        Assert.Same(firstOldMesh.CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(firstMirror).Mesh);
        Assert.Same(secondOldMesh.CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(secondMirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.World.ReadComponentData<RenderTransform>(firstMirror).Position);
        Assert.Equal(new Vector3(2), renderWorld.World.ReadComponentData<RenderTransform>(secondMirror).Position);
        AssertRenderBindings(renderWorld, firstMirror, oldMaterial, default!, count: 1);
        Assert.Equal(47, renderWorld.World.ReadComponentData<PipelinePreparedState>(firstMirror).Value);
    }

    [Fact]
    public void Extract_LightModuleFaultRollsBackEarlierMeshModuleChanges()
    {
        var injectedFault = new InvalidOperationException("intentional light extraction fault");
        World mainWorld = new();
        RenderWorld renderWorld = new();
        var lightWrites = new ExtractionWrites(new LightRenderExtractor());
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), lightWrites);
        Mesh originalMesh = MeshAsset(43, 1);
        Entity source = AddMeshSource(
            mainWorld,
            originalMesh,
            boundsExpansion: 1,
            position: Vector3.One);
        PointLight originalLight = new(
            Vector3.One,
            8,
            new Vector3(0.5f),
            2,
            0x04u);
        mainWorld.Add(source, originalLight);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Move(mainWorld, source, new Vector3(5, 6, 7));
        mainWorld.Set(
            source,
            new MeshInstance
            {
                Mesh = MeshAsset(44, 2),
                BoundsExpansion = 3,
            });
        mainWorld.Set(
            source,
            originalLight with
            {
                Range = 16,
                Intensity = 7,
            });
        lightWrites.AfterApply = context =>
        {
            Assert.Same(MeshAsset(44, 2).CaptureData(), context.World.ReadComponentData<RenderMesh>(mirror).Mesh);
            Assert.Equal(7, context.World.ReadComponentData<RenderPointLight>(mirror).Intensity);
            throw injectedFault;
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));
        Assert.Same(injectedFault, error);

        Assert.Same(originalMesh.CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(mirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);
        AssertPointLight(originalLight, renderWorld.World.ReadComponentData<RenderPointLight>(mirror));
    }

    [Fact]
    public void Extract_RejectsReentrantExtractionBeforeCollectedValuesCanBeRewritten()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        var meshWrites = new ExtractionWrites(new MeshRenderExtractor());
        using var extraction = new RenderExtractionSystems(renderWorld, meshWrites, new LightRenderExtractor());
        Entity firstSource = AddMeshSource(
            mainWorld,
            MeshAsset(51, 1),
            boundsExpansion: 1,
            position: Vector3.Zero);
        Entity secondSource = AddMeshSource(
            mainWorld,
            MeshAsset(52, 1),
            boundsExpansion: 2,
            position: Vector3.One);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Mesh firstReplacement = MeshAsset(61, 2);
        Mesh secondReplacement = MeshAsset(62, 2);
        mainWorld.Set(
            firstSource,
            new MeshInstance { Mesh = firstReplacement, BoundsExpansion = 3 });
        mainWorld.Set(
            secondSource,
            new MeshInstance { Mesh = secondReplacement, BoundsExpansion = 4 });

        InvalidOperationException? nestedFault = null;
        meshWrites.AfterApply = _ => nestedFault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.NotNull(nestedFault);

        Assert.Equal(
            firstReplacement.CaptureData(),
            renderWorld.World.ReadComponentData<RenderMesh>(Mirror(renderWorld, firstSource)).Mesh);
        Assert.Equal(
            secondReplacement.CaptureData(),
            renderWorld.World.ReadComponentData<RenderMesh>(Mirror(renderWorld, secondSource)).Mesh);

        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Single(RenderSources(renderWorld, firstSource));
        Assert.Single(RenderSources(renderWorld, secondSource));
    }

    [Fact]
    public void Extract_IndexesOneStableRenderSourceMirrorPerMainEntity()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity firstSource = AddMeshSource(
            mainWorld,
            MeshAsset(1, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        Entity secondSource = AddMeshSource(
            mainWorld,
            MeshAsset(2, 1),
            boundsExpansion: 0,
            position: Vector3.One);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity firstMirror = Mirror(renderWorld, firstSource);
        Entity secondMirror = Mirror(renderWorld, secondSource);
        Assert.NotEqual(firstMirror, secondMirror);
        Assert.Equal(firstSource, renderWorld.World.ReadComponentData<RenderSource>(firstMirror).Entity);
        Assert.Equal(secondSource, renderWorld.World.ReadComponentData<RenderSource>(secondMirror).Entity);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(firstMirror, Mirror(renderWorld, firstSource));
        Assert.Equal(secondMirror, Mirror(renderWorld, secondSource));
    }

    [Fact]
    public void Extract_DoesNotInvalidateIndependentInternedQueryAcquisitions()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Query renderSourceQuery = renderWorld.World.Query(
            new QueryDescription().With<RenderSource>());
        Query sourceQuery = mainWorld.Query(SourceExtractionDefinition());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(3, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(source, renderWorld.World.ReadComponentData<RenderSource>(Mirror(renderWorld, source)).Entity);
        Assert.Equal(1, sourceQuery.Count);
        Assert.Equal(1, renderSourceQuery.Count);
    }

    [Fact]
    public void Extract_StoresTransformAndMeshAsSeparateRenderComponents()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Mesh mesh = MeshAsset(17, 3);
        var sourceTransform = new TransformQvvs(
            new Vector3(3, 2, 1),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f),
            2)
        {
            Stretch = new Vector3(1, 2, 3),
        };
        Entity source = mainWorld.Create();
        mainWorld.Add(source, new WorldTransform { Qvvs = sourceTransform });
        mainWorld.Add(source, new MeshInstance { Mesh = mesh, BoundsExpansion = 1.5f });

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.World.Has<RenderTransform>(mirror));
        Assert.True(renderWorld.World.Has<RenderMesh>(mirror));
        RenderTransform transform = renderWorld.World.ReadComponentData<RenderTransform>(mirror);
        RenderMesh renderMesh = renderWorld.World.ReadComponentData<RenderMesh>(mirror);
        Assert.Equal(sourceTransform.Position, transform.Position);
        Assert.Equal(sourceTransform.Rotation, transform.Rotation);
        Assert.Equal(sourceTransform.Scale, transform.Scale);
        Assert.Equal(sourceTransform.Stretch, transform.Stretch);
        Assert.Same(mesh.CaptureData(), renderMesh.Mesh);
        Assert.Equal(1.5f, renderMesh.BoundsExpansion);
    }

    [Fact]
    public void Extract_TranslatesMaterialBindingBuffersAndTracksReplacementAndRemoval()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(5, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        Material first = MaterialAsset(11, 1);
        Material second = MaterialAsset(12, 1);
        Material replacement = MaterialAsset(13, 2);
        SetSourceBindings(mainWorld, source, first, second, count: 2);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.World.Has<Buffer<RenderMaterialBinding>>(mirror));
        Assert.False(renderWorld.World.Has<Buffer<MeshMaterialBinding>>(mirror));
        AssertRenderBindings(renderWorld, mirror, first, second, count: 2);

        SetSourceBindings(mainWorld, source, replacement, default!, count: 1);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        AssertRenderBindings(renderWorld, mirror, replacement, default!, count: 1);

        mainWorld.RemoveBuffer<MeshMaterialBinding>(source);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.False(renderWorld.World.Has<Buffer<RenderMaterialBinding>>(mirror));
    }

    [Fact]
    public void Extract_ValueDeltaMatchesForcedStructuralReference()
    {
        World deltaMainWorld = new();
        World referenceMainWorld = new();
        RenderWorld deltaRenderWorld = new();
        RenderWorld referenceRenderWorld = new();
        using var deltaExtraction = new RenderExtractionSystems(deltaRenderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        using var referenceExtraction = new RenderExtractionSystems(referenceRenderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Mesh initialMesh = MeshAsset(81, 1);
        Material initialMaterial = MaterialAsset(82, 1);
        Entity deltaMeshSource = AddMeshSource(
            deltaMainWorld,
            initialMesh,
            boundsExpansion: 1,
            position: Vector3.One);
        Entity referenceMeshSource = AddMeshSource(
            referenceMainWorld,
            initialMesh,
            boundsExpansion: 1,
            position: Vector3.One);
        SetSourceBindings(
            deltaMainWorld,
            deltaMeshSource,
            initialMaterial,
            default!,
            count: 1);
        SetSourceBindings(
            referenceMainWorld,
            referenceMeshSource,
            initialMaterial,
            default!,
            count: 1);
        PointLight initialPoint = new(
            Vector3.One,
            8,
            new Vector3(0.25f, 0.5f, 1),
            2,
            0x01u);
        LightCookie initialCookie = new(
            TextureAsset(83, 1),
            0.5f,
            new Vector4(1, 1, 0, 0),
            Matrix4x4.Identity);
        deltaMainWorld.Add(deltaMeshSource, initialPoint);
        deltaMainWorld.Add(deltaMeshSource, initialCookie);
        referenceMainWorld.Add(referenceMeshSource, initialPoint);
        referenceMainWorld.Add(referenceMeshSource, initialCookie);

        DirectionalLight initialDirectional = new(
            -Vector3.UnitY,
            Vector3.One,
            1,
            0x02u);
        Entity deltaDirectionalSource = deltaMainWorld.Create(initialDirectional);
        Entity referenceDirectionalSource =
            referenceMainWorld.Create(initialDirectional);
        SpotLight initialSpot = new(
            Vector3.Zero,
            12,
            Vector3.UnitZ,
            0.8f,
            0.4f,
            new Vector3(1, 0.5f, 0.25f),
            3,
            0x04u);
        Entity deltaSpotSource = deltaMainWorld.Create(initialSpot);
        Entity referenceSpotSource = referenceMainWorld.Create(initialSpot);

        ExtractAndApply(deltaExtraction, deltaMainWorld, deltaRenderWorld);
        ExtractAndApply(referenceExtraction, referenceMainWorld, referenceRenderWorld);

        Mesh replacementMesh = MeshAsset(91, 2);
        Material replacementMaterial = MaterialAsset(92, 2);
        MeshInstance replacementInstance = new()
        {
            Mesh = replacementMesh,
            BoundsExpansion = 4,
        };
        Vector3 replacementPosition = new(7, 8, 9);
        PointLight replacementPoint = initialPoint with
        {
            Position = new Vector3(2, 3, 4),
            Range = 32,
            Intensity = 6,
            LayerMask = 0x10u,
        };
        LightCookie replacementCookie = initialCookie with
        {
            Texture = TextureAsset(93, 2),
            Strength = 0.75f,
            ScaleOffset = new Vector4(0.5f, 0.5f, 0.25f, 0.25f),
            WorldToCookie = Matrix4x4.CreateTranslation(1, 2, 3),
        };
        DirectionalLight replacementDirectional = initialDirectional with
        {
            Direction = Vector3.Normalize(new Vector3(1, -2, 3)),
            Intensity = 5,
            LayerMask = 0x20u,
        };
        SpotLight replacementSpot = initialSpot with
        {
            Position = new Vector3(4, 5, 6),
            Range = 24,
            Intensity = 7,
            LayerMask = 0x40u,
        };

        Move(deltaMainWorld, deltaMeshSource, replacementPosition);
        Move(referenceMainWorld, referenceMeshSource, replacementPosition);
        deltaMainWorld.Set(deltaMeshSource, replacementInstance);
        referenceMainWorld.Set(referenceMeshSource, replacementInstance);
        SetSourceBindings(
            deltaMainWorld,
            deltaMeshSource,
            replacementMaterial,
            default!,
            count: 1);
        SetSourceBindings(
            referenceMainWorld,
            referenceMeshSource,
            replacementMaterial,
            default!,
            count: 1);
        deltaMainWorld.Set(deltaMeshSource, replacementPoint);
        referenceMainWorld.Set(referenceMeshSource, replacementPoint);
        deltaMainWorld.Set(deltaMeshSource, replacementCookie);
        referenceMainWorld.Set(referenceMeshSource, replacementCookie);
        deltaMainWorld.Set(deltaDirectionalSource, replacementDirectional);
        referenceMainWorld.Set(referenceDirectionalSource, replacementDirectional);
        deltaMainWorld.Set(deltaSpotSource, replacementSpot);
        referenceMainWorld.Set(referenceSpotSource, replacementSpot);

        Query deltaMembers = deltaMainWorld.Query(new QueryDescription().WithStored<SpotLight>());
        Query referenceMembers = referenceMainWorld.Query(new QueryDescription().WithStored<SpotLight>());
        ulong deltaMembership = deltaMembers.MembershipVersion;
        ulong referenceMembership = referenceMembers.MembershipVersion;
        referenceMainWorld.Remove<SpotLight>(referenceSpotSource);
        referenceMainWorld.Add(referenceSpotSource, replacementSpot);
        Assert.Equal(deltaMembership, deltaMembers.MembershipVersion);
        Assert.True(referenceMembers.MembershipVersion > referenceMembership);

        ExtractAndApply(deltaExtraction, deltaMainWorld, deltaRenderWorld);
        ExtractAndApply(referenceExtraction, referenceMainWorld, referenceRenderWorld);

        Entity deltaMeshMirror = Mirror(deltaRenderWorld, deltaMeshSource);
        Entity referenceMeshMirror = Mirror(referenceRenderWorld, referenceMeshSource);
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderTransform>(referenceMeshMirror),
            deltaRenderWorld.World.ReadComponentData<RenderTransform>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderPreviousTransform>(referenceMeshMirror),
            deltaRenderWorld.World.ReadComponentData<RenderPreviousTransform>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderMesh>(referenceMeshMirror),
            deltaRenderWorld.World.ReadComponentData<RenderMesh>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderPointLight>(referenceMeshMirror),
            deltaRenderWorld.World.ReadComponentData<RenderPointLight>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderLightCookie>(referenceMeshMirror),
            deltaRenderWorld.World.ReadComponentData<RenderLightCookie>(deltaMeshMirror));
        Assert.Equal(
            Vector3.One,
            deltaRenderWorld.World.ReadComponentData<RenderPreviousTransform>(deltaMeshMirror).Value.Position);
        Assert.Equal(
            replacementPosition,
            deltaRenderWorld.World.ReadComponentData<RenderTransform>(deltaMeshMirror).Position);
        AssertRenderBindings(
            deltaRenderWorld,
            deltaMeshMirror,
            replacementMaterial,
            default!,
            count: 1);
        AssertRenderBindings(
            referenceRenderWorld,
            referenceMeshMirror,
            replacementMaterial,
            default!,
            count: 1);

        Entity deltaDirectionalMirror = Mirror(deltaRenderWorld, deltaDirectionalSource);
        Entity referenceDirectionalMirror =
            Mirror(referenceRenderWorld, referenceDirectionalSource);
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderDirectionalLight>(referenceDirectionalMirror),
            deltaRenderWorld.World.ReadComponentData<RenderDirectionalLight>(deltaDirectionalMirror));

        Entity deltaSpotMirror = Mirror(deltaRenderWorld, deltaSpotSource);
        Entity referenceSpotMirror = Mirror(referenceRenderWorld, referenceSpotSource);
        Assert.Equal(
            referenceRenderWorld.World.ReadComponentData<RenderSpotLight>(referenceSpotMirror),
            deltaRenderWorld.World.ReadComponentData<RenderSpotLight>(deltaSpotMirror));
    }

    [Fact]
    public void Extract_CreatesPerEntityLightsAndOptionalCookies()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        DirectionalLight directional = new(
            new Vector3(0, -1, 0),
            new Vector3(1, 0.5f, 0.25f),
            2,
            0x02u);
        PointLight point = new(
            new Vector3(1, 2, 3),
            8,
            new Vector3(0.25f, 1, 0.5f),
            3,
            0x04u);
        SpotLight spot = new(
            new Vector3(4, 5, 6),
            16,
            Vector3.Normalize(new Vector3(0, -1, 1)),
            0.8f,
            0.4f,
            new Vector3(0.1f, 0.2f, 1),
            4,
            0x08u);
        LightCookie directionalCookie = new(
            TextureAsset(31, 1),
            0.5f,
            new Vector4(0.5f, 0.5f, 0, 0),
            Matrix4x4.Identity);
        LightCookie spotCookie = new(
            TextureAsset(32, 1),
            0.75f,
            new Vector4(0.25f, 0.25f, 0.5f, 0),
            Matrix4x4.CreateTranslation(1, 2, 3));
        Entity directionalSource = mainWorld.Create();
        Entity pointSource = mainWorld.Create();
        Entity spotSource = mainWorld.Create();
        mainWorld.Add(directionalSource, directional);
        mainWorld.Add(directionalSource, directionalCookie);
        mainWorld.Add(pointSource, point);
        mainWorld.Add(spotSource, spot);
        mainWorld.Add(spotSource, spotCookie);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity directionalMirror = Mirror(renderWorld, directionalSource);
        Entity pointMirror = Mirror(renderWorld, pointSource);
        Entity spotMirror = Mirror(renderWorld, spotSource);
        AssertDirectionalLight(
            directional,
            renderWorld.World.ReadComponentData<RenderDirectionalLight>(directionalMirror));
        AssertLightCookie(
            directionalCookie,
            renderWorld.World.ReadComponentData<RenderLightCookie>(directionalMirror));
        Assert.False(renderWorld.World.Has<RenderPointLight>(directionalMirror));
        Assert.False(renderWorld.World.Has<RenderSpotLight>(directionalMirror));
        AssertPointLight(point, renderWorld.World.ReadComponentData<RenderPointLight>(pointMirror));
        Assert.False(renderWorld.World.Has<RenderLightCookie>(pointMirror));
        AssertSpotLight(spot, renderWorld.World.ReadComponentData<RenderSpotLight>(spotMirror));
        AssertLightCookie(spotCookie, renderWorld.World.ReadComponentData<RenderLightCookie>(spotMirror));

        DirectionalLight updatedDirectional = directional with { Intensity = 7, LayerMask = 0x10u };
        mainWorld.Set(directionalSource, updatedDirectional);
        mainWorld.Remove<LightCookie>(directionalSource);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(directionalMirror, Mirror(renderWorld, directionalSource));
        AssertDirectionalLight(
            updatedDirectional,
            renderWorld.World.ReadComponentData<RenderDirectionalLight>(directionalMirror));
        Assert.False(renderWorld.World.Has<RenderLightCookie>(directionalMirror));
    }

    [Fact]
    public void Extract_RemovingMeshFacetKeepsSameLightMirror()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(7, 1),
            boundsExpansion: 0.5f,
            position: Vector3.One);
        PointLight point = new(Vector3.One, 10, Vector3.One, 2);
        mainWorld.Add(source, point);
        SetSourceBindings(mainWorld, source, MaterialAsset(21, 1), default!, count: 1);

        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity mirror = Mirror(renderWorld, source);

        mainWorld.Remove<MeshInstance>(source);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.True(renderWorld.World.IsAlive(mirror));
        Assert.Equal(mirror, Mirror(renderWorld, source));
        AssertPointLight(point, renderWorld.World.ReadComponentData<RenderPointLight>(mirror));
        Assert.False(renderWorld.World.Has<RenderTransform>(mirror));
        Assert.False(renderWorld.World.Has<RenderMesh>(mirror));
        Assert.False(renderWorld.World.Has<Buffer<RenderMaterialBinding>>(mirror));
    }

    [Fact]
    public void Extract_PreservesRendererOnlyComponentOnMirror()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(8, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity mirror = Mirror(renderWorld, source);
        renderWorld.World.Add(mirror, new PipelinePreparedState(73));

        Move(mainWorld, source, new Vector3(9, 8, 7));
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.Equal(73, renderWorld.World.ReadComponentData<PipelinePreparedState>(mirror).Value);
        Assert.Equal(new Vector3(9, 8, 7), renderWorld.World.ReadComponentData<RenderTransform>(mirror).Position);
    }

    [Fact]
    public void Extract_PreservesRendererOnlyEntity()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity rendererOnly = renderWorld.World.Create();
        renderWorld.World.Add(rendererOnly, new PipelinePreparedState(91));
        AddMeshSource(
            mainWorld,
            MeshAsset(9, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);

        ExtractAndApply(extraction, mainWorld, renderWorld);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.True(renderWorld.World.IsAlive(rendererOnly));
        Assert.False(renderWorld.World.Has<RenderSource>(rendererOnly));
        Assert.Equal(91, renderWorld.World.ReadComponentData<PipelinePreparedState>(rendererOnly).Value);
    }

    [Fact]
    public void Extract_DestroysMirrorWhenSourceIsDestroyed()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(10, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity mirror = Mirror(renderWorld, source);
        renderWorld.World.Add(mirror, new PipelinePreparedState(12));

        mainWorld.Destroy(source);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.False(mainWorld.IsAlive(source));
        Assert.False(renderWorld.World.IsAlive(mirror));
        Assert.Empty(RenderSources(renderWorld, source));
    }

    [Fact]
    public void Extract_TopologyFaultRollsBackCreatedAndDestroyedMirrorsThenCanRetry()
    {
        var injectedFault = new InvalidOperationException("intentional mirror topology fault");
        World mainWorld = new();
        RenderWorld renderWorld = new();
        var meshWrites = new ExtractionWrites(new MeshRenderExtractor());
        using var extraction = new RenderExtractionSystems(renderWorld, meshWrites, new LightRenderExtractor());
        Entity removedSource = AddMeshSource(
            mainWorld,
            MeshAsset(73, 1),
            boundsExpansion: 1,
            position: Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity removedMirror = Mirror(renderWorld, removedSource);
        renderWorld.World.Add(removedMirror, new PipelinePreparedState(144));

        mainWorld.Destroy(removedSource);
        Entity addedSource = AddMeshSource(
            mainWorld,
            MeshAsset(74, 1),
            boundsExpansion: 2,
            position: Vector3.One);
        meshWrites.AfterApply = context =>
        {
            Assert.True(context.World.Has<RenderMesh>(context.RequireMirror(addedSource)));
            context.World.Destroy(removedMirror);
            throw injectedFault;
        };

        InvalidOperationException fault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));
        Assert.Same(injectedFault, fault);

        Assert.True(renderWorld.World.IsAlive(removedMirror));
        Assert.Equal(removedMirror, Mirror(renderWorld, removedSource));
        Assert.Equal(144, renderWorld.World.ReadComponentData<PipelinePreparedState>(removedMirror).Value);
        Assert.Empty(RenderSources(renderWorld, addedSource));

        meshWrites.AfterApply = null;
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.False(renderWorld.World.IsAlive(removedMirror));
        Assert.Empty(RenderSources(renderWorld, removedSource));
        Entity addedMirror = Mirror(renderWorld, addedSource);
        Assert.Same(MeshAsset(74, 1).CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(addedMirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.World.ReadComponentData<RenderTransform>(addedMirror).Position);
    }

    [Fact]
    public void Extract_DoesNotWriteRenderLinksOrSnapshotsIntoMainWorld()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(11, 1),
            boundsExpansion: 0.75f,
            position: Vector3.One);
        SetSourceBindings(mainWorld, source, MaterialAsset(31, 1), default!, count: 1);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.True(mainWorld.Has<WorldTransform>(source));
        Assert.True(mainWorld.Has<MeshInstance>(source));
        Assert.True(mainWorld.Has<Buffer<MeshMaterialBinding>>(source));
        Assert.False(mainWorld.Has<RenderSource>(source));
        Assert.False(mainWorld.Has<RenderTransform>(source));
        Assert.False(mainWorld.Has<RenderMesh>(source));
        Assert.False(mainWorld.Has<Buffer<RenderMaterialBinding>>(source));
    }

    [Fact]
    public void Extract_FirstFailureDoesNotBindSourceWorld()
    {
        var injectedFault = new InvalidOperationException("intentional first extraction fault");
        World rejectedMainWorld = new();
        World acceptedMainWorld = new();
        RenderWorld renderWorld = new();
        var meshWrites = new ExtractionWrites(new MeshRenderExtractor());
        using var extraction = new RenderExtractionSystems(renderWorld, meshWrites, new LightRenderExtractor());
        Entity rejectedSource = AddMeshSource(
            rejectedMainWorld,
            MeshAsset(71, 1),
            boundsExpansion: 1,
            position: Vector3.Zero);
        acceptedMainWorld.Create();
        Entity acceptedSource = AddMeshSource(
            acceptedMainWorld,
            MeshAsset(72, 1),
            boundsExpansion: 2,
            position: Vector3.One);
        meshWrites.AfterApply = _ => throw injectedFault;

        InvalidOperationException firstFault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, rejectedMainWorld, renderWorld));
        Assert.Same(injectedFault, firstFault);

        Assert.Empty(RenderSources(renderWorld, rejectedSource));

        meshWrites.AfterApply = null;
        ExtractAndApply(extraction, acceptedMainWorld, renderWorld);

        Entity acceptedMirror = Mirror(renderWorld, acceptedSource);
        Assert.Same(MeshAsset(72, 1).CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(acceptedMirror).Mesh);
        Assert.Empty(RenderSources(renderWorld, rejectedSource));
        InvalidOperationException bindingFault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, rejectedMainWorld, renderWorld));

        Assert.True(renderWorld.World.IsAlive(acceptedMirror));
    }

    [Fact]
    public void Extract_BindsRenderWorldToOneMainWorld()
    {
        World firstMainWorld = new();
        World secondMainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity firstSource = AddMeshSource(
            firstMainWorld,
            MeshAsset(41, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        AddMeshSource(
            secondMainWorld,
            MeshAsset(42, 1),
            boundsExpansion: 0,
            position: Vector3.One);

        ExtractAndApply(extraction, firstMainWorld, renderWorld);
        Entity firstMirror = Mirror(renderWorld, firstSource);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, secondMainWorld, renderWorld));

        Assert.True(renderWorld.World.IsAlive(firstMirror));
        Assert.Same(MeshAsset(41, 1).CaptureData(), renderWorld.World.ReadComponentData<RenderMesh>(firstMirror).Mesh);
    }

    [Fact]
    public void Extract_RejectsUsingRenderWorldAsItsOwnMainWorld()
    {
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ExtractAndApply(extraction, renderWorld.World, renderWorld));

        Assert.Equal("mainWorld", error.ParamName);

    }

    [Fact]
    public void ExtractionFeatureRegistersItsOwnReadsAndWrites()
    {
        World mainWorld = new();
        Entity source = mainWorld.Create();
        mainWorld.Add(source, new CustomExtractValue(37));
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        var custom = new CustomExtractionSystem();
        extraction.Add(custom);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.Equal(37, renderWorld.World.ReadComponentData<CustomRenderValue>(mirror).Value);
        mainWorld.Set(source, new CustomExtractValue(53));
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.Equal(53, renderWorld.World.ReadComponentData<CustomRenderValue>(mirror).Value);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(53, renderWorld.World.ReadComponentData<CustomRenderValue>(mirror).Value);
        Assert.Throws<InvalidOperationException>(() =>
            extraction.Add(new CustomExtractionSystem()));
    }

    [Fact]
    public void Extract_RejectsAnotherRenderWorldAsTheMainWorld()
    {
        using RenderWorld source = new();
        using RenderWorld destination = new();
        using var extraction = new RenderExtractionSystems(destination, new MeshRenderExtractor());

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ExtractAndApply(extraction, source.World, destination));

        Assert.Equal("mainWorld", error.ParamName);
    }

    private void ExtractAndApply(RenderExtractionSystems extraction, World mainWorld, RenderWorld renderWorld)
    {
        extraction.Extract(mainWorld);
        Assert.True(graphics.Apply(extraction, renderWorld));
    }

    private static Entity AddMeshSource(
        World world,
        Mesh mesh,
        float boundsExpansion,
        Vector3 position)
    {
        Entity entity = world.Create();
        world.Add(
            entity,
            new WorldTransform
            {
                Qvvs = new TransformQvvs(position, Quaternion.Identity, 1),
            });
        world.Add(entity, new MeshInstance { Mesh = mesh, BoundsExpansion = boundsExpansion });
        return entity;
    }

    private static QueryDescription SourceExtractionDefinition() =>
        new QueryDescription()
            .Optional<WorldTransform>()
            .Optional<MeshInstance>()
            .Optional<DirectionalLight>()
            .Optional<PointLight>()
            .Optional<SpotLight>()
            .Optional<LightCookie>()
            .Optional<Buffer<MeshMaterialBinding>>()
            ;

    private static void Move(World world, Entity entity, Vector3 position)
    {
        WorldTransform transform = world.ReadComponentData<WorldTransform>(entity);
        transform.Qvvs = new TransformQvvs(position, Quaternion.Identity, 1);
        world.Set(entity, transform);
    }

    private static Entity[] RenderSources(RenderWorld world, Entity source)
    {
        using var members = world.World.Index<RenderSource, Entity>().Find(source);
        return members.Entities.ToArray();
    }

    private static Entity Mirror(RenderWorld renderWorld, Entity source)
    {
        ReadOnlySpan<Entity> matches = RenderSources(renderWorld, source);
        Assert.Equal(1, matches.Length);
        return matches[0];
    }

    private static void SetSourceBindings(
        World world,
        Entity entity,
        Material first,
        Material second,
        int count)
    {
        Assert.InRange(count, 0, 2);
        if (!world.Has<Buffer<MeshMaterialBinding>>(entity))
            world.AddBuffer<MeshMaterialBinding>(entity);

        using DynamicBuffer<MeshMaterialBinding> buffer = world.GetBuffer<MeshMaterialBinding>(entity);
        buffer.Clear();
        if (count > 0) buffer.Add(new MeshMaterialBinding(first));
        if (count > 1) buffer.Add(new MeshMaterialBinding(second));
    }

    private static void AssertRenderBindings(
        RenderWorld renderWorld,
        Entity entity,
        Material first,
        Material second,
        int count)
    {
        if (count > 0) Assert.Same(first, renderWorld.ReadMaterial(first.MaterialId));
        if (count > 1) Assert.Same(second, renderWorld.ReadMaterial(second.MaterialId));
        using ReadOnlyBuffer<RenderMaterialBinding> buffer = renderWorld.World.ReadBuffer<RenderMaterialBinding>(entity);
        ReadOnlySpan<RenderMaterialBinding> bindings = buffer.AsSpan();
        Assert.Equal(count, bindings.Length);
        if (count > 0) Assert.Equal(first.MaterialId, bindings[0].MaterialId);
        if (count > 1) Assert.Equal(second.MaterialId, bindings[1].MaterialId);
    }

    private static void AssertDirectionalLight(
        in DirectionalLight expected,
        in RenderDirectionalLight actual)
    {
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Intensity, actual.Intensity);
        Assert.Equal(expected.LayerMask, actual.LayerMask);
    }

    private static void AssertPointLight(in PointLight expected, in RenderPointLight actual)
    {
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Range, actual.Range);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Intensity, actual.Intensity);
        Assert.Equal(expected.LayerMask, actual.LayerMask);
    }

    private static void AssertSpotLight(in SpotLight expected, in RenderSpotLight actual)
    {
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Range, actual.Range);
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.InnerConeCos, actual.InnerConeCos);
        Assert.Equal(expected.OuterConeCos, actual.OuterConeCos);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Intensity, actual.Intensity);
        Assert.Equal(expected.LayerMask, actual.LayerMask);
    }

    private static void AssertLightCookie(in LightCookie expected, in RenderLightCookie actual)
    {
        Assert.Equal(expected.Texture, actual.Texture);
        Assert.Equal(expected.Strength, actual.Strength);
        Assert.Equal(expected.ScaleOffset, actual.ScaleOffset);
        Assert.Equal(expected.WorldToCookie, actual.WorldToCookie);
    }

    private readonly record struct PipelinePreparedState(int Value) : SomeEngine.ECS.IComponent;

    private readonly record struct CustomExtractValue(int Value) :
        SomeEngine.ECS.IComponent, ITrackedComponent;

    private sealed class MeshReadObserver(Action<MeshInstance> observe) : IRenderExtractionSystem
    {
        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<MeshInstance>();
        }

        public void Reset() { }
        public void Collect(QueryChunk chunk)
        {
            if (chunk.TryRead<MeshInstance>(out var values))
                foreach (var value in values) observe(value);
        }
        public bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context)
        {
            Collect(chunk);
            return false;
        }
        public void Apply(RenderExtractionContext context) { }
        public void ApplyChanges(RenderExtractionContext context) { }
    }

    private readonly record struct CustomRenderValue(int Value) :
        SomeEngine.ECS.IComponent;

    private sealed class CustomExtractionSystem : IRenderExtractionSystem
    {
        private readonly List<(Entity Source, int Value)> _values = [];

        public void DeclareReads(RenderExtractionQuery query) =>
            query.ReadOptional<CustomExtractValue>();

        public void Reset() => _values.Clear();

        public void Collect(QueryChunk chunk)
        {
            if (!chunk.TryRead<CustomExtractValue>(out ReadOnlySpan<CustomExtractValue> values))
                return;
            ReadOnlySpan<Entity> entities = chunk.Entities;
            for (int row = 0; row < entities.Length; row++)
                _values.Add((entities[row], values[row].Value));
        }

        public bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context)
        {
            if (!chunk.Has<CustomExtractValue>() || !chunk.WasWritten<CustomExtractValue>(since))
                return false;
            Collect(chunk);
            return true;
        }

        public void ApplyChanges(RenderExtractionContext context)
        {
            foreach (var value in _values)
                context.UpdateExisting(context.RequireMirror(value.Source), new CustomRenderValue(value.Value));
        }

        public void Apply(RenderExtractionContext context)
        {
            for (int index = 0; index < _values.Count; index++)
            {
                (Entity source, int value) = _values[index];
                Entity mirror = context.RetainMirror(source);
                context.Upsert(mirror, new CustomRenderValue(value));
            }
        }
    }

    private sealed class ExtractionWrites(IRenderExtractionSystem inner) : IRenderExtractionSystem
    {
        public Action<RenderExtractionContext>? AfterApply { get; set; }
        public void DeclareReads(RenderExtractionQuery query) => inner.DeclareReads(query);
        public void Reset() => inner.Reset();
        public void CollectResources(scoped QueryScope source, RenderExtractionResources resources) => inner.CollectResources(source, resources);
        public void Collect(QueryChunk chunk) => inner.Collect(chunk);
        public bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context) => inner.CollectChanges(chunk, since, context);
        public bool CompleteSourceCollection(World world, bool fullCollection) => inner.CompleteSourceCollection(world, fullCollection);
        public bool CompleteCollection(RenderExtractionContext context, bool fullCollection) => inner.CompleteCollection(context, fullCollection);
        public void Apply(RenderExtractionContext context)
        {
            inner.Apply(context);
            AfterApply?.Invoke(context);
        }
        public void ApplyChanges(RenderExtractionContext context)
        {
            inner.ApplyChanges(context);
            AfterApply?.Invoke(context);
        }
        public void Dispose() => inner.Dispose();
    }

    private readonly record struct MaterialBindingExpectation(
        Material First,
        Material Second,
        int Count);

    private Mesh MeshAsset(int id, int revision)
        => assets.Mesh(id, revision);

    private Material MaterialAsset(int id, int revision)
        => assets.Material(id, revision);

    private Texture TextureAsset(int id, int revision)
        => assets.Texture(id, revision);
}
