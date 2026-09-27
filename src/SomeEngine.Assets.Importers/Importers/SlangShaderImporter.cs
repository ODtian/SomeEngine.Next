using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using Schema = global::SomeEngine.Assets.Schema;
using SlangProgram = SlangShaderSharp.ShaderProgram;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public const uint ImporterVersion = 27;

    [ThreadStatic]
    private static IGlobalSession? t_globalSession;

    public static IGlobalSession GlobalSession
    {
        get
        {
            if (t_globalSession == null)
            {
                Slang.CreateGlobalSession(Slang.ApiVersion, out t_globalSession);
            }

            return t_globalSession;
        }
    }

    private static TargetDesc[] CreateTargets(
        IGlobalSession globalSession,
        SlangShaderCookProfile profile)
    {
        SlangProfileID dxilProfile = globalSession.FindProfile(profile.DxilProfile);
        SlangProfileID spirvProfile = globalSession.FindProfile(profile.SpirvProfile);
        return
        [
            new() { Format = SlangCompileTarget.Dxil, Profile = dxilProfile },
            new() { Format = SlangCompileTarget.Spirv, Profile = spirvProfile },
        ];
    }

    private static SessionDesc CreateSessionDesc(
        string filePath,
        TargetDesc[] targets)
    {
        CompilerOptionEntry[] options =
        [
            new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
        ];
        return new SessionDesc
        {
            Targets = targets,
            // Engine matrices use System.Numerics' row-vector convention and shader code uses
            // mul(vector, matrix). Preserve that ABI in both DXIL and SPIR-V artifacts.
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            SearchPaths = [Path.GetDirectoryName(filePath)!],
            CompilerOptionEntries = options,
        };
    }

    private static ShaderMetadata CreateMetadata()
    {
        return new ShaderMetadata
        {
            Tags = [],
            MaterialBindings = [],
            MaterialScalarLayouts = [],
            InstanceProperties = [],
        };
    }

    private static Schema.ImportTrace CreateImportTrace(
        ShaderImportContext context,
        string fingerprint,
        FileDependency[] dependencies)
    {
        return new Schema.ImportTrace
        {
            SourcePath = context.FilePath,
            ContentFingerprint = fingerprint,
            Dependencies = dependencies
                .Select(static d => new Schema.DependencyEntry
                {
                    Path = d.RelativePath,
                    ContentHash = d.ContentHash,
                })
                .ToImmutableList(),
            ImporterVersion = ImporterVersion,
        };
    }

    private static Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>> CreateBackendResourceMaps(
        TargetDesc[] targets)
    {
        var maps = new Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>>();
        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            maps[BackendName(targets[targetIndex])] = [];
        }

        return maps;
    }
}

public static partial class SlangShaderImporter
{


    private static void CollectModuleMaterialMetadata(
        ShaderImportState state,
        string source, IModule? sourceModule = null)
    {
        DeclReflection moduleReflection = (sourceModule ?? state.Module).GetModuleReflection();
        state.MaterialScalarTypes.AddRange(
            SlangMaterialMeta.ScalarTypes(
                moduleReflection,
                source,
                state.Dependencies,
                state.Context.ProjectRoot));
        ShaderMetadata metadata = state.Code.Metadata!;
        for (uint index = 0; index < moduleReflection.Count; index++)
        {
            CollectModuleVariableMetadata(moduleReflection[(int)index], ref metadata);
        }
        state.Code.Metadata = metadata;
    }

    private static void CollectModuleVariableMetadata(
        DeclReflection declaration,
        ref ShaderMetadata metadata)
    {
        if (declaration.Kind != DeclReflectionKind.Variable)
        {
            return;
        }

        VariableReflection variable = declaration.AsVariable();
        if (variable == VariableReflection.Null || variable.Type.Kind != SlangTypeKind.ParameterBlock)
        {
            return;
        }

        TypeReflection elementType = variable.Type.ElementType;
        AddPipelineTags(elementType, ref metadata);
        AddParameterBlockBindings(elementType, ref metadata);
    }

