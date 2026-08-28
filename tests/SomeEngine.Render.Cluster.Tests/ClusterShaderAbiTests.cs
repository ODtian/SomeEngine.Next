using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Instances;
using SlangShaderReflection = SlangShaderSharp.ShaderReflection;

namespace SomeEngine.Render.Cluster.Tests;

[Collection(SlangReflectionCollection.Name)]
public sealed class ClusterShaderAbiTests
{
    // Sorts before SomeEngine ids so the exact-layout probe exercises shifted Cluster ordinals.
    private const string TestTintCanonicalId = "material.test.tint";

    [Fact]
    public async Task SharedInstanceLayoutsMatchGeneratedContractAndConsumersCompile()
    {
        string shaderDirectory = Path.Combine(FindRepositoryRoot(), "assets", "Shaders");
        AssertPipelineNeutralInterfaceSources(shaderDirectory);
        RenderInstancePropertyLayout instanceLayout = ClusterRenderFeature.InstanceLayout;

        Assert.Equal(5, instanceLayout.Properties.Count);
        Assert.Collection(
            instanceLayout.Properties,
            property => AssertProperty(
                property,
                "someengine.cluster.bounds_expansion",
                "someengine.render.linear.float32.v1",
                4,
                4),
            property => AssertProperty(
                property,
                "someengine.cluster.bvh_root",
                "someengine.render.linear.uint32.v1",
                4,
                4),
            property => AssertProperty(
                property,
                "someengine.cluster.material_slot_offset",
                "someengine.render.linear.uint32.v1",
                4,
                4),
            property => AssertProperty(
                property,
                RenderInstanceTransformProperties.CurrentTransformKey.Value,
                "someengine.render.transform_qvvs48.v1",
                48,
                16),
            property => AssertProperty(
                property,
                RenderInstanceTransformProperties.PreviousTransformKey.Value,
                "someengine.render.transform_qvvs48.v1",
                48,
                16));
        Assert.DoesNotContain(
            instanceLayout.Properties,
            property => property.Key.Value.Contains("tint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            instanceLayout.Properties,
            property => property.Key.Value.Contains("data_offset", StringComparison.Ordinal));
        Assert.DoesNotContain(
            instanceLayout.Properties,
            property => property.Key.Value.Contains("data_flags", StringComparison.Ordinal));

        RenderInstancePropertyLayout materialLayout = CreateTestTintMaterialLayout();
        RenderInstancePropertyLayout composedLayout = RenderInstancePropertyLayout.Compose(
            instanceLayout,
            materialLayout);
        Assert.Equal(6, composedLayout.Properties.Count);
        RenderInstancePropertyKey tintKey = new(TestTintCanonicalId);
        Assert.True(materialLayout.Contains(tintKey));
        Assert.True(composedLayout.Contains(tintKey));
        Assert.False(instanceLayout.Contains(tintKey));

        string genericProperties = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "render_instance_properties.slang"));
        Assert.Contains(
            "T RenderInstanceLoad<T>(",
            genericProperties,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RenderInstanceLoadFloat4", genericProperties, StringComparison.Ordinal);
        Assert.DoesNotContain("Cluster", genericProperties, StringComparison.Ordinal);
        Assert.DoesNotContain("Tint", genericProperties, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transform", genericProperties, StringComparison.Ordinal);

        string clusterProperties = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "cluster_instance_properties.slang"));
        Assert.Contains(
            "float LoadClusterBoundsExpansion(ByteAddressBuffer data, uint metadata, uint instanceSlot)",
            clusterProperties,
            StringComparison.Ordinal);
        Assert.Contains(
            "uint LoadClusterBvhRoot(ByteAddressBuffer data, uint metadata, uint instanceSlot)",
            clusterProperties,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RENDER_INSTANCE_TRANSFORM_STRIDE", clusterProperties, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderInstanceTransform LoadRenderTransform", clusterProperties, StringComparison.Ordinal);

