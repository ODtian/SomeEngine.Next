using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
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

public sealed class RenderWorldExtractorTests
{
    [Fact]
    public void Extract_UsesInstalledSystemsAndUpdatesStableMirror()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(1, 1),
            boundsExpansion: 0.25f,
            position: Vector3.One);

        extraction.Extract(mainWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.IsAlive(mirror));

        Move(mainWorld, source, new Vector3(4, 5, 6));
        extraction.Extract(mainWorld);

        Assert.Equal(new Vector3(4, 5, 6), renderWorld.Read<RenderTransform>(mirror).Position);
    }

    [Fact]
    public async Task Extract_WaitsForActiveMainWorldMutationBeforeReadingSourceImage()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extractionSystems = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(1, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        using ManualResetEventSlim mutationEntered = new();
        using ManualResetEventSlim releaseMutation = new();
        mainWorld.Hooks<MeshInstance>().OnReplace(
            (DeferredWorld _, Entity _, in MeshInstance _) =>
            {
                mutationEntered.Set();
                Assert.True(releaseMutation.Wait(TimeSpan.FromSeconds(10)));
            });

        MeshInstance replacement = new()
        {
            Mesh = MeshAsset(2, 1),
            BoundsExpansion = 3,
        };
        Task mutation = Task.Run(() => mainWorld.Replace(source, replacement));
        Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(5)));

        Task extraction = Task.Run(() => extractionSystems.Extract(mainWorld));
        try
        {
            await Task.Delay(100);
            Assert.False(extraction.IsCompleted);
        }
        finally
        {
            releaseMutation.Set();
        }

        await Task.WhenAll(mutation, extraction);

        Entity mirror = Mirror(renderWorld, source);
        Assert.Equal(replacement.Mesh, renderWorld.Read<RenderMesh>(mirror).Mesh);
        Assert.Equal(replacement.BoundsExpansion, renderWorld.Read<RenderMesh>(mirror).BoundsExpansion);
    }

    [Fact]
    public void Extract_ApplyFaultKeepsPublishedRootUnchanged()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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
        extraction.Extract(mainWorld);

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
                    throw new InvalidOperationException("intentional extraction apply fault");
            });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => extraction.Extract(mainWorld));

        Assert.Contains("intentional extraction apply fault", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, replaceCount);
        Assert.Equal(firstOldMesh, renderWorld.Read<RenderMesh>(firstMirror).Mesh);
        Assert.Equal(secondOldMesh, renderWorld.Read<RenderMesh>(secondMirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.Read<RenderTransform>(firstMirror).Position);
        Assert.Equal(new Vector3(2), renderWorld.Read<RenderTransform>(secondMirror).Position);
        AssertRenderBindings(renderWorld, firstMirror, oldMaterial, default!, count: 1);
        Assert.Equal(47, renderWorld.Read<PipelinePreparedState>(firstMirror).Value);
    }

    [Fact]
    public void Extract_LightModuleFaultRollsBackEarlierMeshModuleChanges()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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
        extraction.Extract(mainWorld);

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
                throw new InvalidOperationException("intentional light extraction fault"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => extraction.Extract(mainWorld));

        Assert.Contains("intentional light extraction fault", error.Message, StringComparison.Ordinal);
        Assert.Equal(originalMesh, renderWorld.Read<RenderMesh>(mirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.Read<RenderTransform>(mirror).Position);
        AssertPointLight(originalLight, renderWorld.Read<RenderPointLight>(mirror));
    }

    [Fact]
    public void Extract_RejectsReentrantHookBeforeScratchCanBeRewritten()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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
        extraction.Extract(mainWorld);

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
                    extraction.Extract(mainWorld);
                }
                catch (InvalidOperationException exception)
                {
                    nestedFault = exception;
                }
            });

        extraction.Extract(mainWorld);

        Assert.NotNull(nestedFault);
        Assert.Contains("already active", nestedFault.Message, StringComparison.Ordinal);
        Assert.Equal(2, replaceCount);
        Assert.Equal(
            firstReplacement,
            renderWorld.Read<RenderMesh>(Mirror(renderWorld, firstSource)).Mesh);
        Assert.Equal(
            secondReplacement,
            renderWorld.Read<RenderMesh>(Mirror(renderWorld, secondSource)).Mesh);

        extraction.Extract(mainWorld);
        Assert.Equal(1, renderWorld.GetByIndex<RenderSource, Entity>(firstSource).Length);
        Assert.Equal(1, renderWorld.GetByIndex<RenderSource, Entity>(secondSource).Length);
    }

    [Fact]
    public void Extract_IndexesOneStableRenderSourceMirrorPerMainEntity()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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

        extraction.Extract(mainWorld);

        Entity firstMirror = Mirror(renderWorld, firstSource);
        Entity secondMirror = Mirror(renderWorld, secondSource);
        Assert.NotEqual(firstMirror, secondMirror);
        Assert.Equal(firstSource, renderWorld.Read<RenderSource>(firstMirror).Entity);
        Assert.Equal(secondSource, renderWorld.Read<RenderSource>(secondMirror).Entity);

        extraction.Extract(mainWorld);

        Assert.Equal(firstMirror, Mirror(renderWorld, firstSource));
        Assert.Equal(secondMirror, Mirror(renderWorld, secondSource));
    }

    [Fact]
    public void Extract_DoesNotInvalidateIndependentInternedQueryAcquisitions()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        QueryHandle renderSourceQuery = renderWorld.Query(
            new QueryDefinitionBuilder().Read<RenderSource>());
        QueryHandle sourceQuery = mainWorld.Query(SourceExtractionDefinition());
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(3, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);

        renderWorld.ReleaseQuery(renderSourceQuery);
        extraction.Extract(mainWorld);

        Assert.Equal(source, renderWorld.Read<RenderSource>(Mirror(renderWorld, source)).Entity);
        Assert.Equal(SourceExtractionDefinition().Key, mainWorld.GetQueryDefinition(sourceQuery).Key);
        mainWorld.ReleaseQuery(sourceQuery);
    }

    [Fact]
    public void Extract_StoresTransformAndMeshAsSeparateRenderComponents()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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

        extraction.Extract(mainWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.Has<RenderTransform>(mirror));
        Assert.True(renderWorld.Has<RenderMesh>(mirror));
        RenderTransform transform = renderWorld.Read<RenderTransform>(mirror);
        RenderMesh renderMesh = renderWorld.Read<RenderMesh>(mirror);
        Assert.Equal(sourceTransform.Position, transform.Position);
        Assert.Equal(sourceTransform.Rotation, transform.Rotation);
        Assert.Equal(sourceTransform.Scale, transform.Scale);
        Assert.Equal(sourceTransform.Stretch, transform.Stretch);
        Assert.Equal(mesh, renderMesh.Mesh);
        Assert.Equal(1.5f, renderMesh.BoundsExpansion);
    }

    [Fact]
    public void Extract_TranslatesMaterialBindingBuffersAndTracksReplacementAndRemoval()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(5, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        Material first = MaterialAsset(11, 1);
        Material second = MaterialAsset(12, 1);
        Material replacement = MaterialAsset(13, 2);
        SetSourceBindings(mainWorld, source, first, second, count: 2);

        extraction.Extract(mainWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.True(renderWorld.HasBuffer<RenderMaterialBinding>(mirror));
        Assert.False(renderWorld.HasBuffer<MeshMaterialBinding>(mirror));
        AssertRenderBindings(renderWorld, mirror, first, second, count: 2);

        SetSourceBindings(mainWorld, source, replacement, default!, count: 1);
        extraction.Extract(mainWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        AssertRenderBindings(renderWorld, mirror, replacement, default!, count: 1);

        mainWorld.RemoveBuffer<MeshMaterialBinding>(source);
        extraction.Extract(mainWorld);

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
        using var deltaExtraction = new RenderExtractionSystems(deltaRenderWorld);
        using var referenceExtraction = new RenderExtractionSystems(referenceRenderWorld);
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

        deltaExtraction.Extract(deltaMainWorld);
        referenceExtraction.Extract(referenceMainWorld);

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
        long deltaRenderTopology = deltaRenderWorld.PublishedTopologyRevision;
        long referenceRenderTopology = referenceRenderWorld.PublishedTopologyRevision;

        deltaExtraction.Extract(deltaMainWorld);
        referenceExtraction.Extract(referenceMainWorld);

        Assert.Equal(deltaRenderTopology, deltaRenderWorld.PublishedTopologyRevision);
        Assert.True(
            referenceRenderWorld.PublishedTopologyRevision > referenceRenderTopology);
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
        using var extraction = new RenderExtractionSystems(renderWorld);
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

        extraction.Extract(mainWorld);

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
        extraction.Extract(mainWorld);

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
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(7, 1),
            boundsExpansion: 0.5f,
            position: Vector3.One);
        PointLight point = new(Vector3.One, 10, Vector3.One, 2);
        mainWorld.Add(source, point);
        SetSourceBindings(mainWorld, source, MaterialAsset(21, 1), default!, count: 1);

        extraction.Extract(mainWorld);
        Entity mirror = Mirror(renderWorld, source);

        mainWorld.Remove<MeshInstance>(source);
        extraction.Extract(mainWorld);

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
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(8, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        extraction.Extract(mainWorld);
        Entity mirror = Mirror(renderWorld, source);
        renderWorld.Add(mirror, new PipelinePreparedState(73));

        Move(mainWorld, source, new Vector3(9, 8, 7));
        extraction.Extract(mainWorld);

        Assert.Equal(mirror, Mirror(renderWorld, source));
        Assert.Equal(73, renderWorld.Read<PipelinePreparedState>(mirror).Value);
        Assert.Equal(new Vector3(9, 8, 7), renderWorld.Read<RenderTransform>(mirror).Position);
    }

    [Fact]
    public void Extract_PreservesRendererOnlyEntity()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity rendererOnly = renderWorld.CreateEntity();
        renderWorld.Add(rendererOnly, new PipelinePreparedState(91));
        AddMeshSource(
            mainWorld,
            MeshAsset(9, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);

        extraction.Extract(mainWorld);
        extraction.Extract(mainWorld);

        Assert.True(renderWorld.IsAlive(rendererOnly));
        Assert.False(renderWorld.Has<RenderSource>(rendererOnly));
        Assert.Equal(91, renderWorld.Read<PipelinePreparedState>(rendererOnly).Value);
    }

    [Fact]
    public void Extract_DestroysMirrorWhenSourceIsDestroyed()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(10, 1),
            boundsExpansion: 0,
            position: Vector3.Zero);
        extraction.Extract(mainWorld);
        Entity mirror = Mirror(renderWorld, source);
        renderWorld.Add(mirror, new PipelinePreparedState(12));

        mainWorld.DestroyEntity(source);
        extraction.Extract(mainWorld);

        Assert.False(mainWorld.IsAlive(source));
        Assert.False(renderWorld.IsAlive(mirror));
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(source).Length);
    }

    [Fact]
    public void Extract_TopologyFaultRollsBackCreatedAndDestroyedMirrorsThenCanRetry()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity removedSource = AddMeshSource(
            mainWorld,
            MeshAsset(73, 1),
            boundsExpansion: 1,
            position: Vector3.Zero);
        extraction.Extract(mainWorld);
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
                    throw new InvalidOperationException(
                        "intentional mirror topology fault");
                }
            });

        InvalidOperationException fault = Assert.Throws<InvalidOperationException>(
            () => extraction.Extract(mainWorld));

        Assert.Contains("intentional mirror topology fault", fault.Message, StringComparison.Ordinal);
        Assert.True(renderWorld.IsAlive(removedMirror));
        Assert.Equal(removedMirror, Mirror(renderWorld, removedSource));
        Assert.Equal(144, renderWorld.Read<PipelinePreparedState>(removedMirror).Value);
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(addedSource).Length);

        extraction.Extract(mainWorld);

        Assert.False(renderWorld.IsAlive(removedMirror));
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(removedSource).Length);
        Entity addedMirror = Mirror(renderWorld, addedSource);
        Assert.Same(MeshAsset(74, 1), renderWorld.Read<RenderMesh>(addedMirror).Mesh);
        Assert.Equal(Vector3.One, renderWorld.Read<RenderTransform>(addedMirror).Position);
    }

    [Fact]
    public void Extract_DoesNotWriteRenderLinksOrSnapshotsIntoMainWorld()
    {
        World mainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = AddMeshSource(
            mainWorld,
            MeshAsset(11, 1),
            boundsExpansion: 0.75f,
            position: Vector3.One);
        SetSourceBindings(mainWorld, source, MaterialAsset(31, 1), default!, count: 1);

        extraction.Extract(mainWorld);

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
        World rejectedMainWorld = new();
        World acceptedMainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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
                    throw new InvalidOperationException("intentional first extraction fault");
            });

        InvalidOperationException firstFault = Assert.Throws<InvalidOperationException>(
            () => extraction.Extract(rejectedMainWorld));

        Assert.Contains("intentional first extraction fault", firstFault.Message, StringComparison.Ordinal);
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(rejectedSource).Length);

        extraction.Extract(acceptedMainWorld);

        Entity acceptedMirror = Mirror(renderWorld, acceptedSource);
        Assert.Same(MeshAsset(72, 1), renderWorld.Read<RenderMesh>(acceptedMirror).Mesh);
        Assert.Equal(0, renderWorld.GetByIndex<RenderSource, Entity>(rejectedSource).Length);
        InvalidOperationException bindingFault = Assert.Throws<InvalidOperationException>(
            () => extraction.Extract(rejectedMainWorld));
        Assert.Contains("one authoritative main World", bindingFault.Message, StringComparison.Ordinal);
        Assert.True(renderWorld.IsAlive(acceptedMirror));
    }

    [Fact]
    public void Extract_BindsRenderWorldToOneMainWorld()
    {
        World firstMainWorld = new();
        World secondMainWorld = new();
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
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

        extraction.Extract(firstMainWorld);
        Entity firstMirror = Mirror(renderWorld, firstSource);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => extraction.Extract(secondMainWorld));

        Assert.Contains("one authoritative main World", error.Message, StringComparison.Ordinal);
        Assert.True(renderWorld.IsAlive(firstMirror));
        Assert.Same(MeshAsset(41, 1), renderWorld.Read<RenderMesh>(firstMirror).Mesh);
    }

    [Fact]
    public void Extract_RejectsUsingRenderWorldAsItsOwnMainWorld()
    {
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => extraction.Extract(renderWorld));

        Assert.Equal("mainWorld", error.ParamName);
        Assert.Contains("cannot be an authoritative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractionFeatureRegistersItsOwnReadsAndPublicationLogic()
    {
        World mainWorld = new();
        Entity source = mainWorld.CreateEntity();
        mainWorld.Add(source, new CustomExtractValue(37));
        RenderWorld renderWorld = new();
        using var extraction = new RenderExtractionSystems(renderWorld);
        var custom = new CustomExtractionSystem();
        extraction.Add(custom);

        extraction.Extract(mainWorld);

        Entity mirror = Mirror(renderWorld, source);
        Assert.Equal(37, renderWorld.Read<CustomRenderValue>(mirror).Value);
        Assert.Throws<InvalidOperationException>(() =>
            extraction.Add(new CustomExtractionSystem()));
        Assert.Null(typeof(RenderWorld).GetMethod(
            "Extract",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
    }

    [Fact]
    public void Extract_RejectsAnotherRenderWorldAsTheMainWorld()
    {
        RenderWorld source = new();
        RenderWorld destination = new();
        using var extraction = new RenderExtractionSystems(destination);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => extraction.Extract(source));

        Assert.Equal("mainWorld", error.ParamName);
        Assert.Contains("requires a main World", error.Message, StringComparison.Ordinal);
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
        MaterialBindingExpectation state = new(first, second, count);
        renderWorld.ExecuteBufferRead<RenderMaterialBinding, MaterialBindingExpectation>(
            entity,
            ref state,
            static (BufferView<RenderMaterialBinding> buffer, ref MaterialBindingExpectation expected) =>
            {
                ReadOnlySpan<RenderMaterialBinding> bindings = buffer.AsSpan();
                Assert.Equal(expected.Count, bindings.Length);
                if (expected.Count > 0)
                    Assert.Equal(expected.First, bindings[0].Material);
                if (expected.Count > 1)
                    Assert.Equal(expected.Second, bindings[1].Material);
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

    private static Mesh MeshAsset(int id, int revision)
        => TestAssets.Mesh(id, revision);

    private static Material MaterialAsset(int id, int revision)
        => TestAssets.Material(id, revision);

    private static Texture TextureAsset(int id, int revision)
        => TestAssets.Texture(id, revision);
}