    private static void AddPipelineTags(TypeReflection elementType, ref ShaderMetadata metadata)
    {
        for (uint attributeIndex = 0; attributeIndex < elementType.AttributeCount; attributeIndex++)
        {
            AttributeReflection attribute = elementType.GetAttribute(attributeIndex);
            if (attribute.Name == "PipelineTag" && attribute.ArgumentCount > 0)
            {
                metadata = metadata with { Tags = (metadata.Tags ?? []).Add(attribute.GetArgumentValueString(0)) };
            }
        }
    }

    private static void AddParameterBlockBindings(TypeReflection elementType, ref ShaderMetadata metadata)
    {
        for (uint fieldIndex = 0; fieldIndex < elementType.FieldCount; fieldIndex++)
        {
            VariableReflection field = elementType.GetFieldByIndex(fieldIndex);
            SlangMaterialMeta.AddBinding(
                ref metadata,
                field.Name,
                SlangMaterialMeta.ResourceType(field.Type));
        }
    }

    private static void CompileEntries(ShaderImportState state, IComponentType[] components,
        CookedEntryPoint[] entries, IReadOnlyList<string> specializations)
    {
        state.Session.CreateCompositeComponentType(
            components,
            out IComponentType? composedProgram,
            out ISlangBlob? diagnostics);
        state.Lifetime.Track(composedProgram);
        state.Lifetime.Track(diagnostics);
        if (composedProgram == null)
        {
            throw new InvalidDataException(
                $"Failed to compose cooked shader program: {GetString(diagnostics)}");
        }

        if (specializations.Count != 0)
        {
            SpecializationArg[] arguments = specializations.Select(SpecializationArg.FromExpression).ToArray();
            SlangResult specializationResult = composedProgram.Specialize(arguments, arguments.Length, out IComponentType specialized, out ISlangBlob? specializationDiagnostics);
            state.Lifetime.Track(specialized);
            state.Lifetime.Track(specializationDiagnostics);
            if (specializationResult.Failed || specialized is null)
                throw new InvalidDataException("Shader specialization failed: " + GetString(specializationDiagnostics));
            composedProgram = specialized;
        }
        composedProgram.Link(out IComponentType? linkedProgram, out ISlangBlob? linkDiagnostics);
        state.Lifetime.Track(linkedProgram);
        state.Lifetime.Track(linkDiagnostics);
        if (linkedProgram == null)
        {
            throw new InvalidDataException(
                $"Failed to link cooked shader program: {GetString(linkDiagnostics)}");
        }

        AddMaterialScalarLayouts(state, linkedProgram);
        AddCookedPrograms(state, linkedProgram, entries);
    }

