using SomeEngine.Assets;
using System.Buffers;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Tests;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterShaderDataTests
{
    [Fact]
    public async Task SavedStandardCodeAndReflectionMatchDirectSlang()
    {
        string path = Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "templates",
            "DefaultProject",
            "assets",
            "Shaders",
            "standard.shader.asset");
        await using ShaderAsset file = await ShaderAsset.OpenAsync(path);
        foreach (TargetDesc targetDescription in SlangShaderImporter.CreateDefaultTargets(
                     ShaderTestCompilation.Slang))
        {
            SessionDesc description = SlangShaderImporter.CreateSessionDescription(
                path,
                [targetDescription]);
            Shader saved = await file.GetBlock<ShaderBlock>().CreateAsync(
                file.GetBlock<ShaderSourceBlock>(),
                ShaderTestCompilation.Slang,
                description,
                file.Name);
            Shader direct = await file.GetBlock<ShaderSourceBlock>().CreateAsync(
                ShaderTestCompilation.Slang,
                description,
                file.Name);
            Assert.Contains(
                Enumerable.Range(0, saved.SlangSourceUnitCount),
                unit => ContainsMaterialScalars(
                    saved.GetSlangSourceUnit(unit).GetModuleReflection()));
            Assert.Equal(direct.SlangEntryCount, saved.SlangEntryCount);
            for (int entry = 0; entry < saved.SlangEntryCount; entry++)
            {
                string componentName = saved.GetSlangEntryPoint(entry).GetFunctionReflection().Name;
                string reflectedName = saved.GetSlangReflection(0)
                    .GetEntryPointByIndex(checked((nuint)entry)).Name;
                Assert.Equal(componentName, reflectedName);
                Assert.True(
                    GetCode(saved, 0, entry).AsSpan().SequenceEqual(
                        GetCode(direct, 0, entry)),
                    $"Target {targetDescription.Format}, entry {entry} differs.");
            }
        }

        static bool ContainsMaterialScalars(DeclReflection declaration)
        {
            if (declaration == DeclReflection.Null)
                return false;
            if (declaration.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection type = declaration.Type;
                for (uint index = 0; index < type.AttributeCount; index++)
                {
                    if (type.GetAttribute(index).Name == "MaterialScalars")
                        return true;
                }
            }
            for (int index = 0; index < declaration.ChildrenCount; index++)
                if (ContainsMaterialScalars(declaration.GetChild(checked((uint)index))))
                    return true;
            return false;
        }
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("split")]
    public async Task TessellationSurfaceEntriesFuseVertexEvaluationWithoutGlobalCaches(
        string stage)
    {
        Shader shader = await ReadCookedShader("cluster_tessellation_phong.shader.asset");
        string entryPoint = stage switch
        {
            "seed" => "SeedPhongTessellation",
            "split" => "SplitPhongTessellation",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

        string[] resources = ResourceNames(shader, entryPoint);
        Assert.Contains("PageHeap", resources);
        Assert.Contains("TessellationCounters", resources);
        Assert.Contains("TessellationUniformData", resources);
        Assert.Contains("TessellationVisibleClusters", resources);
        Assert.DoesNotContain("TessellationDeformCache", resources);
        Assert.DoesNotContain("TessellationCacheOffsets", resources);
        Assert.DoesNotContain("TessellationQueueStates", resources);
        Assert.DoesNotContain("TessellationWorkNext", resources);
        if (stage == "split")
        {
            Assert.Contains("InstanceData", resources);
            Assert.Contains("InstanceProperties", resources);
            Assert.Contains("TessellationFinalPatches", resources);
            Assert.Contains("TessellationRasterSlots", resources);
            Assert.Contains("TessellationHiZTexture", resources);
            Assert.Contains("TessellationLevelCounts", resources);
            Assert.Contains("TessellationNextLevelArgs", resources);
            Assert.Contains("TessellationPolicyViews", resources);
            Assert.Contains("TessellationViews", resources);
            Assert.Contains("TessellationWorkPatches0", resources);
            Assert.Contains("TessellationWorkPatches1", resources);
        }
    }

    [Fact]
    public async Task TessellationTemplateBuildsAllClusterEntriesFromOneSurfaceType()
    {
        Shader shader = await ReadCookedShader("cluster_tessellation_phong.shader.asset");
        (string Entry, SlangStage Stage)[] entries =
        [
            ("ClusterTessellationSeed", SlangStage.Compute),
            ("ClusterTessellationSplit", SlangStage.Compute),
            ("ClusterTessellationDiceImageAtomic64", SlangStage.Compute),
            ("ClusterTessellationDiceBufferAtomic64", SlangStage.Compute),
            ("ClusterTessellationHardwareVertex", SlangStage.Vertex),
            ("ClusterTessellationHardwarePixel", SlangStage.Fragment),
        ];

        foreach ((string entry, SlangStage stage) in entries)
        {
            _ = RequireEntry(
                shader,
                SlangCompileTarget.Spirv,
                entry,
                stage);
        }
    }

    [Fact]
    public async Task TessellationRasterEntriesConsumeGroupedPatchesWithoutIntermediateGeometry()
    {
        Shader shader = await ReadCookedShader("cluster_tessellation_phong.shader.asset");
        (string Entry, SlangStage Stage)[] entries =
        [
            ("ClusterTessellationDiceImageAtomic64", SlangStage.Compute),
            ("ClusterTessellationDiceBufferAtomic64", SlangStage.Compute),
            ("ClusterTessellationHardwareVertex", SlangStage.Vertex),
        ];

        foreach ((string entry, SlangStage stage) in entries)
        {
            _ = RequireEntry(shader, SlangCompileTarget.Spirv, entry, stage);
            string[] resources = ResourceNames(shader, entry);
            Assert.Contains("TessellationRasterPatches", resources);
            Assert.DoesNotContain("GroupedPatches", resources);
            if (stage == SlangStage.Compute)
            {
                Assert.Contains("GroupedPatchIndices", resources);
                Assert.Contains("TessellationBinOffsets", resources);
            }
            Assert.Contains("InstanceData", resources);
            Assert.Contains("InstanceProperties", resources);
            Assert.DoesNotContain("DicedTriangles", resources);
            Assert.DoesNotContain("TessellationDeformCache", resources);
            Assert.DoesNotContain("TessellationCacheOffsets", resources);
        }
    }

    [Fact]
    public async Task TessellationBinningEntriesContainNoDicedTrianglePayload()
    {
        ClusterShaderOperationRole[] roles =
        [
            ClusterShaderOperationRole.TessellationBinningReset,
            ClusterShaderOperationRole.TessellationBinningCount,
            ClusterShaderOperationRole.TessellationBinningReserve,
            ClusterShaderOperationRole.TessellationBinningScatter,
        ];
        foreach (ClusterShaderOperationRole role in roles)
        {
            (Shader loadedShader, string entryPoint) = await ConfiguredComputeOperation(role);
            Shader shader = loadedShader;
            string[] resources = ResourceNames(shader, entryPoint);
            Assert.DoesNotContain("DicedTriangles", resources);
            Assert.DoesNotContain("VisibilityDicedTriangles", resources);
        }
    }

    [Fact]
    public async Task StandardShadeEntryConsumesMaterialLightingAndVirtualShadowResources()
    {
        Shader shader = await ReadTemplateShader("standard.shader.asset");
        string[] resources = ResourceNames(shader, "ClusterShade");

        Assert.Contains("AlbedoMap", resources);
        Assert.Contains("NormalMap", resources);
        Assert.Contains("ARMMap", resources);
        Assert.Contains("MaterialSampler", resources);
        Assert.Contains("LightingViews", resources);
        Assert.Contains("LightBuffer", resources);
        Assert.Contains("ClusteredLightGrid", resources);
        Assert.Contains("LightIndexList", resources);
        Assert.Contains("LightingCookieAtlas", resources);
        Assert.Contains("VirtualShadowViews", resources);
        Assert.Contains("VirtualShadowPageTable", resources);
        Assert.Contains("VirtualShadowAtlas", resources);
        Assert.Contains("VirtualShadowSampling", resources);
    }

    [Fact]
    public async Task ProjectMaterialShaderExposesItsSupportedClusterEntries()
    {
        Shader shader = await ReadTemplateShader("standard.shader.asset");
        (string Entry, SlangStage Stage)[] entries =
        [
            ("ClusterRasterSoftwareImageAtomic64", SlangStage.Compute),
            ("ClusterRasterSoftwareBufferAtomic64", SlangStage.Compute),
            ("ClusterRasterVertex", SlangStage.Vertex),
            ("ClusterRasterPixelImageAtomic64", SlangStage.Fragment),
            ("ClusterRasterPixelBufferAtomic64", SlangStage.Fragment),
            ("ClusterRasterPixelHardwareOnly", SlangStage.Fragment),
            ("ClusterShade", SlangStage.Compute),
        ];

        foreach ((string entry, SlangStage stage) in entries)
        {
            _ = RequireEntry(
                shader,
                SlangCompileTarget.Spirv,
                entry,
                stage);
        }
    }

    [Fact]
    public void GlobalDeformationAndMotionPrepassAssetsAreNotCooked()
    {
        string shaderDirectory = TestProjectPaths.ShaderDirectory();
        Assert.False(File.Exists(Path.Combine(shaderDirectory, "cluster_deform.shader.asset")));
        Assert.False(File.Exists(Path.Combine(shaderDirectory, "cluster_deform_prefix.shader.asset")));
        Assert.False(File.Exists(Path.Combine(shaderDirectory, "render_motion.shader.asset")));
    }

    [Theory]
    [InlineData("ClusterRasterSoftwareTarget", SlangStage.Compute)]
    [InlineData("ClusterRasterSoftwareMeshletTarget", SlangStage.Compute)]
    [InlineData("ClusterRasterPixelTarget", SlangStage.Fragment)]
    [InlineData("ClusterRasterMeshPixelTarget", SlangStage.Fragment)]
    public async Task DepthTargetEntriesDoNotRequirePrimaryVisibilityOutputs(
        string entryPoint,
        SlangStage stage)
    {
        Shader shader = await ReadTemplateShader("standard.shader.asset");
        foreach (SlangCompileTarget target in new[]
        {
            SlangCompileTarget.Dxil,
            SlangCompileTarget.Spirv,
        })
        {
            _ = RequireEntry(shader, target, entryPoint, stage);
            string[] resources = ResourceNames(shader, entryPoint, target);
            Assert.Contains("RasterTargetOutput", resources);
            Assert.DoesNotContain("VisibilityWinnerImage", resources);
            Assert.DoesNotContain("VisibilityWinnerBuffer", resources);
        }
    }

    [Theory]
    [InlineData(
        "clustered_light_grid.shader.asset",
        "BuildClusteredLightGrid",
        "ClusteredLightGrid|LightBuffer|LightIndexList|LightingViews")]
    [InlineData(
        "temporal_resolve.shader.asset",
        "PSMain",
        "MotionVectors|SceneColor|SceneDepth|TemporalHistory|TemporalResolveUniforms")]
    [InlineData(
        "render_debug_overlay.shader.asset",
        "PSMain",
        "DebugUniforms|InputColor|InputDepth")]
    [InlineData(
        "translucency_composite.shader.asset",
        "PSMain",
        "CompositionUniforms|OpaqueColor|TranslucencyAccumulation|TranslucencyRevealage")]
    [InlineData(
        "stream_output_geometry.shader.asset",
        "BuildStreamOutput",
        "ExportUniforms|PhaseOneCounts|PhaseTwoCounts|StreamOutputClusters|StreamOutputCount|VisibleClusters")]
    [InlineData(
        "ray_tracing_geometry.shader.asset",
        "BuildRayTracingGeometry",
        "ExportUniforms|PageHeap|PhaseOneCounts|PhaseTwoCounts|RayTracingClusterCount|RayTracingClusters|VisibleClusters")]
    public async Task IndependentFeatureEntriesExposeExactOwnedResourceContracts(
        string assetName,
        string entryPoint,
        string expectedResources)
    {
        Shader shader = await ReadCookedShader(assetName);

        Assert.Equal(
            Sorted(expectedResources.Split('|', StringSplitOptions.RemoveEmptyEntries)),
            ResourceNames(shader, entryPoint));
    }

    private static async Task<(Shader Shader, string EntryPoint)> ConfiguredComputeOperation(
        ClusterShaderOperationRole role)
    {
        string root = TestProjectPaths.DefaultProjectRoot();
        string manifestDirectory = Path.Combine(root, "Library", "AssetManifest");
        AssetManifest manifest = AssetManifest.Load(manifestDirectory);
        AssetEntry configurationRecord = Assert.Single(
            manifest.List(ClusterShadersAsset.FileType));
        using ClusterShadersAsset configuration = await ClusterShadersAsset.OpenAsync(
            Path.Combine(root, configurationRecord.Path));
        ClusterShaderOperation operation = Assert.Single(
            configuration.GetBlock<ClusterShadersBlock>().Operations,
            candidate => candidate.Role == role);
        ShaderRef shaderRef = Assert.Single(operation.Shaders!);
        Assert.False(string.IsNullOrWhiteSpace(shaderRef.EntryPoint));
        Guid shaderGuid = shaderRef.Asset.Value;
        Assert.True(manifest.TryGetAsset(shaderGuid, out AssetEntry shaderRecord));
        using ShaderAsset file = await ShaderAsset.OpenAsync(Path.Combine(root, shaderRecord.Path));
        Shader shader = await ShaderTestCompilation.CreateAsync(file);
        _ = RequireEntry(
            shader,
            SlangCompileTarget.Dxil,
            shaderRef.EntryPoint!,
            SlangStage.Compute);
        return (shader, shaderRef.EntryPoint!);
    }

    private static string[] ResourceNames(
        Shader shader,
        string entryPoint,
        SlangCompileTarget target = SlangCompileTarget.Dxil)
    {
        int targetIndex = shader.RequireSlangTarget(target);
        ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
        int entryIndex = -1;
        EntryPointReflection selectedEntry = EntryPointReflection.Null;
        for (int index = 0; index < shader.SlangEntryCount; index++)
        {
            EntryPointReflection candidate = reflection.GetEntryPointByIndex(checked((uint)index));
            if (string.Equals(candidate.Name, entryPoint, StringComparison.Ordinal) ||
                string.Equals(candidate.NameOverride, entryPoint, StringComparison.Ordinal))
            {
                entryIndex = index;
                selectedEntry = candidate;
                break;
            }
        }
        Assert.True(entryIndex >= 0, $"Entry '{entryPoint}' was not found.");

        SlangResult metadataResult = shader.SlangCode.GetEntryPointMetadata(
            entryIndex,
            targetIndex,
            out IMetadata metadata,
            out ISlangBlob? diagnostics);
        Assert.True(metadataResult.Succeeded, diagnostics?.AsString ?? metadataResult.ToString());
        VariableLayoutReflection globals = reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, globals);
        TypeLayoutReflection contents = globals.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (nint range = 0; range < contents.BindingRangeCount; range++)
        {
            nint descriptorCount = contents.GetBindingRangeDescriptorRangeCount(range);
            if (descriptorCount <= 0)
                continue;
            nint set = contents.GetBindingRangeDescriptorSetIndex(range);
            nint first = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
            string name = contents.GetBindingRangeLeafVariable(range).Name;
            for (nint relative = 0; relative < descriptorCount; relative++)
            {
                nint descriptor = first + relative;
                SlangParameterCategory category =
                    contents.GetDescriptorSetDescriptorRangeCategory(set, descriptor);
                nuint space = checked(
                    (nuint)contents.GetDescriptorSetSpaceOffset(set) +
                    globals.GetBindingSpace(category) +
                    SubObjectSpace(contents, range, category));
                nuint register = checked(
                    (nuint)contents.GetDescriptorSetDescriptorRangeIndexOffset(set, descriptor) +
                    globals.GetOffset(category));
                nint count = contents.GetDescriptorSetDescriptorRangeDescriptorCount(set, descriptor);
                int occurrences = unchecked((nuint)count) == Slang.UnboundedSize
                    ? 1
                    : checked((int)count);
                for (int element = 0; element < occurrences; element++)
                {
                    SlangResult usedResult = metadata.IsParameterLocationUsed(
                        category,
                        space,
                        checked(register + (nuint)element),
                        out bool used);
                    Assert.True(usedResult.Succeeded, usedResult.ToString());
                    if (used)
                        names.Add(name);
                }
            }
        }
        TypeLayoutReflection entryContents = selectedEntry.VarLayout.TypeLayout.UnwrapArray();
        if (entryContents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            entryContents = entryContents.ElementTypeLayout.UnwrapArray();
        for (nint range = 0; range < entryContents.BindingRangeCount; range++)
        {
            if (entryContents.GetBindingRangeDescriptorRangeCount(range) > 0)
                names.Add(entryContents.GetBindingRangeLeafVariable(range).Name);
        }
        return [.. names];

        static nuint SubObjectSpace(
            TypeLayoutReflection layout,
            nint bindingRange,
            SlangParameterCategory category)
        {
            for (nint index = 0; index < layout.SubObjectRangeCount; index++)
            {
                if (layout.GetSubObjectRangeBindingRangeIndex(index) != bindingRange)
                    continue;
                VariableLayoutReflection variable = layout.GetSubObjectRangeOffset(index);
                return variable == VariableLayoutReflection.Null
                    ? 0
                    : variable.GetBindingSpace(category);
            }
            return 0;
        }
    }

    [Theory]
    [InlineData("standard.shader.asset", "VirtualShadowViews")]
    [InlineData("vsm_view_compact.shader.asset", "ShadowViews")]
    public async Task ShadowViewBuffersAcceptTheSameCpuDataOnBothTargets(string asset, string resource)
    {
        Shader shader = await ReadTemplateShader(asset);
        foreach (SlangCompileTarget target in new[]
                 {
                     SlangCompileTarget.Dxil,
                     SlangCompileTarget.Spirv,
                 })
        {
            TypeLayoutReflection element = StructuredElement(shader, target, resource);
            Assert.Equal((nuint)Marshal.SizeOf<VirtualShadowView>(), element.GetStride(SlangParameterCategory.Uniform));
            for (uint index = 0; index < element.FieldCount; index++)
            {
                VariableLayoutReflection field = element.GetFieldByIndex(index);
                Assert.Equal((nuint)Marshal.OffsetOf<VirtualShadowView>(field.Name), field.GetOffset());
            }
        }
    }

    [Theory]
    [InlineData("standard.shader.asset")]
    [InlineData("vsm_view_compact.shader.asset")]
    public async Task RasterTargetViewsUseTheSameFormatOnBothTargets(string asset)
    {
        Shader shader = await ReadTemplateShader(asset);
        TypeLayoutReflection first = StructuredElement(shader, SlangCompileTarget.Dxil, "RasterTargetViews");
        TypeLayoutReflection second = StructuredElement(shader, SlangCompileTarget.Spirv, "RasterTargetViews");
        Assert.Equal(first.GetStride(SlangParameterCategory.Uniform), second.GetStride(SlangParameterCategory.Uniform));
        Assert.Equal(first.FieldCount, second.FieldCount);
        for (uint index = 0; index < first.FieldCount; index++)
        {
            Assert.Equal(first.GetFieldByIndex(index).Name, second.GetFieldByIndex(index).Name);
            Assert.Equal(first.GetFieldByIndex(index).GetOffset(), second.GetFieldByIndex(index).GetOffset());
        }
    }

    private static TypeLayoutReflection StructuredElement(
        Shader shader,
        SlangCompileTarget target,
        string resource)
    {
        int targetIndex = shader.RequireSlangTarget(target);
        VariableLayoutReflection globals = shader.GetSlangReflection(targetIndex).GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, globals);
        TypeLayoutReflection data = globals.TypeLayout;
        if (data.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            data = data.ElementTypeLayout;
        for (uint index = 0; index < data.FieldCount; index++)
        {
            VariableLayoutReflection field = data.GetFieldByIndex(index);
            if (field.Name == resource) return field.TypeLayout.ElementTypeLayout;
        }
        throw new InvalidDataException($"Shader {shader.Name} has no {resource} buffer for {target}.");
    }

    private static async ValueTask<Shader> ReadCookedShader(string assetName)
    {
        await using ShaderAsset file = await ShaderAsset.OpenAsync(TestProjectPaths.BuiltShaderPath(assetName));
        return await ShaderTestCompilation.CreateAsync(file);
    }

    private static async ValueTask<Shader> ReadTemplateShader(string assetName)
    {
        await using ShaderAsset file = await ShaderAsset.OpenAsync(Path.Combine(
            TestProjectPaths.ProjectRoot(), "templates", "DefaultProject", "assets", "Shaders", assetName));
        return await ShaderTestCompilation.CreateAsync(file);
    }

    private static EntryPointReflection RequireEntry(
        Shader shader,
        SlangCompileTarget target,
        string name,
        SlangStage stage)
    {
        int targetIndex = shader.RequireSlangTarget(target);
        return shader.RequireSlangEntry(targetIndex, name, stage);
    }

    private static byte[] GetCode(Shader shader, int targetIndex, int entryIndex)
    {
        ISlangBlob? blob = shader.GetSlangEntryBlob(targetIndex, entryIndex);
        return blob is null
            ? shader.GetSlangEntryBytes(targetIndex, entryIndex).ToArray()
            : blob.Buffer.ToArray();
    }

    private static string[] Sorted(params string[] names)
    {
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }
}
