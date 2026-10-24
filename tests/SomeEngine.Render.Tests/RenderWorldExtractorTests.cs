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
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
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
        Entity custom = main.CreateEntity(new CustomGeometry(material));
        ExtractAndApply(extraction, main, render);

        main.DestroyEntity(mesh);
        ExtractAndApply(extraction, main, render);
        Assert.Same(material, render.ReadMaterial(material.MaterialId));

        main.DestroyEntity(custom);
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
        public void CollectResources(scoped QueryCursor source, RenderExtractionResources resources)
        {
            foreach (QueryChunkView chunk in source.Chunks)
                if (chunk.TryRead<CustomGeometry>(out var values))
                    foreach (var value in values) resources.Add(value.Material);
        }
        public void Reset() => _values.Clear();
        public void Collect(QueryChunkView chunk)
        {
            if (!chunk.TryRead<CustomGeometry>(out var values)) return;
            for (int index = 0; index < values.Length; index++)
                _values.Add((chunk.Entities[index], values[index].Material));
        }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) => false;
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
        Mesh before = render.Read<RenderMesh>(mirror).Mesh;
        Assert.True(complete.TryGetPage(0, out var page));

        mesh.AddPages([(0, page)], mesh.Revision);
        extraction.Extract(game);
        Assert.Same(before, render.Read<RenderMesh>(mirror).Mesh);
        graphics.Apply(extraction, render);

        Mesh after = render.Read<RenderMesh>(mirror).Mesh;
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
        uint version = render.AcquireSystemVersion();
        int materialWrites = 0;
        render.Hooks<RenderMaterialReference>().OnReplace((DeferredWorld _, Entity _, in RenderMaterialReference _) => materialWrites++);
        material.Roughness = 0.9f;
        extraction.Extract(main);
        Assert.Same(before, render.ReadMaterial(material.MaterialId));
        Assert.Equal(materialVersion, render.ReadMaterialRevision(material.MaterialId));
        graphics.Apply(extraction, render);
        Assert.Same(material, render.ReadMaterial(material.MaterialId));
        Assert.Equal(0.9f, Assert.IsType<StandardMaterial>(render.ReadMaterial(material.MaterialId)).Roughness);
        Assert.Equal(material.Revision, render.ReadMaterialRevision(material.MaterialId));
        Assert.Equal(1, materialWrites);
        QueryHandle query = render.Query(new QueryDefinitionBuilder().ReadBuffer<RenderMaterialBinding>());
        try
        {
            bool bindingsChanged = false;
            render.ExecuteQuery(query, version, ref bindingsChanged,
                static (scoped QueryCursor rows, ref bool changed) =>
                {
                    foreach (QueryChunkView chunk in rows.Chunks)
                        changed |= chunk.HasBufferChangedSinceLastSystemVersion<RenderMaterialBinding>();
                });
            Assert.False(bindingsChanged);
        }
        finally { render.ReleaseQuery(query); }
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
        main.DestroyEntity(source);
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
        int writes = 0;
        render.Hooks<RenderMaterialReference>().OnReplace((DeferredWorld _, Entity _, in RenderMaterialReference _) => writes++);
        render.Hooks<RenderLightCookie>().OnReplace((DeferredWorld _, Entity _, in RenderLightCookie _) => writes++);
        ulong materialRevision = material.Revision;
        ulong textureRevision = texture.Revision;
        TextureVersions before = render.ReadTextureVersions(texture);
        await texture.UpdateAsync(TextureAsset(92, 1));
        extraction.Extract(main);
        Assert.Equal(before, render.ReadTextureVersions(texture));
        graphics.Apply(extraction, render);
        Assert.Equal(textureRevision + 1, texture.Revision);
        Assert.Equal(materialRevision, material.Revision);
        Assert.Equal(0, writes);
        Assert.Equal(texture.Versions, render.ReadTextureVersions(texture));
        Assert.Same(material, render.ReadMaterial(material.MaterialId));
        Assert.Same(texture, material.AlbedoMap);
        Assert.Same(texture, render.Read<RenderLightCookie>(mirror).Texture);
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
                Assert.Equal(positions[index], renderWorld.Read<RenderTransform>(mirrors[index]).Position);
                Assert.Equal(previous[index], renderWorld.Read<RenderPreviousTransform>(mirrors[index]).Value.Position);
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
        renderWorld.Remove<RenderPreviousTransform>(secondMirror);
        for (int index = 0; index < count; index++) Move(mainWorld, sources[index], new Vector3(index + 3));
        Assert.Throws<InvalidOperationException>(() => ExtractAndApply(extraction, mainWorld, renderWorld));
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(new Vector3(index + 1), renderWorld.Read<RenderTransform>(mirrors[index]).Position);
            if (index < count - 1)
                Assert.Equal(new Vector3(index + 1), renderWorld.Read<RenderPreviousTransform>(mirrors[index]).Value.Position);
        }
        renderWorld.Add(secondMirror, new RenderPreviousTransform(renderWorld.Read<RenderTransform>(secondMirror)));
        ExtractAndApply(extraction, mainWorld, renderWorld);
        for (int index = 0; index < count; index++)
        {
            Assert.Equal(new Vector3(index + 3), renderWorld.Read<RenderTransform>(mirrors[index]).Position);
            Assert.Equal(new Vector3(index + 1), renderWorld.Read<RenderPreviousTransform>(mirrors[index]).Value.Position);
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
            Assert.Equal(position, renderWorld.Read<RenderTransform>(mirror).Position);
            Assert.Equal(previous, renderWorld.Read<RenderPreviousTransform>(mirror).Value.Position);
            previous = position;
        }
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(previous, renderWorld.Read<RenderPreviousTransform>(mirror).Value.Position);
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
        Entity mirror = renderWorld.GetByIndex<RenderSource, Entity>(source)[0];
        WorldTransform moved = mainWorld.Read<WorldTransform>(source);
        moved.Qvvs.Position = new Vector3(3, 4, 5);
        mainWorld.Replace(source, moved);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(new Vector3(3, 4, 5), renderWorld.Read<RenderTransform>(mirror).Position);
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
        var entity = game.CreateEntity();
        game.Add(entity, new SomeEngine.Core.ECS.Components.WorldTransform
        {
            Qvvs = new SomeEngine.Core.Math.TransformQvvs(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity),
        });
        game.Add(entity, new SomeEngine.Render.Components.MeshInstance { Mesh = mesh, PageSource = first });
        extraction.Extract(game);
        Assert.True(graphics.Apply(extraction, render));
        var mirror = render.GetByIndex<SomeEngine.Render.Components.RenderSource, SomeEngine.ECS.Entities.Entity>(entity)[0];
        Assert.Same(first, render.Read<SomeEngine.Render.Components.RenderMesh>(mirror).PageSource);
        game.Replace(entity, new SomeEngine.Render.Components.MeshInstance { Mesh = mesh, PageSource = second });
        extraction.Extract(game);
        Assert.True(graphics.Apply(extraction, render));
        Assert.Same(second, render.Read<SomeEngine.Render.Components.RenderMesh>(mirror).PageSource);
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
        QueryHandle query = renderWorld.Query(new QueryDefinitionBuilder()
            .Read<RenderSource>().Read<RenderTransform>().Read<RenderPreviousTransform>());
        static void ReadVersions(scoped QueryCursor cursor, ref Dictionary<Entity, (uint, uint)> versions)
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                ReadOnlySpan<RenderSource> source = chunk.Read<RenderSource>();
                ReadOnlySpan<uint> current = chunk.ReadWriteVersions<RenderTransform>();
                ReadOnlySpan<uint> previous = chunk.ReadWriteVersions<RenderPreviousTransform>();
                for (int row = 0; row < source.Length; row++)
                    versions[source[row].Entity] = (current[row], previous[row]);
            }
        }
        Dictionary<Entity, (uint, uint)> before = [];
        renderWorld.ExecuteQuery(query, ref before, ReadVersions);
        mainWorld.Replace(sources[3], new WorldTransform { Qvvs = new TransformQvvs(new Vector3(3, 1, 0), Quaternion.Identity) });
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Dictionary<Entity, (uint, uint)> after = [];
        renderWorld.ExecuteQuery(query, ref after, ReadVersions);
        foreach (Entity source in sources)
            if (source == sources[3]) Assert.NotEqual(before[source], after[source]);
            else Assert.Equal(before[source], after[source]);
        Entity changed = Mirror(renderWorld, sources[3]);
        Assert.Equal(new Vector3(3, 1, 0), renderWorld.Read<RenderTransform>(changed).Position);
        Assert.Equal(new Vector3(3, 0, 0), renderWorld.Read<RenderPreviousTransform>(changed).Value.Position);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(renderWorld.Read<RenderTransform>(changed),
            renderWorld.Read<RenderPreviousTransform>(changed).Value);
        renderWorld.ReleaseQuery(query);
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
        Assert.True(renderWorld.IsAlive(mirror));

        Move(mainWorld, source, new Vector3(4, 5, 6));
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(new Vector3(4, 5, 6), renderWorld.Read<RenderTransform>(mirror).Position);
    }

    [Fact]
    public async Task Extract_WaitsForActiveMainWorldMutationBeforeReadingSourceImage()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
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
        extractionSystems.Add(new MeshReadObserver(extractionEntered, value =>
        {
            Assert.Same(replacement.Mesh, value.Mesh);
            Assert.Equal(replacement.BoundsExpansion, value.BoundsExpansion);
            events.Enqueue("extraction observed committed mesh");
        }));
        mainWorld.Hooks<MeshInstance>().OnReplace(
            (DeferredWorld _, Entity _, in MeshInstance _) =>
            {
                mutationEntered.Set();
                Assert.True(releaseMutation.Wait(TimeSpan.FromSeconds(10)));
            });
        mainWorld.Hooks<MeshInstance>().OnInsert(
            (DeferredWorld _, Entity _, in MeshInstance value) =>
            {
                Assert.Same(replacement.Mesh, value.Mesh);
                events.Enqueue("mesh replacement committed");
            });
        Task mutation = Task.Run(() => mainWorld.Replace(source, replacement));
        Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(5)));

        Task extraction = Task.Run(() => extractionSystems.Extract(mainWorld));
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

        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(source).Length);
        Assert.True(graphics.Apply(extractionSystems, renderWorld));
        Entity mirror = Mirror(renderWorld, source);
        Assert.Same(replacement.Mesh.CaptureData(), renderWorld.Read<RenderMesh>(mirror).Mesh);
        Assert.Equal(replacement.BoundsExpansion, renderWorld.Read<RenderMesh>(mirror).BoundsExpansion);
    }

    [Fact]
    public async Task NextGameExtractionCompletesWhileCurrentRenderSnapshotRemainsOpen()
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
        QueryHandle renderQuery = renderWorld.Query(
            new QueryDefinitionBuilder().Read<RenderTransform>());
        using ManualResetEventSlim renderCpuEntered = new();
        using ManualResetEventSlim releaseRenderCpu = new();
        Task renderCpu = Task.CompletedTask;

        try
        {
            renderCpu = Task.Run(() => renderWorld.ExecuteReadSnapshot(
                renderQuery,
                cursor =>
                {
                    int rowCount = 0;
                    foreach (QueryRow row in cursor.Rows)
                    {
                        Assert.Equal(Vector3.Zero, row.Read<RenderTransform>().Position);
                        rowCount++;
                    }
                    Assert.Equal(1, rowCount);
                    renderCpuEntered.Set();
                    Assert.True(releaseRenderCpu.Wait(TimeSpan.FromSeconds(10)));
                }));
            Assert.True(renderCpuEntered.Wait(TimeSpan.FromSeconds(5)));

            Vector3 nextPosition = new(7, 8, 9);
            Move(mainWorld, source, nextPosition);
            Task nextGameExtraction = Task.Run(() => extraction.Extract(mainWorld));
            Task winner = await Task.WhenAny(
                nextGameExtraction,
                Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(nextGameExtraction, winner);
            await nextGameExtraction;
            Assert.Equal(Vector3.Zero, renderWorld.Read<RenderTransform>(mirror).Position);

            releaseRenderCpu.Set();
            await renderCpu;
            Assert.True(graphics.Apply(extraction, renderWorld));
            Assert.Equal(nextPosition, renderWorld.Read<RenderTransform>(mirror).Position);
        }
        finally
        {
            releaseRenderCpu.Set();
            await renderCpu;
            renderWorld.ReleaseQuery(renderQuery);
        }
    }

    [Fact]
    public void Extract_ApplyFaultKeepsPublishedRootUnchanged()
    {
        var injectedFault = new InvalidOperationException("intentional extraction apply fault");
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
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
        renderWorld.Add(firstMirror, new PipelinePreparedState(47));

        Move(mainWorld, firstSource, new Vector3(10));
        Move(mainWorld, secondSource, new Vector3(20));
        mainWorld.Replace(
            firstSource,
            new MeshInstance { Mesh = MeshAsset(31, 2), BoundsExpansion = 3 });
        mainWorld.Replace(
            secondSource,
            new MeshInstance { Mesh = MeshAsset(32, 2), BoundsExpansion = 4 });
        SetSourceBindings(
            mainWorld,
            firstSource,
            MaterialAsset(41, 2),
            default!,
            count: 1);

        int replaceCount = 0;
        renderWorld.Hooks<RenderMesh>().OnReplace(
            (DeferredWorld _, Entity _, in RenderMesh _) =>
            {
                if (Interlocked.Increment(ref replaceCount) == 2)
                    throw injectedFault;
            });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));
        Assert.Same(injectedFault, error);

        Assert.Equal(2, replaceCount);
        Assert.Same(firstOldMesh.CaptureData(), renderWorld.Read<RenderMesh>(firstMirror).Mesh);
        Assert.Same(secondOldMesh.CaptureData(), renderWorld.Read<RenderMesh>(secondMirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.Read<RenderTransform>(firstMirror).Position);
        Assert.Equal(new Vector3(2), renderWorld.Read<RenderTransform>(secondMirror).Position);
        AssertRenderBindings(renderWorld, firstMirror, oldMaterial, default!, count: 1);
        Assert.Equal(47, renderWorld.Read<PipelinePreparedState>(firstMirror).Value);
    }

    [Fact]
    public void Extract_LightModuleFaultRollsBackEarlierMeshModuleChanges()
    {
        var injectedFault = new InvalidOperationException("intentional light extraction fault");
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
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
        mainWorld.Replace(
            source,
            new MeshInstance
            {
                Mesh = MeshAsset(44, 2),
                BoundsExpansion = 3,
            });
        mainWorld.Replace(
            source,
            originalLight with
            {
                Range = 16,
                Intensity = 7,
            });
        renderWorld.Hooks<RenderPointLight>().OnReplace(
            (DeferredWorld _, Entity _, in RenderPointLight _) =>
                throw injectedFault);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));
        Assert.Same(injectedFault, error);

        Assert.Same(originalMesh.CaptureData(), renderWorld.Read<RenderMesh>(mirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.Read<RenderTransform>(mirror).Position);
        AssertPointLight(originalLight, renderWorld.Read<RenderPointLight>(mirror));
    }

    [Fact]
    public void Extract_RejectsReentrantHookBeforeScratchCanBeRewritten()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
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
        mainWorld.Replace(
            firstSource,
            new MeshInstance { Mesh = firstReplacement, BoundsExpansion = 3 });
        mainWorld.Replace(
            secondSource,
            new MeshInstance { Mesh = secondReplacement, BoundsExpansion = 4 });

        int replaceCount = 0;
        InvalidOperationException? nestedFault = null;
        renderWorld.Hooks<RenderMesh>().OnReplace(
            (DeferredWorld _, Entity _, in RenderMesh _) =>
            {
                if (Interlocked.Increment(ref replaceCount) != 1)
                    return;

                try
                {
                    ExtractAndApply(extraction, mainWorld, renderWorld);
                }
                catch (InvalidOperationException exception)
                {
                    nestedFault = exception;
                }
            });

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.NotNull(nestedFault);

        Assert.Equal(2, replaceCount);
        Assert.Equal(
            firstReplacement.CaptureData(),
            renderWorld.Read<RenderMesh>(Mirror(renderWorld, firstSource)).Mesh);
        Assert.Equal(
            secondReplacement.CaptureData(),
            renderWorld.Read<RenderMesh>(Mirror(renderWorld, secondSource)).Mesh);

        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(1, renderWorld.GetByIndex<RenderSource, Entity>(firstSource).Length);
        Assert.Equal(1, renderWorld.GetByIndex<RenderSource, Entity>(secondSource).Length);
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
        Assert.Equal(firstSource, renderWorld.Read<RenderSource>(firstMirror).Entity);
        Assert.Equal(secondSource, renderWorld.Read<RenderSource>(secondMirror).Entity);

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
        QueryHandle renderSourceQuery = renderWorld.Query(
            new QueryDefinitionBuilder().Read<RenderSource>());
        QueryHandle sourceQuery = mainWorld.Query(SourceExtractionDefinition());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(3, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);

        renderWorld.ReleaseQuery(renderSourceQuery);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(source, renderWorld.Read<RenderSource>(Mirror(renderWorld, source)).Entity);
        Assert.Equal(SourceExtractionDefinition().Key, mainWorld.GetQueryDefinition(sourceQuery).Key);
        mainWorld.ReleaseQuery(sourceQuery);
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
        Entity source = mainWorld.CreateEntity();
        mainWorld.Add(source, new WorldTransform { Qvvs = sourceTransform });
        mainWorld.Add(source, new MeshInstance { Mesh = mesh, BoundsExpansion = 1.5f });

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.Has<RenderTransform>(mirror));
        Assert.True(renderWorld.Has<RenderMesh>(mirror));
        RenderTransform transform = renderWorld.Read<RenderTransform>(mirror);
        RenderMesh renderMesh = renderWorld.Read<RenderMesh>(mirror);
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
        Assert.True(renderWorld.HasBuffer<RenderMaterialBinding>(mirror));
        Assert.False(renderWorld.HasBuffer<MeshMaterialBinding>(mirror));
        AssertRenderBindings(renderWorld, mirror, first, second, count: 2);

        SetSourceBindings(mainWorld, source, replacement, default!, count: 1);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        AssertRenderBindings(renderWorld, mirror, replacement, default!, count: 1);

        mainWorld.RemoveBuffer<MeshMaterialBinding>(source);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.False(renderWorld.HasBuffer<RenderMaterialBinding>(mirror));
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
        Entity deltaDirectionalSource = deltaMainWorld.CreateEntity(initialDirectional);
        Entity referenceDirectionalSource =
            referenceMainWorld.CreateEntity(initialDirectional);
        SpotLight initialSpot = new(
            Vector3.Zero,
            12,
            Vector3.UnitZ,
            0.8f,
            0.4f,
            new Vector3(1, 0.5f, 0.25f),
            3,
            0x04u);
        Entity deltaSpotSource = deltaMainWorld.CreateEntity(initialSpot);
        Entity referenceSpotSource = referenceMainWorld.CreateEntity(initialSpot);

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
        deltaMainWorld.Replace(deltaMeshSource, replacementInstance);
        referenceMainWorld.Replace(referenceMeshSource, replacementInstance);
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
        deltaMainWorld.Replace(deltaMeshSource, replacementPoint);
        referenceMainWorld.Replace(referenceMeshSource, replacementPoint);
        deltaMainWorld.Replace(deltaMeshSource, replacementCookie);
        referenceMainWorld.Replace(referenceMeshSource, replacementCookie);
        deltaMainWorld.Replace(deltaDirectionalSource, replacementDirectional);
        referenceMainWorld.Replace(referenceDirectionalSource, replacementDirectional);
        deltaMainWorld.Replace(deltaSpotSource, replacementSpot);
        referenceMainWorld.Replace(referenceSpotSource, replacementSpot);

        long deltaTopology = deltaMainWorld.PublishedTopologyRevision;
        Entity irrelevant = referenceMainWorld.CreateEntity();
        referenceMainWorld.DestroyEntity(irrelevant);
        Assert.Equal(deltaTopology, deltaMainWorld.PublishedTopologyRevision);
        Assert.NotEqual(deltaTopology, referenceMainWorld.PublishedTopologyRevision);

        ExtractAndApply(deltaExtraction, deltaMainWorld, deltaRenderWorld);
        ExtractAndApply(referenceExtraction, referenceMainWorld, referenceRenderWorld);

        Entity deltaMeshMirror = Mirror(deltaRenderWorld, deltaMeshSource);
        Entity referenceMeshMirror = Mirror(referenceRenderWorld, referenceMeshSource);
        Assert.Equal(
            referenceRenderWorld.Read<RenderTransform>(referenceMeshMirror),
            deltaRenderWorld.Read<RenderTransform>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.Read<RenderPreviousTransform>(referenceMeshMirror),
            deltaRenderWorld.Read<RenderPreviousTransform>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.Read<RenderMesh>(referenceMeshMirror),
            deltaRenderWorld.Read<RenderMesh>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.Read<RenderPointLight>(referenceMeshMirror),
            deltaRenderWorld.Read<RenderPointLight>(deltaMeshMirror));
        Assert.Equal(
            referenceRenderWorld.Read<RenderLightCookie>(referenceMeshMirror),
            deltaRenderWorld.Read<RenderLightCookie>(deltaMeshMirror));
        Assert.Equal(
            Vector3.One,
            deltaRenderWorld.Read<RenderPreviousTransform>(deltaMeshMirror).Value.Position);
        Assert.Equal(
            replacementPosition,
            deltaRenderWorld.Read<RenderTransform>(deltaMeshMirror).Position);
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
            referenceRenderWorld.Read<RenderDirectionalLight>(referenceDirectionalMirror),
            deltaRenderWorld.Read<RenderDirectionalLight>(deltaDirectionalMirror));

        Entity deltaSpotMirror = Mirror(deltaRenderWorld, deltaSpotSource);
        Entity referenceSpotMirror = Mirror(referenceRenderWorld, referenceSpotSource);
        Assert.Equal(
            referenceRenderWorld.Read<RenderSpotLight>(referenceSpotMirror),
            deltaRenderWorld.Read<RenderSpotLight>(deltaSpotMirror));
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
        Entity directionalSource = mainWorld.CreateEntity();
        Entity pointSource = mainWorld.CreateEntity();
        Entity spotSource = mainWorld.CreateEntity();
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
            renderWorld.Read<RenderDirectionalLight>(directionalMirror));
        AssertLightCookie(
            directionalCookie,
            renderWorld.Read<RenderLightCookie>(directionalMirror));
        Assert.False(renderWorld.Has<RenderPointLight>(directionalMirror));
        Assert.False(renderWorld.Has<RenderSpotLight>(directionalMirror));
        AssertPointLight(point, renderWorld.Read<RenderPointLight>(pointMirror));
        Assert.False(renderWorld.Has<RenderLightCookie>(pointMirror));
        AssertSpotLight(spot, renderWorld.Read<RenderSpotLight>(spotMirror));
        AssertLightCookie(spotCookie, renderWorld.Read<RenderLightCookie>(spotMirror));

        DirectionalLight updatedDirectional = directional with { Intensity = 7, LayerMask = 0x10u };
        mainWorld.Replace(directionalSource, updatedDirectional);
        mainWorld.Remove<LightCookie>(directionalSource);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(directionalMirror, Mirror(renderWorld, directionalSource));
        AssertDirectionalLight(
            updatedDirectional,
            renderWorld.Read<RenderDirectionalLight>(directionalMirror));
        Assert.False(renderWorld.Has<RenderLightCookie>(directionalMirror));
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

        Assert.True(renderWorld.IsAlive(mirror));
        Assert.Equal(mirror, Mirror(renderWorld, source));
        AssertPointLight(point, renderWorld.Read<RenderPointLight>(mirror));
        Assert.False(renderWorld.Has<RenderTransform>(mirror));
        Assert.False(renderWorld.Has<RenderMesh>(mirror));
        Assert.False(renderWorld.HasBuffer<RenderMaterialBinding>(mirror));
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
        renderWorld.Add(mirror, new PipelinePreparedState(73));

        Move(mainWorld, source, new Vector3(9, 8, 7));
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.Equal(73, renderWorld.Read<PipelinePreparedState>(mirror).Value);
        Assert.Equal(new Vector3(9, 8, 7), renderWorld.Read<RenderTransform>(mirror).Position);
    }

    [Fact]
    public void Extract_PreservesRendererOnlyEntity()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity rendererOnly = renderWorld.CreateEntity();
        renderWorld.Add(rendererOnly, new PipelinePreparedState(91));
        AddMeshSource(
            mainWorld,
            MeshAsset(9, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);

        ExtractAndApply(extraction, mainWorld, renderWorld);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.True(renderWorld.IsAlive(rendererOnly));
        Assert.False(renderWorld.Has<RenderSource>(rendererOnly));
        Assert.Equal(91, renderWorld.Read<PipelinePreparedState>(rendererOnly).Value);
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
        renderWorld.Add(mirror, new PipelinePreparedState(12));

        mainWorld.DestroyEntity(source);
        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.False(mainWorld.IsAlive(source));
        Assert.False(renderWorld.IsAlive(mirror));
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(source).Length);
    }

    [Fact]
    public void Extract_TopologyFaultRollsBackCreatedAndDestroyedMirrorsThenCanRetry()
    {
        var injectedFault = new InvalidOperationException("intentional mirror topology fault");
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity removedSource = AddMeshSource(
            mainWorld,
            MeshAsset(73, 1),
            boundsExpansion: 1,
            position: Vector3.Zero);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Entity removedMirror = Mirror(renderWorld, removedSource);
        renderWorld.Add(removedMirror, new PipelinePreparedState(144));

        mainWorld.DestroyEntity(removedSource);
        Entity addedSource = AddMeshSource(
            mainWorld,
            MeshAsset(74, 1),
            boundsExpansion: 2,
            position: Vector3.One);
        int removeCount = 0;
        renderWorld.Hooks<RenderSource>().OnRemove(
            (DeferredWorld _, Entity _, in RenderSource _) =>
            {
                if (Interlocked.Increment(ref removeCount) == 1)
                {
                    throw injectedFault;
                }
            });

        InvalidOperationException fault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, mainWorld, renderWorld));
        Assert.Same(injectedFault, fault);

        Assert.True(renderWorld.IsAlive(removedMirror));
        Assert.Equal(removedMirror, Mirror(renderWorld, removedSource));
        Assert.Equal(144, renderWorld.Read<PipelinePreparedState>(removedMirror).Value);
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(addedSource).Length);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Assert.False(renderWorld.IsAlive(removedMirror));
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(removedSource).Length);
        Entity addedMirror = Mirror(renderWorld, addedSource);
        Assert.Same(MeshAsset(74, 1).CaptureData(), renderWorld.Read<RenderMesh>(addedMirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.Read<RenderTransform>(addedMirror).Position);
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
        Assert.True(mainWorld.HasBuffer<MeshMaterialBinding>(source));
        Assert.False(mainWorld.Has<RenderSource>(source));
        Assert.False(mainWorld.Has<RenderTransform>(source));
        Assert.False(mainWorld.Has<RenderMesh>(source));
        Assert.False(mainWorld.HasBuffer<RenderMaterialBinding>(source));
    }

    [Fact]
    public void Extract_FirstFailureDoesNotBindSourceWorld()
    {
        var injectedFault = new InvalidOperationException("intentional first extraction fault");
        World rejectedMainWorld = new();
        World acceptedMainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        Entity rejectedSource = AddMeshSource(
            rejectedMainWorld,
            MeshAsset(71, 1),
            boundsExpansion: 1,
            position: Vector3.Zero);
        acceptedMainWorld.CreateEntity();
        Entity acceptedSource = AddMeshSource(
            acceptedMainWorld,
            MeshAsset(72, 1),
            boundsExpansion: 2,
            position: Vector3.One);
        int addCount = 0;
        renderWorld.Hooks<RenderMesh>().OnAdd(
            (DeferredWorld _, Entity _, in RenderMesh _) =>
            {
                if (++addCount == 1)
                    throw injectedFault;
            });

        InvalidOperationException firstFault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, rejectedMainWorld, renderWorld));
        Assert.Same(injectedFault, firstFault);

        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(rejectedSource).Length);

        ExtractAndApply(extraction, acceptedMainWorld, renderWorld);

        Entity acceptedMirror = Mirror(renderWorld, acceptedSource);
        Assert.Same(MeshAsset(72, 1).CaptureData(), renderWorld.Read<RenderMesh>(acceptedMirror).Mesh);
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(rejectedSource).Length);
        InvalidOperationException bindingFault = Assert.Throws<InvalidOperationException>(
            () => ExtractAndApply(extraction, rejectedMainWorld, renderWorld));

        Assert.True(renderWorld.IsAlive(acceptedMirror));
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

        Assert.True(renderWorld.IsAlive(firstMirror));
        Assert.Same(MeshAsset(41, 1).CaptureData(), renderWorld.Read<RenderMesh>(firstMirror).Mesh);
    }

    [Fact]
    public void Extract_RejectsUsingRenderWorldAsItsOwnMainWorld()
    {
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ExtractAndApply(extraction, renderWorld, renderWorld));

        Assert.Equal("mainWorld", error.ParamName);

    }

    [Fact]
    public void ExtractionFeatureRegistersItsOwnReadsAndPublicationLogic()
    {
        World mainWorld = new();
        Entity source = mainWorld.CreateEntity();
        mainWorld.Add(source, new CustomExtractValue(37));
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());
        var custom = new CustomExtractionSystem();
        extraction.Add(custom);

        ExtractAndApply(extraction, mainWorld, renderWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.Equal(37, renderWorld.Read<CustomRenderValue>(mirror).Value);
        mainWorld.Replace(source, new CustomExtractValue(53));
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.Equal(53, renderWorld.Read<CustomRenderValue>(mirror).Value);
        ExtractAndApply(extraction, mainWorld, renderWorld);
        Assert.Equal(53, renderWorld.Read<CustomRenderValue>(mirror).Value);
        Assert.Throws<InvalidOperationException>(() =>
            extraction.Add(new CustomExtractionSystem()));
    }

    [Fact]
    public void Extract_RejectsAnotherRenderWorldAsTheMainWorld()
    {
        RenderWorld source = new();
        RenderWorld destination = new();
        using var extraction = new RenderExtractionSystems(destination, new MeshRenderExtractor(), new LightRenderExtractor());

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ExtractAndApply(extraction, source, destination));

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
        Entity entity = world.CreateEntity();
        world.Add(
            entity,
            new WorldTransform
            {
                Qvvs = new TransformQvvs(position, Quaternion.Identity, 1),
            });
        world.Add(entity, new MeshInstance { Mesh = mesh, BoundsExpansion = boundsExpansion });
        return entity;
    }

    private static QueryDefinition SourceExtractionDefinition() =>
        new QueryDefinitionBuilder()
            .Optional<WorldTransform>(QueryAccess.Read)
            .Optional<MeshInstance>(QueryAccess.Read)
            .Optional<DirectionalLight>(QueryAccess.Read)
            .Optional<PointLight>(QueryAccess.Read)
            .Optional<SpotLight>(QueryAccess.Read)
            .Optional<LightCookie>(QueryAccess.Read)
            .OptionalBuffer<MeshMaterialBinding>(QueryAccess.Read)
            .Build();

    private static void Move(World world, Entity entity, Vector3 position)
    {
        WorldTransform transform = world.Read<WorldTransform>(entity);
        transform.Qvvs = new TransformQvvs(position, Quaternion.Identity, 1);
        world.Replace(entity, transform);
    }

    private static Entity Mirror(RenderWorld renderWorld, Entity source)
    {
        ReadOnlySpan<Entity> matches = renderWorld.GetByIndex<RenderSource, Entity>(source);
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
        if (!world.HasBuffer<MeshMaterialBinding>(entity))
            world.AddBuffer<MeshMaterialBinding>(entity);

        MaterialBindingExpectation state = new(first, second, count);
        world.ExecuteBufferWrite<MeshMaterialBinding, MaterialBindingExpectation>(
            entity,
            ref state,
            static (DynamicBuffer<MeshMaterialBinding> buffer, ref MaterialBindingExpectation values) =>
            {
                buffer.Clear();
                if (values.Count > 0)
                    buffer.Add(new MeshMaterialBinding(values.First));
                if (values.Count > 1)
                    buffer.Add(new MeshMaterialBinding(values.Second));
            });
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
        MaterialBindingExpectation state = new(first, second, count);
        renderWorld.ExecuteBufferRead<RenderMaterialBinding, MaterialBindingExpectation>(
            entity,
            ref state,
            static (BufferView<RenderMaterialBinding> buffer, ref MaterialBindingExpectation expected) =>
            {
                ReadOnlySpan<RenderMaterialBinding> bindings = buffer.AsSpan();
                Assert.Equal(expected.Count, bindings.Length);
                if (expected.Count > 0)
                    Assert.Equal(expected.First!.MaterialId, bindings[0].MaterialId);
                if (expected.Count > 1)
                    Assert.Equal(expected.Second!.MaterialId, bindings[1].MaterialId);
            });
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
        SomeEngine.ECS.IComponent;

    private sealed class MeshReadObserver(ManualResetEventSlim entered, Action<MeshInstance> observe) : IRenderExtractionSystem
    {
        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<MeshInstance>();
            entered.Set();
        }

        public void Reset() { }
        public void Collect(QueryChunkView chunk)
        {
            if (chunk.TryRead<MeshInstance>(out var values))
                foreach (var value in values) observe(value);
        }
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
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

        public void Collect(QueryChunkView chunk)
        {
            if (!chunk.TryRead<CustomExtractValue>(out ReadOnlySpan<CustomExtractValue> values))
                return;
            ReadOnlySpan<Entity> entities = chunk.Entities;
            for (int row = 0; row < entities.Length; row++)
                _values.Add((entities[row], values[row].Value));
        }

        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
        {
            if (!chunk.Has<CustomExtractValue>() || !chunk.HasChangedSinceLastSystemVersion<CustomExtractValue>())
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