    private static void AddCookedPrograms(
        ShaderImportState state,
        IComponentType linkedProgram,
        ReadOnlySpan<CookedEntryPoint> entries)
    {
        for (int targetIndex = 0; targetIndex < state.Targets.Length; targetIndex++)
        {
            ShaderReflection reflection = linkedProgram.GetLayout(
                (nint)targetIndex,
                out ISlangBlob? diagnostics);
            state.Lifetime.Track(diagnostics);
            if (reflection == ShaderReflection.Null)
                throw new InvalidDataException(
                    $"Failed to obtain cooked shader layout for target {targetIndex}: " +
                    GetString(diagnostics));

            string backend = BackendName(state.Targets[targetIndex]);
            SlangProgram program = SlangProgram.Capture(linkedProgram, targetIndex, backend);
            using var storage = new PageBuffer(state.Code.Pages);
            using var output = new PageBufferStream(storage);
            program.Write(output);
            using PageBuffer payload = storage.Slice(0, output.Length);
            using PageRange<byte> segments = payload.Retain();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (ReadOnlyMemory<byte> segment in segments.Sequence) hash.AppendData(segment.Span);
            int programIndex = state.Code.Programs!.Count;
            ulong chunkKey = ShaderBlock.BytecodeChunkKey(programIndex);
            state.Code.Chunks.Set(chunkKey, payload);
            state.Code.Programs.Add(new CookedShaderProgram
            {
                Backend = backend,
                DataChunkKey = chunkKey,
                DataDecodedLength = checked((ulong)output.Length),
                ContentHash = Convert.ToHexString(hash.GetHashAndReset()),
            });

            if (reflection.EntryPointCount != checked((nuint)entries.Length) ||
                program.EntryPoints.Count != entries.Length)
            {
                throw new InvalidDataException(
                    $"Cooked target {targetIndex} exposes {reflection.EntryPointCount} native and " +
                    $"{program.EntryPoints.Count} detached entries; expected {entries.Length}.");
            }

            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                SlangResult metadataResult = linkedProgram.GetEntryPointMetadata(
                    entryIndex,
                    targetIndex,
                    out IMetadata metadata,
                    out ISlangBlob? metadataDiagnostics);
                state.Lifetime.Track(metadata);
                state.Lifetime.Track(metadataDiagnostics);
                if (metadataResult.Failed || metadata is null)
                {
                    throw new InvalidDataException(
                        $"Failed to obtain compiled entry-point metadata for target {targetIndex}, " +
                        $"entry {entryIndex}: {GetString(metadataDiagnostics)}");
                }

                AddEntryPointReflection(
                    state,
                    backend,
                    reflection,
                    reflection.GetEntryPointByIndex(checked((nuint)entryIndex)),
                    metadata);
                AddEntryPointAttributes(
                    state.Code,
                    programIndex,
                    entryIndex,
                    entries[entryIndex].Attributes);
                AddEntryPointMetadata(
                    state.Code,
                    programIndex,
                    entryIndex,
                    entries[entryIndex].Attributes);
            }
        }
    }

    private static void AddEntryPointReflection(
        ShaderImportState state,
        string backendName,
        ShaderReflection reflection,
        EntryPointReflection entryReflection,
        IMetadata metadata)
    {
        ShaderStage stage = entryReflection.Stage != SlangStage.None
            ? MapStage(entryReflection.Stage)
            : ShaderStage.Vertex;
        var globalResources = new Dictionary<SlangBindingMap.ResourceKey, uint>();
        ShaderMetadata materialMetadata = state.Code.Metadata!;
        CollectGlobalMaterialResources(reflection, ref materialMetadata);
        EntryPointMaterialResources(entryReflection, ref materialMetadata);
        state.Code.Metadata = materialMetadata;
        CollectGlobalResources(reflection, stage, globalResources, backendName);
        Dictionary<SlangBindingMap.SpaceKey, uint> entryBases =
            SlangBindingMap.Next(globalResources, backendName);
        var parameterResources = new Dictionary<SlangBindingMap.ResourceKey, uint>();
        CollectEntryPointResources(
            entryReflection,
            stage,
            parameterResources,
            backendName,
            entryBases);
        RemoveUnusedEntryPointResources(globalResources, backendName, metadata);
        SlangBindingMap.Merge(globalResources, parameterResources);
        Dictionary<SlangBindingMap.ResourceKey, uint> entryResources = globalResources;
        SlangBindingMap.Merge(state.BackendResourceMaps[backendName], entryResources);
        state.Code.EntryPointReflections!.Add(
            new SomeEngine.Resources.ShaderEntryPointReflection
            {
                Backend = backendName,
                EntryPoint = entryReflection.Name,
                Stage = stage,
                Reflection = SlangBindingMap.Create(entryResources),
            });
    }

    private static void RemoveUnusedEntryPointResources(
        Dictionary<SlangBindingMap.ResourceKey, uint> resources,
        string backendName,
        IMetadata metadata)
    {
        List<SlangBindingMap.ResourceKey>? unused = null;
        foreach (SlangBindingMap.ResourceKey resource in resources.Keys)
        {
            SlangParameterCategory category = ParameterCategory(
                backendName,
                resource.Kind);
            SlangResult result = metadata.IsParameterLocationUsed(
                category,
                (nuint)resource.Space,
                (nuint)resource.Binding,
                out bool used);
            if (result.Failed)
            {
                throw new InvalidDataException(
                    $"Slang could not determine whether shader resource '{resource.Name}' "
                        + $"at {category} {resource.Space}:{resource.Binding} is used.");
            }
            if (!used)
                (unused ??= []).Add(resource);
        }

        if (unused is null)
            return;
        for (int index = 0; index < unused.Count; index++)
            resources.Remove(unused[index]);
    }

    private static SlangParameterCategory ParameterCategory(
        string backendName,
        SomeEngine.Resources.DescriptorType kind)
    {
        if (string.Equals(backendName, "spirv", StringComparison.Ordinal))
            return SlangParameterCategory.DescriptorTableSlot;

        return kind switch
        {
            SomeEngine.Resources.DescriptorType.ConstantBuffer => SlangParameterCategory.ConstantBuffer,
            SomeEngine.Resources.DescriptorType.SampledTexture
                or SomeEngine.Resources.DescriptorType.ReadOnlyBuffer
                or SomeEngine.Resources.DescriptorType.AccelerationStructure => SlangParameterCategory.ShaderResource,
            SomeEngine.Resources.DescriptorType.StorageTexture
                or SomeEngine.Resources.DescriptorType.StorageBuffer => SlangParameterCategory.UnorderedAccess,
            SomeEngine.Resources.DescriptorType.Sampler => SlangParameterCategory.SamplerState,
            _ => throw new InvalidDataException(
                $"ShaderData entry reflection cannot query usage for slot kind {kind}."),
        };
    }

    private static HashSet<string> EntryPointMaterialResources(
        EntryPointReflection entryReflection,
        ref ShaderMetadata metadata)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (uint parameterIndex = 0; parameterIndex < entryReflection.ParameterCount; parameterIndex++)
        {
            SlangMaterialMeta.BindEntry(
                entryReflection.GetParameterByIndex(parameterIndex),
                ref metadata,
                names);
        }

        return names;
    }

    private static void CollectGlobalMaterialResources(
        ShaderReflection reflection,
        ref ShaderMetadata metadata)
    {
        for (uint parameterIndex = 0; parameterIndex < reflection.ParameterCount; parameterIndex++)
            SlangMaterialMeta.BindEntry(reflection.GetParameterByIndex(parameterIndex), ref metadata);
    }

    private static void CollectGlobalResources(
        ShaderReflection reflection,
        ShaderStage stage,
        Dictionary<SlangBindingMap.ResourceKey, uint> resources,
        string backendName)
    {
        for (uint parameterIndex = 0; parameterIndex < reflection.ParameterCount; parameterIndex++)
        {
            SlangBindingMap.Collect(
                reflection.GetParameterByIndex(parameterIndex),
                stage,
                resources,
                backendName);
        }
    }

    private static void CollectEntryPointResources(
        EntryPointReflection entryReflection,
        ShaderStage stage,
        Dictionary<SlangBindingMap.ResourceKey, uint> resources,
        string backendName,
        IReadOnlyDictionary<SlangBindingMap.SpaceKey, uint> bindingBases)
    {
        for (uint parameterIndex = 0; parameterIndex < entryReflection.ParameterCount; parameterIndex++)
        {
            SlangBindingMap.Collect(
                entryReflection.GetParameterByIndex(parameterIndex),
                stage,
                resources,
                backendName,
                entryBases: bindingBases);
        }
    }
}