        string transformProperties = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "render_instance_transform.slang"));
        Assert.Contains("RENDER_INSTANCE_TRANSFORM_STRIDE = 48u", transformProperties, StringComparison.Ordinal);
        Assert.Contains(
            "RenderInstanceTransform LoadRenderTransform(",
            transformProperties,
            StringComparison.Ordinal);

        string generatedLayout = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "generated",
            "render_instance_layout.slang"));
        Assert.Contains(
            "uint4 PackedWords[RENDER_INSTANCE_METADATA_VECTOR_COUNT]",
            generatedLayout,
            StringComparison.Ordinal);
        Assert.Contains("uint LoadRenderInstanceMetadata(", generatedLayout, StringComparison.Ordinal);
        Assert.Contains("RENDER_INSTANCE_METADATA_WORD_COUNT = 5u", generatedLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTRACT_ID", generatedLayout, StringComparison.Ordinal);

        SlangResult createResult = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession globalSession);
        Assert.True(createResult.Succeeded, $"Failed to create the Slang global session: {createResult}.");

        SlangProfileID spirvProfile = globalSession.FindProfile("glsl_460");
        SlangProfileID dxilProfile = globalSession.FindProfile("sm_6_5");
        Assert.NotEqual(SlangProfileID.Unknown, spirvProfile);
        Assert.NotEqual(SlangProfileID.Unknown, dxilProfile);
        TargetDesc[] targets = OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64
            ?
            [
                new TargetDesc
                    {
                        Format = SlangCompileTarget.Spirv,
                        Profile = spirvProfile,
                    },
                    new TargetDesc
                    {
                        Format = SlangCompileTarget.Dxil,
                        Profile = dxilProfile,
                    },
            ]
            :
            [
                new TargetDesc
                    {
                        Format = SlangCompileTarget.Spirv,
                        Profile = spirvProfile,
                    },
            ];
        var sessionDesc = new SessionDesc
        {
            Targets = targets,
            SearchPaths = [shaderDirectory],
        };
        SlangResult sessionResult = globalSession.CreateSession(sessionDesc, out ISession session);
        Assert.True(sessionResult.Succeeded, $"Failed to create the Slang reflection session: {sessionResult}.");

        const string transformReflectionSource = """
                #include "render_instance_transform.slang"

                StructuredBuffer<RenderInstanceTransform> ReflectedTransforms;

                [shader("compute")]
                [numthreads(1, 1, 1)]
                void ReflectRenderInstanceTransform(uint3 dispatchThreadId : SV_DispatchThreadID)
                {
                }
                """;
        string sourcePath = Path.Combine(shaderDirectory, "render_instance_transform_reflection_test.slang");
        IModule? module = session.LoadModuleFromSource(
            "render_instance_transform_reflection_test",
            sourcePath,
            Slang.CreateBlob(transformReflectionSource),
            out ISlangBlob? moduleDiagnostics);
        Assert.True(module is not null, moduleDiagnostics?.AsString ?? "Slang did not return module diagnostics.");

        Type renderInstanceTransform = GetRenderInstanceTransformType();
        for (int targetIndex = 0; targetIndex < sessionDesc.Targets.Length; targetIndex++)
        {
            SlangShaderReflection reflection = module.GetLayout(
                targetIndex,
                out ISlangBlob? layoutDiagnostics);
            Assert.NotEqual(SlangShaderReflection.Null, reflection);
            Assert.True(
                layoutDiagnostics is null || string.IsNullOrWhiteSpace(layoutDiagnostics.AsString),
                layoutDiagnostics?.AsString);

            AssertStructuredBufferLayout(
                reflection,
                renderInstanceTransform,
                "RenderInstanceTransform",
                48);
        }

        AssertDirectPropertyAccessCompiles(
            session,
            shaderDirectory,
            sessionDesc.Targets.Length);
        AssertExactPipelineMaterialLayoutCompiles(
            session,
            shaderDirectory,
            sessionDesc.Targets.Length,
            composedLayout,
            composedLayout.Resolve<Vector4>(tintKey));
        AssertPipelineNeutralMaterialInterfaceCompiles(
            session,
            shaderDirectory,
            sessionDesc.Targets.Length);
        await AssertConfiguredClusterOperationsCompile(
            session,
            FindRepositoryRoot(),
            sessionDesc.Targets.Length);
    }

    private static void AssertExactPipelineMaterialLayoutCompiles(
        ISession session,
        string shaderDirectory,
        int targetCount,
        RenderInstancePropertyLayout composedLayout,
        ResolvedRenderInstanceProperty<Vector4> materialTint)
    {
        ResolvedRenderInstanceProperty<uint> bvhRoot = composedLayout.Resolve<uint>(
            ClusterRenderFeature.BvhRootKey);

        string source = $$"""
                #include "render_instance_properties.slang"

                struct ExactRenderInstanceMetadata
                {
                    uint4 PackedWords[({{composedLayout.MetadataWordCount}} + 3) / 4];
                };

                uint LoadExactRenderInstanceMetadata(
                    ExactRenderInstanceMetadata metadata,
                    uint wordIndex)
                {
                    return metadata.PackedWords[wordIndex >> 2u][wordIndex & 3u];
                }

                ByteAddressBuffer ExactInstanceData;
                ConstantBuffer<ExactRenderInstanceMetadata> ExactInstanceProperties;
                RWByteAddressBuffer ExactLayoutProbeOutput;

                [shader("compute")]
                [numthreads(1, 1, 1)]
                void ExactPipelineMaterialLayoutProbe(uint3 dispatchThreadId : SV_DispatchThreadID)
                {
                    uint instanceIndex = dispatchThreadId.x;
                    uint root = RenderInstanceLoad<uint>(
                        ExactInstanceData,
                        LoadExactRenderInstanceMetadata(
                            ExactInstanceProperties,
                            {{bvhRoot.Descriptor.MetadataWordOffset}}u),
                        instanceIndex,
                        {{bvhRoot.Encoding.StorageStride}}u);
                    float4 tint = RenderInstanceLoad<float4>(
                        ExactInstanceData,
                        LoadExactRenderInstanceMetadata(
                            ExactInstanceProperties,
                            {{materialTint.Descriptor.MetadataWordOffset}}u),
                        instanceIndex,
                        {{materialTint.Encoding.StorageStride}}u);
                    ExactLayoutProbeOutput.Store(0u, root);
                    ExactLayoutProbeOutput.Store4(4u, asuint(tint));
                }
                """;
        const string shaderFile = "exact_pipeline_material_layout_probe.slang";
        string shaderPath = Path.Combine(shaderDirectory, shaderFile);
        IModule? module = session.LoadModuleFromSource(
            "exact_pipeline_material_layout_probe",
            shaderPath,
            Slang.CreateBlob(source),
            out ISlangBlob? moduleDiagnostics);
        Assert.True(
            module is not null,
            $"Failed to load {shaderFile}: {moduleDiagnostics?.AsString ?? "no diagnostics"}");

        SlangResult entryPointResult = module.FindAndCheckEntryPoint(
            "ExactPipelineMaterialLayoutProbe",
            SlangStage.Compute,
            out IEntryPoint entryPoint,
            out ISlangBlob? entryPointDiagnostics);
        Assert.True(
            entryPointResult.Succeeded,
            "Failed to check ExactPipelineMaterialLayoutProbe: "
            + (entryPointDiagnostics?.AsString ?? entryPointResult.ToString()));
        AssertEntryPointCompiles(
            session,
            module,
            entryPoint,
            shaderFile,
            "ExactPipelineMaterialLayoutProbe",
            targetCount);
    }

    private static RenderInstancePropertyLayout CreateTestTintMaterialLayout()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        _ = builder.Register<Vector4>(
            "TestTintMaterial",
            new RenderInstancePropertyKey(TestTintCanonicalId),
            new RenderInstancePropertyEncoding(
                "someengine.render.linear.float4.v1",
                valueSize: 16,
                storageAlignment: 16,
                storageStride: 16,
                metadataWordCount: 1));
        return builder.Freeze();
    }

    private static void AssertPipelineNeutralInterfaceSources(string shaderDirectory)
    {
        string materialInterfaces = File.ReadAllText(
            Path.Combine(shaderDirectory, "material_interfaces.slang"));
        string materialVertexInterfaces = File.ReadAllText(
            Path.Combine(shaderDirectory, "material_vertex_interfaces.slang"));
        string vertexEvaluate = File.ReadAllText(
            Path.Combine(shaderDirectory, "vertex_evaluate.slang"));

        Assert.Contains(
            "#include \"material_vertex_interfaces.slang\"",
            materialInterfaces,
            StringComparison.Ordinal);
        Assert.DoesNotContain("vertex_evaluate.slang", materialInterfaces, StringComparison.Ordinal);
        Assert.DoesNotContain("cluster_common.slang", materialInterfaces, StringComparison.Ordinal);
        Assert.DoesNotContain("#include", materialVertexInterfaces, StringComparison.Ordinal);
        Assert.DoesNotContain("cluster", materialVertexInterfaces, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "interface IVertexEvaluate : IMaterialVertex",
            vertexEvaluate,
            StringComparison.Ordinal);
    }

    private static void AssertPipelineNeutralMaterialInterfaceCompiles(
        ISession session,
        string shaderDirectory,
        int targetCount)
    {
        const string source = """
                #include "material_interfaces.slang"

                struct NeutralMaterialVertex : IMaterialVertex
                {
                    typedef float3 DeformedVertex;
                };

                RWStructuredBuffer<float3> NeutralInterfaceProbeOutput;

                [shader("compute")]
                [numthreads(1, 1, 1)]
                void NeutralMaterialInterfaceProbe(uint3 dispatchThreadId : SV_DispatchThreadID)
                {
                    NeutralInterfaceProbeOutput[dispatchThreadId.x] = float3(1.0, 2.0, 3.0);
                }
                """;
        const string shaderFile = "material_interface_neutral_probe.slang";
        string shaderPath = Path.Combine(shaderDirectory, shaderFile);
        IModule? module = session.LoadModuleFromSource(
            "material_interface_neutral_probe",
            shaderPath,
            Slang.CreateBlob(source),
            out ISlangBlob? moduleDiagnostics);
        Assert.True(
            module is not null,
            $"Failed to load {shaderFile}: {moduleDiagnostics?.AsString ?? "no diagnostics"}");

        SlangResult entryPointResult = module.FindAndCheckEntryPoint(
            "NeutralMaterialInterfaceProbe",
            SlangStage.Compute,
            out IEntryPoint entryPoint,
            out ISlangBlob? entryPointDiagnostics);
        Assert.True(
            entryPointResult.Succeeded,
            "Failed to check NeutralMaterialInterfaceProbe: "
            + (entryPointDiagnostics?.AsString ?? entryPointResult.ToString()));
        AssertEntryPointCompiles(
            session,
            module,
            entryPoint,
            shaderFile,
            "NeutralMaterialInterfaceProbe",
            targetCount);
    }

    private static async Task AssertConfiguredClusterOperationsCompile(
        ISession session,
        string repositoryRoot,
        int targetCount)
    {
        AssetManifest manifest = AssetManifest.Load(
            Path.Combine(repositoryRoot, "Library", "AssetManifest"));
        AssetManifestRecord configurationRecord = Assert.Single(
            manifest.List(AssetType<ClusterShaders>.Name));
        await using var loader = new AssetLoader(
            new LooseAssetStorage(repositoryRoot, manifest));
        AssetHandle<ClusterShaders> configurationHandle = loader.Load(
            new AssetId<ClusterShaders>(configurationRecord.Guid));
        await loader.WaitAsync(configurationHandle);
        using AssetRead<ClusterShaders> configurationRead = loader.Read(configurationHandle);
        IList<ClusterShaderOperation> operations = Assert.IsAssignableFrom<IList<ClusterShaderOperation>>(
            configurationRead.Value.Operations);
        Assert.Equal(24, operations.Count);

        var modules = new Dictionary<string, IModule>(StringComparer.OrdinalIgnoreCase);
        foreach (ClusterShaderOperation operation in operations)
        {
            IList<ShaderRef> entries = Assert.IsAssignableFrom<IList<ShaderRef>>(operation.Shaders);
            Assert.NotEmpty(entries);
            ShaderRef first = entries[0];
            Assert.True(
                AssetGuid.TryParse(first.AssetGuid, out AssetGuid shaderGuid),
                $"Operation {operation.Role} has no Shader asset identity.");
            Assert.All(entries, entry => Assert.Equal(first.AssetGuid, entry.AssetGuid));
            AssetHandle<Shader> shaderHandle = loader.Load(new AssetId<Shader>(shaderGuid));
            await loader.WaitAsync(shaderHandle);

            string sourcePath;
            using (AssetRead<Shader> shaderRead = loader.Read(shaderHandle))
            {
                string source = Assert.IsType<string>(shaderRead.Value.ImportTrace?.SourcePath);
                sourcePath = Path.GetFullPath(Path.Combine(
                    repositoryRoot,
                    source.Replace('/', Path.DirectorySeparatorChar)));
            }
            Assert.StartsWith(
                Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar,
                sourcePath,
                StringComparison.OrdinalIgnoreCase);

            if (!modules.TryGetValue(sourcePath, out IModule? module))
            {
                string moduleName = Path.GetFileNameWithoutExtension(sourcePath);
                module = session.LoadModuleFromSource(
                    moduleName,
                    sourcePath,
                    Slang.CreateBlob(File.ReadAllText(sourcePath)),
                    out ISlangBlob? moduleDiagnostics);
                Assert.True(
                    module is not null,
                    $"Failed to load configured shader {sourcePath}: "
                    + (moduleDiagnostics?.AsString ?? "no diagnostics"));
                modules.Add(sourcePath, module);
            }

            foreach (ShaderRef entry in entries)
            {
                AssertConfiguredEntryPointCompiles(
                    session,
                    module,
                    sourcePath,
                    Assert.IsType<string>(entry.EntryPoint),
                    entry.Stage switch
                    {
                        ShaderStage.Compute => SlangStage.Compute,
                        ShaderStage.Vertex => SlangStage.Vertex,
                        ShaderStage.Pixel => SlangStage.Fragment,
                        _ => throw new InvalidDataException(
                            $"Cluster operation {operation.Role} uses unsupported stage {entry.Stage}."),
                    },
                    targetCount);
            }
        }
    }

    private static void AssertConfiguredEntryPointCompiles(
        ISession session,
        IModule module,
        string shaderPath,
        string entryPointName,
        SlangStage stage,
        int targetCount)
    {
        SlangResult entryPointResult = module.FindAndCheckEntryPoint(
            entryPointName,
            stage,
            out IEntryPoint entryPoint,
            out ISlangBlob? entryPointDiagnostics);
        Assert.True(
            entryPointResult.Succeeded,
            $"Failed to check configured {stage} entry point {entryPointName} in {shaderPath}: "
            + (entryPointDiagnostics?.AsString ?? entryPointResult.ToString()));
        AssertEntryPointCompiles(
            session,
            module,
            entryPoint,
            shaderPath,
            entryPointName,
            targetCount);
    }

    private static void AssertDirectPropertyAccessCompiles(
        ISession session,
        string shaderDirectory,
        int targetCount)
    {
        const string source = """
                #include "cluster_instance_properties.slang"

                ByteAddressBuffer InstanceData;
                ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
                RWByteAddressBuffer PropertyProbeOutput;

                [shader("compute")]
                [numthreads(1, 1, 1)]
                void DirectPropertyProbe(uint3 dispatchThreadId : SV_DispatchThreadID)
                {
                    uint instanceIndex = dispatchThreadId.x;
                    PropertyProbeOutput.Store(
                        0u,
                        LoadClusterBvhRoot(
                            InstanceData,
                            LoadRenderInstanceMetadata(
                                InstanceProperties,
                                CLUSTER_BVH_ROOT_METADATA_WORD),
                            instanceIndex));
                    PropertyProbeOutput.Store(
                        4u,
                        LoadClusterMaterialSlotOffset(
                            InstanceData,
                            LoadRenderInstanceMetadata(
                                InstanceProperties,
                                CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
                            instanceIndex));
                    PropertyProbeOutput.Store(
                        8u,
                        asuint(LoadClusterBoundsExpansion(
                            InstanceData,
                            LoadRenderInstanceMetadata(
                                InstanceProperties,
                                CLUSTER_BOUNDS_EXPANSION_METADATA_WORD),
                            instanceIndex)));
                }
                """;
        const string shaderFile = "render_instance_direct_property_probe.slang";
        string shaderPath = Path.Combine(shaderDirectory, shaderFile);
        IModule? module = session.LoadModuleFromSource(
            "render_instance_direct_property_probe",
            shaderPath,
            Slang.CreateBlob(source),
            out ISlangBlob? moduleDiagnostics);
        Assert.True(
            module is not null,
            $"Failed to load {shaderFile}: {moduleDiagnostics?.AsString ?? "no diagnostics"}");

        SlangResult entryPointResult = module.FindAndCheckEntryPoint(
            "DirectPropertyProbe",
            SlangStage.Compute,
            out IEntryPoint entryPoint,
            out ISlangBlob? entryPointDiagnostics);
        Assert.True(
            entryPointResult.Succeeded,
            $"Failed to check DirectPropertyProbe: "
            + (entryPointDiagnostics?.AsString ?? entryPointResult.ToString()));
        AssertEntryPointCompiles(
            session,
            module,
            entryPoint,
            shaderFile,
            "DirectPropertyProbe",
            targetCount);
    }

    private static void AssertEntryPointCompiles(
        ISession session,
        IModule module,
        IEntryPoint entryPoint,
        string shaderFile,
        string entryPointName,
        int targetCount)
    {
        IComponentType[] components = [module, entryPoint];
        SlangResult composeResult = session.CreateCompositeComponentType(
            components,
            out IComponentType composedProgram,
            out ISlangBlob? composeDiagnostics);
        Assert.True(
            composeResult.Succeeded,
            $"Failed to compose entry point {entryPointName} in {shaderFile}: "
            + (composeDiagnostics?.AsString ?? composeResult.ToString()));

        SlangResult linkResult = composedProgram.Link(
            out IComponentType linkedProgram,
            out ISlangBlob? linkDiagnostics);
        Assert.True(
            linkResult.Succeeded,
            $"Failed to link entry point {entryPointName} in {shaderFile}: "
            + (linkDiagnostics?.AsString ?? linkResult.ToString()));

        for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            SlangResult compileResult = linkedProgram.GetEntryPointCode(
                0,
                targetIndex,
                out ISlangBlob code,
                out ISlangBlob? compileDiagnostics);
            Assert.True(
                compileResult.Succeeded && code.GetBufferSize() > 0,
                $"Failed to compile entry point {entryPointName} in {shaderFile} " +
                $"for target {targetIndex}: " +
                (compileDiagnostics?.AsString ?? compileResult.ToString()));
        }
    }

    private static void AssertStructuredBufferLayout(
        SlangShaderReflection reflection,
        Type managedType,
        string shaderTypeName,
        int declaredManagedSize)
    {
        Assert.True(managedType.IsLayoutSequential, $"{managedType.Name} must have sequential layout.");

        int managedSize = Marshal.SizeOf(managedType);
        Assert.Equal(declaredManagedSize, managedSize);

        TypeReflection? reflectedType = reflection.FindTypeByName(shaderTypeName);
        Assert.True(reflectedType.HasValue, $"Slang did not reflect {shaderTypeName}.");

        TypeLayoutReflection? reflectedLayout = reflection.GetTypeLayout(
            reflectedType.Value,
            LayoutRules.DefaultStructuredBuffer);
        Assert.True(reflectedLayout.HasValue, $"Slang did not lay out {shaderTypeName} as a StructuredBuffer element.");

        TypeLayoutReflection layout = reflectedLayout.Value;
        Assert.Equal((nuint)managedSize, layout.GetSize(SlangParameterCategory.Uniform));
        Assert.Equal((nuint)managedSize, layout.GetStride(SlangParameterCategory.Uniform));

        FieldInfo[] managedFields = managedType.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
        Assert.Equal((uint)managedFields.Length, layout.FieldCount);

        foreach (FieldInfo managedField in managedFields)
        {
            nint reflectedFieldIndex = layout.FindFieldIndexByName(managedField.Name);
            Assert.True(reflectedFieldIndex >= 0, $"Slang field {managedType.Name}.{managedField.Name} is missing.");

            VariableLayoutReflection reflectedField = layout.GetFieldByIndex((uint)reflectedFieldIndex);
            Assert.NotEqual(VariableLayoutReflection.Null, reflectedField);
            Assert.Equal(managedField.Name, reflectedField.Name);
            AssertFieldType(managedField, reflectedField.Type);

            nuint managedOffset = checked((nuint)Marshal.OffsetOf(managedType, managedField.Name).ToInt64());
            Assert.Equal(
                managedOffset,
                reflectedField.GetOffset(SlangParameterCategory.Uniform));
        }
    }

    private static void AssertProperty(
        RenderInstancePropertyDescriptor property,
        string key,
        string encodingId,
        int valueSize,
        int alignment)
    {
        Assert.Equal(key, property.Key.Value);
        Assert.Equal(encodingId, property.Encoding.Codec);
        Assert.Equal(valueSize, property.Encoding.ValueSize);
        Assert.Equal(alignment, property.Encoding.StorageAlignment);
        Assert.Equal(valueSize, property.Encoding.StorageStride);
        Assert.Equal(1, property.Encoding.MetadataWordCount);
    }

    private static Type GetRenderInstanceTransformType()
    {
        const string fullName = "SomeEngine.Render.Components.RenderTransform";
        Type? type = typeof(RenderInstancePropertyLayout).Assembly.GetType(
            fullName,
            throwOnError: false,
            ignoreCase: false);
        Assert.NotNull(type);
        Assert.Equal(fullName, type.FullName);
        return type;
    }

    private static void AssertFieldType(FieldInfo managedField, TypeReflection reflectedType)
    {
        Type managedType = managedField.FieldType;
        if (managedType.IsEnum)
            managedType = Enum.GetUnderlyingType(managedType);

        if (managedType == typeof(uint))
        {
            Assert.Equal(SlangTypeKind.Scalar, reflectedType.Kind);
            Assert.Equal(SlangScalarType.UInt32, reflectedType.ScalarType);
            return;
        }
        if (managedType == typeof(float))
        {
            Assert.Equal(SlangTypeKind.Scalar, reflectedType.Kind);
            Assert.Equal(SlangScalarType.Float32, reflectedType.ScalarType);
            return;
        }
        if (managedType == typeof(Vector3)
            || managedType == typeof(Vector4)
            || managedType == typeof(Quaternion))
        {
            Assert.Equal(SlangTypeKind.Vector, reflectedType.Kind);
            Assert.Equal(SlangScalarType.Float32, reflectedType.ScalarType);
            Assert.Equal(
                managedType == typeof(Vector3) ? (nuint)3 : 4,
                reflectedType.ElementCount);
            return;
        }

        Assert.Fail(
            $"Cluster shader ABI test has no scalar/vector contract for " +
            $"{managedField.DeclaringType?.Name}.{managedField.Name} ({managedType}).");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SomeEngine repository above {AppContext.BaseDirectory}.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SlangReflectionCollection
{
    public const string Name = "Slang reflection";
}