public static partial class SlangShaderImporter
{


    private static void AddMaterialScalarLayouts(
        ShaderImportState state,
        IComponentType linkedProgram)
    {
        ShaderReflection baseReflection = linkedProgram.GetLayout(0, out ISlangBlob? diagnostics);
        state.Lifetime.Track(diagnostics);
        if (baseReflection == ShaderReflection.Null)
        {
            baseReflection = linkedProgram.GetLayout(1, out diagnostics);
            state.Lifetime.Track(diagnostics);
        }

        ShaderMetadata metadata = state.Code.Metadata!;
        SlangMaterialMeta.ScalarLayouts(
            baseReflection,
            state.MaterialScalarTypes,
            ref metadata);
        state.Code.Metadata = metadata;
    }

    private static void AddEntryPointAttributes(
        Schema.ShaderBlock asset,
        int programIndex,
        int entryPointIndex,
        IReadOnlyList<SlangEntryMeta.Attr> attributes)
    {
        for (int attributeIndex = 0; attributeIndex < attributes.Count; attributeIndex++)
        {
            asset.EntryPointAttributes!.Add(
                new SomeEngine.Resources.ShaderEntryPointAttribute
                {
                    ProgramIndex = programIndex,
                    EntryPointIndex = entryPointIndex,
                    Name = attributes[attributeIndex].Name,
                    Args = attributes[attributeIndex].Args.Count == 0
                        ? []
                        : [.. attributes[attributeIndex].Args],
                });
        }
    }

    private static void AddEntryPointMetadata(
        Schema.ShaderBlock asset,
        int programIndex,
        int entryPointIndex,
        IReadOnlyList<SlangEntryMeta.Attr> attributes)
    {
        ShaderEntryPointMetadata? entryPointMetadata = SlangEntryMeta.Create(
            programIndex,
            entryPointIndex,
            attributes);
        if (entryPointMetadata != null)
        {
            asset.EntryPointMetadata!.Add(entryPointMetadata);
        }
    }

    private static void AddBackendReflections(
        Schema.ShaderBlock asset,
        Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>> backendResourceMaps)
    {
        foreach (var kvp in backendResourceMaps)
        {
            var reflectionData = SlangBindingMap.Create(kvp.Value);
            asset.Reflections!.Add(
                new SomeEngine.Resources.BackendReflection
                {
                    Backend = kvp.Key,
                    Reflection = reflectionData,
                });
        }
    }

    private static string BackendName(TargetDesc target)
        => target.Format == SlangCompileTarget.Dxil ? "dxil" : "spirv";
}

public static partial class SlangShaderImporter
{


    private readonly record struct ShaderImportContext(string FilePath, string ProjectRoot);

    private sealed record ShaderImportState(
        ShaderImportContext Context,
        TargetDesc[] Targets,
        SlangImportLifetime Lifetime,
        ISession Session,
        IModule Module,
        FileDependency[] Dependencies,
        Schema.ShaderAsset Asset,
        Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>> BackendResourceMaps,
        List<string> MaterialScalarTypes)
    {
        public ShaderBlock Code => Asset.GetBlock<ShaderBlock>();
    }

    private readonly record struct CookedEntryPoint(
        IEntryPoint EntryPoint,
        IReadOnlyList<SlangEntryMeta.Attr> Attributes);

    private static SomeEngine.Resources.ShaderStage MapStage(SlangStage stage)
    {
        switch (stage)
        {
            case SlangStage.None:
                Console.WriteLine(
                    "Warning: Slang reported ShaderStage.None. Falling back to Vertex."
                );
                return SomeEngine.Resources.ShaderStage.Vertex;
            case SlangStage.Vertex:
                return SomeEngine.Resources.ShaderStage.Vertex;
            case SlangStage.Fragment:
                return SomeEngine.Resources.ShaderStage.Pixel;
            case SlangStage.Compute:
                return SomeEngine.Resources.ShaderStage.Compute;
            case SlangStage.Hull:
                return SomeEngine.Resources.ShaderStage.Hull;
            case SlangStage.Domain:
                return SomeEngine.Resources.ShaderStage.Domain;
            case SlangStage.Geometry:
                return SomeEngine.Resources.ShaderStage.Geometry;
            case SlangStage.Amplification:
                return SomeEngine.Resources.ShaderStage.Amplification;
            case SlangStage.Mesh:
                return SomeEngine.Resources.ShaderStage.Mesh;
            case SlangStage.RayGeneration:
                return SomeEngine.Resources.ShaderStage.RayGen;
            case SlangStage.Miss:
                return SomeEngine.Resources.ShaderStage.RayMiss;
            case SlangStage.ClosestHit:
                return SomeEngine.Resources.ShaderStage.RayClosestHit;
            case SlangStage.AnyHit:
                return SomeEngine.Resources.ShaderStage.RayAnyHit;
            case SlangStage.Intersection:
                return SomeEngine.Resources.ShaderStage.RayIntersection;
            case SlangStage.Callable:
                return SomeEngine.Resources.ShaderStage.Callable;
            case SlangStage.Node:
                return SomeEngine.Resources.ShaderStage.Node;
            default:
                throw new NotImplementedException($"Stage {stage} not supported");
        }
    }

    private static string? GetString(ISlangBlob? blob)
    {
        if (blob == null)
            return null;
        unsafe
        {
            return Encoding.UTF8.GetString(
                (byte*)blob.GetBufferPointer(),
                (int)blob.GetBufferSize()
            );
        }
    }

}

