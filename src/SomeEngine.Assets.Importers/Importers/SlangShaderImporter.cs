using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using Schema = global::SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public const uint ImporterVersion = AssetFormatVersions.SlangShaderImporterVersion;
    public const uint ShaderAssetSchemaVersion = AssetFormatVersions.ShaderAssetSchemaVersion;

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

    public static Schema.Shader Import(string filePath, string? source = null)
    {
        SourceMeta sourceMeta = SourceMetaFiles.GetOrCreate(filePath);
        SlangShaderCookProfile profile = ResolveProfile(sourceMeta, filePath);
        AssetMeta? existingAsset = AssetMetaFiles.TryLoad(
            Path.ChangeExtension(Path.GetFullPath(filePath), ".shader.asset")
        );
        return Import(filePath, sourceMeta, existingAsset, profile, source);
    }

    public static Schema.Shader ImportTransient(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        var sourceMeta = File.Exists(SourceMetaFiles.GetMetaPath(filePath))
            ? SourceMetaFiles.Load(filePath)
            : new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = nameof(SlangShaderImporter),
            };

        AssetMeta? existingAsset = AssetMetaFiles.TryLoad(
            Path.ChangeExtension(filePath, ".shader.asset")
        );
        SlangShaderCookProfile profile = ResolveProfile(sourceMeta, filePath);
        return Import(filePath, sourceMeta, existingAsset, profile, source: null, writeCache: false);
    }

    public static Schema.Shader ImportTransient(
        string filePath,
        SlangShaderCookProfile profile)
    {
        filePath = Path.GetFullPath(filePath);
        var sourceMeta = File.Exists(SourceMetaFiles.GetMetaPath(filePath))
            ? SourceMetaFiles.Load(filePath)
            : new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = nameof(SlangShaderImporter),
            };

        AssetMeta? existingAsset = AssetMetaFiles.TryLoad(
            Path.ChangeExtension(filePath, ".shader.asset")
        );
        return Import(filePath, sourceMeta, existingAsset, profile, source: null, writeCache: false);
    }

    public static Schema.Shader Import(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        SlangShaderCookProfile profile,
        string? source = null,
        bool writeCache = true
    )
        => ImportCore(
            filePath,
            sourceMeta,
            existingAsset,
            profile,
            source,
            writeCache,
            trackObserver: null);

    internal static Schema.Shader ImportTransientForLifetimeTest(
        string filePath,
        Action<object> trackObserver)
    {
        ArgumentNullException.ThrowIfNull(trackObserver);
        filePath = Path.GetFullPath(filePath);
        var sourceMeta = new SourceMeta
        {
            SourceGuid = SourceGuid.New(),
            Importer = nameof(SlangShaderImporter),
        };
        return ImportCore(
            filePath,
            sourceMeta,
            existingAsset: null,
            ResolveProfile(sourceMeta, filePath),
            source: null,
            writeCache: false,
            trackObserver);
    }

    private static Schema.Shader ImportCore(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        SlangShaderCookProfile profile,
        string? source,
        bool writeCache,
        Action<object>? trackObserver)
    {
        ShaderImportContext context = CreateImportContext(
            filePath,
            sourceMeta,
            existingAsset,
            profile,
            source,
            writeCache);

        string importSource = context.Source ?? File.ReadAllText(context.FilePath);
        using var lifetime = new SlangImportLifetime(trackObserver);
        ShaderImportState state = CreateImportState(context, importSource, lifetime);
        CollectModuleMaterialMetadata(state, importSource);
        CollectEntryPoints(state);
        SlangMaterialMeta.SortInstanceProperties(state.Metadata);
        AddBackendReflections(state.Asset, state.BackendResourceMaps);
        SaveShaderCache(context, state.Asset, state.Fingerprint, state.Dependencies);
        return state.Asset;
    }

    private static ShaderImportContext CreateImportContext(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        SlangShaderCookProfile profile,
        string? source,
        bool writeCache)
    {
        filePath = Path.GetFullPath(filePath);
        string cachePath = Path.ChangeExtension(filePath, ".shader.asset");
        string projectRoot = SlangDeps.ProjectRoot(filePath);
        string subAssetKey = "shader:main";
        AssetGuid assetGuid = SlangDeps.GuidFor(sourceMeta.SourceGuid, subAssetKey);
        if (existingAsset != null && existingAsset.AssetGuid != assetGuid)
        {
            existingAsset = null;
        }

        return new ShaderImportContext(
            filePath,
            cachePath,
            projectRoot,
            subAssetKey,
            assetGuid,
            sourceMeta,
            existingAsset,
            profile,
            source,
            writeCache);
    }

    private static ShaderImportState CreateImportState(
        ShaderImportContext context,
        string source,
        SlangImportLifetime lifetime)
    {
        string name = Path.GetFileNameWithoutExtension(context.FilePath);
        IGlobalSession globalSession = GlobalSession;
        TargetDesc[] targets = CreateTargets(globalSession, context.Profile);
        SessionDesc sessionDesc = CreateSessionDesc(context.FilePath, context.ProjectRoot, targets);
        globalSession.CreateSession(sessionDesc, out ISession session);
        lifetime.Track(session);

        ISlangBlob sourceBlob = lifetime.Track(Slang.CreateBlob(Encoding.UTF8.GetBytes(source)))!;
        IModule? module = session.LoadModuleFromSource(name, context.FilePath, sourceBlob, out var diagnostics);
        lifetime.Track(module);
        lifetime.Track(diagnostics);
        if (module == null)
        {
            throw new Exception($"Failed to load module {name}: {GetString(diagnostics)}");
        }

        DependencyEntryData[] dependencies = SlangDeps.Collect(module, context.FilePath, context.ProjectRoot);
        string fingerprint = SlangDeps.Fingerprint(
            dependencies,
            ImporterVersion,
            context.Profile.FingerprintPart);
        ShaderMetadata metadata = CreateMetadata();
        Schema.Shader asset = CreateShaderAsset(context, name, fingerprint, dependencies, metadata);
        return new ShaderImportState(
            context,
            targets,
            lifetime,
            session,
            module,
            dependencies,
            fingerprint,
            metadata,
            asset,
            CreateBackendResourceMaps(targets),
            []);
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
        string projectRoot,
        TargetDesc[] targets)
    {
        CompilerOptionEntry[] options =
        [
            new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
        ];
        return new SessionDesc
        {
            Targets = targets,
            // Engine matrices use System.Numerics' row-vector convention and shader code uses
            // mul(vector, matrix). Preserve that ABI in both DXIL and SPIR-V artifacts.
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            SearchPaths = ShaderSearchPaths(filePath, projectRoot),
            CompilerOptionEntries = options,
        };
    }

    private static string[] ShaderSearchPaths(string filePath, string projectRoot)
    {
        string sourceDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
        string libraryDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        return Directory.Exists(libraryDirectory) &&
               !string.Equals(sourceDirectory, libraryDirectory, StringComparison.OrdinalIgnoreCase)
            ? [sourceDirectory, libraryDirectory]
            : [sourceDirectory];
    }

    private static ShaderMetadata CreateMetadata()
    {
        return new ShaderMetadata
        {
            Tags = [],
            MaterialBindings = [],
            MaterialScalarLayouts = [],
            MaterialInstanceProperties = [],
        };
    }

    private static Schema.Shader CreateShaderAsset(
        ShaderImportContext context,
        string name,
        string fingerprint,
        DependencyEntryData[] dependencies,
        ShaderMetadata metadata)
    {
        return new Schema.Shader
        {
            SchemaVersion = ShaderAssetSchemaVersion,
            AssetGuid = context.AssetGuid.ToFlatString(),
            Name = name,
            ImportTrace = CreateImportTrace(context, fingerprint, dependencies),
            Variants = [],
            EntryPointAttributes = [],
            Reflections = [],
            EntryPointReflections = [],
            EntryPointMetadata = [],
            Metadata = metadata,
        };
    }

    private static Schema.ImportTrace CreateImportTrace(
        ShaderImportContext context,
        string fingerprint,
        DependencyEntryData[] dependencies)
    {
        return new Schema.ImportTrace
        {
            SourceGuid = context.SourceMeta.SourceGuid.ToFlatString(),
            SourcePath = context.FilePath,
            SubAssetKey = context.SubAssetKey,
            ContentFingerprint = fingerprint,
            Dependencies = dependencies
                .Select(static d => new Schema.DependencyEntry
                {
                    Path = d.RelativePath,
                    ContentHash = d.ContentHash,
                })
                .ToList(),
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
        string source)
    {
        DeclReflection moduleReflection = state.Module.GetModuleReflection();
        state.MaterialScalarTypes.AddRange(
            SlangMaterialMeta.ScalarTypes(
                moduleReflection,
                source,
                state.Dependencies,
                state.Context.ProjectRoot));
        for (uint index = 0; index < moduleReflection.Count; index++)
        {
            CollectModuleVariableMetadata(moduleReflection[(int)index], state.Metadata);
        }
    }

    private static void CollectModuleVariableMetadata(
        DeclReflection declaration,
        ShaderMetadata metadata)
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
        AddPipelineTags(elementType, metadata);
        AddParameterBlockBindings(elementType, metadata);
    }

    private static void AddPipelineTags(TypeReflection elementType, ShaderMetadata metadata)
    {
        for (uint attributeIndex = 0; attributeIndex < elementType.AttributeCount; attributeIndex++)
        {
            AttributeReflection attribute = elementType.GetAttribute(attributeIndex);
            if (attribute.Name == "PipelineTag" && attribute.ArgumentCount > 0)
            {
                metadata.Tags!.Add(attribute.GetArgumentValueString(0));
            }
        }
    }

    private static void AddParameterBlockBindings(TypeReflection elementType, ShaderMetadata metadata)
    {
        for (uint fieldIndex = 0; fieldIndex < elementType.FieldCount; fieldIndex++)
        {
            VariableReflection field = elementType.GetFieldByIndex(fieldIndex);
            SlangMaterialMeta.AddBinding(
                metadata,
                field.Name,
                SlangMaterialMeta.ResourceType(field.Type));
        }
    }

    private static void CollectEntryPoints(ShaderImportState state)
    {
        int entryPointCount = state.Module.GetDefinedEntryPointCount();
        for (int index = 0; index < entryPointCount; index++)
        {
            if (!TryCreateLinkedEntryPoint(state, index, out LinkedEntryPoint linkedEntryPoint))
            {
                continue;
            }

            AddCompiledVariants(state, linkedEntryPoint);
            AddEntryPointReflections(state, linkedEntryPoint);
            AddMaterialScalarLayouts(state, linkedEntryPoint.LinkedProgram);
        }
    }

    private static bool TryCreateLinkedEntryPoint(
        ShaderImportState state,
        int index,
        out LinkedEntryPoint linkedEntryPoint)
    {
        linkedEntryPoint = default;
        state.Module.GetDefinedEntryPoint(index, out IEntryPoint entryPoint);
        state.Lifetime.Track(entryPoint);
        List<SlangEntryMeta.Attr> attributes = SlangEntryMeta.Read(entryPoint.GetFunctionReflection());
        state.Session.CreateCompositeComponentType(
            [state.Module, entryPoint],
            out IComponentType? composedProgram,
            out ISlangBlob? diagnostics);
        state.Lifetime.Track(composedProgram);
        state.Lifetime.Track(diagnostics);
        if (composedProgram == null)
        {
            Console.WriteLine($"Warning: Failed to compose entry point {index}: {GetString(diagnostics)}");
            return false;
        }

        composedProgram.Link(out IComponentType? linkedProgram, out ISlangBlob? linkDiagnostics);
        state.Lifetime.Track(linkedProgram);
        state.Lifetime.Track(linkDiagnostics);
        if (linkedProgram == null)
        {
            Console.WriteLine($"Warning: Failed to link entry point {index}: {GetString(linkDiagnostics)}");
            return false;
        }

        linkedEntryPoint = new LinkedEntryPoint(entryPoint, linkedProgram, attributes);
        return true;
    }

    private static void AddEntryPointReflections(
        ShaderImportState state,
        LinkedEntryPoint linkedEntryPoint)
    {
        for (int targetIndex = 0; targetIndex < state.Targets.Length; targetIndex++)
        {
            ShaderReflection reflection = linkedEntryPoint.LinkedProgram.GetLayout(
                (nint)targetIndex,
                out ISlangBlob? diagnostics);
            state.Lifetime.Track(diagnostics);
            if (reflection == ShaderReflection.Null)
            {
                continue;
            }

            SlangResult metadataResult = linkedEntryPoint.LinkedProgram.GetEntryPointMetadata(
                0,
                targetIndex,
                out Metadata? metadata,
                out ISlangBlob? metadataDiagnostics);
            state.Lifetime.Track(metadata);
            state.Lifetime.Track(metadataDiagnostics);
            if (metadataResult.Failed)
            {
                throw new InvalidDataException(
                    $"Failed to obtain compiled entry-point metadata for target {targetIndex}: "
                        + GetString(metadataDiagnostics));
            }
            if (metadata is null)
            {
                throw new InvalidDataException(
                    $"Slang returned no compiled entry-point metadata for target {targetIndex}.");
            }

            AddEntryPointReflection(
                state,
                BackendName(state.Targets[targetIndex]),
                reflection,
                metadata);
        }
    }

    private static void AddEntryPointReflection(
        ShaderImportState state,
        string backendName,
        ShaderReflection reflection,
        Metadata metadata)
    {
        EntryPointReflection entryReflection = reflection.GetEntryPointByIndex(0);
        ShaderStage stage = entryReflection.Stage != SlangStage.None
            ? MapStage(entryReflection.Stage)
            : ShaderStage.Vertex;
        var entryResources = new Dictionary<SlangBindingMap.ResourceKey, uint>();
        HashSet<string> materialResourceNames = EntryPointMaterialResources(entryReflection, state.Metadata);
        CollectGlobalResources(reflection, stage, entryResources, backendName, materialResourceNames);
        CollectEntryPointResources(entryReflection, stage, entryResources, backendName);
        RemoveUnusedEntryPointResources(entryResources, backendName, metadata);
        SlangBindingMap.Merge(state.BackendResourceMaps[backendName], entryResources);
        state.Asset.EntryPointReflections!.Add(
            new Schema.ShaderEntryPointReflection
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
        Metadata metadata)
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
        Schema.DescriptorType kind)
    {
        if (string.Equals(backendName, "spirv", StringComparison.Ordinal))
            return SlangParameterCategory.DescriptorTableSlot;

        return kind switch
        {
            Schema.DescriptorType.ConstantBuffer => SlangParameterCategory.ConstantBuffer,
            Schema.DescriptorType.SampledTexture
                or Schema.DescriptorType.ReadOnlyBuffer
                or Schema.DescriptorType.AccelerationStructure => SlangParameterCategory.ShaderResource,
            Schema.DescriptorType.StorageTexture
                or Schema.DescriptorType.StorageBuffer => SlangParameterCategory.UnorderedAccess,
            Schema.DescriptorType.Sampler => SlangParameterCategory.SamplerState,
            _ => throw new InvalidDataException(
                $"Shader entry reflection cannot query usage for slot kind {kind}."),
        };
    }

    private static HashSet<string> EntryPointMaterialResources(
        EntryPointReflection entryReflection,
        ShaderMetadata metadata)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (uint parameterIndex = 0; parameterIndex < entryReflection.ParameterCount; parameterIndex++)
        {
            SlangMaterialMeta.BindEntry(
                entryReflection.GetParameterByIndex(parameterIndex),
                metadata,
                names);
        }

        return names;
    }

    private static void CollectGlobalResources(
        ShaderReflection reflection,
        ShaderStage stage,
        Dictionary<SlangBindingMap.ResourceKey, uint> resources,
        string backendName,
        HashSet<string> materialResourceNames)
    {
        for (uint parameterIndex = 0; parameterIndex < reflection.ParameterCount; parameterIndex++)
        {
            SlangBindingMap.Collect(
                reflection.GetParameterByIndex(parameterIndex),
                stage,
                resources,
                backendName,
                skipNames: materialResourceNames);
        }
    }

    private static void CollectEntryPointResources(
        EntryPointReflection entryReflection,
        ShaderStage stage,
        Dictionary<SlangBindingMap.ResourceKey, uint> resources,
        string backendName)
    {
        var bindingBases = SlangBindingMap.Next(resources, backendName);
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

        SlangMaterialMeta.ScalarLayouts(
            baseReflection,
            state.MaterialScalarTypes,
            state.Metadata);
    }

    private static void AddCompiledVariants(
        ShaderImportState state,
        LinkedEntryPoint linkedEntryPoint)
    {
        ShaderReflection baseReflection = linkedEntryPoint.LinkedProgram.GetLayout(
            0,
            out ISlangBlob? diagnostics);
        state.Lifetime.Track(diagnostics);
        if (baseReflection == ShaderReflection.Null)
        {
            baseReflection = linkedEntryPoint.LinkedProgram.GetLayout(1, out diagnostics);
            state.Lifetime.Track(diagnostics);
        }

        EntryPointReflection entryPointReflection = baseReflection.GetEntryPointByIndex(0);
        for (int targetIndex = 0; targetIndex < state.Targets.Length; targetIndex++)
        {
            AddCompiledVariant(state, linkedEntryPoint, entryPointReflection, targetIndex);
        }
    }

    private static void AddCompiledVariant(
        ShaderImportState state,
        LinkedEntryPoint linkedEntryPoint,
        EntryPointReflection entryPointReflection,
        int targetIndex)
    {
        linkedEntryPoint.LinkedProgram.GetEntryPointCode(0, targetIndex, out var codeBlob, out var diagnostics);
        state.Lifetime.Track(codeBlob);
        state.Lifetime.Track(diagnostics);
        if (codeBlob == null)
        {
            Console.WriteLine($"Warning: Failed to get code for target {targetIndex}: {GetString(diagnostics)}");
            return;
        }

        int variantIndex = state.Asset.Variants!.Count;
        byte[] rawBytes = GetBytes(codeBlob);
        state.Asset.Variants.Add(CreateBytecode(state.Targets[targetIndex], entryPointReflection, rawBytes));
        AddEntryPointAttributes(state.Asset, variantIndex, linkedEntryPoint.Attributes);
        AddEntryPointMetadata(state.Asset, variantIndex, linkedEntryPoint.Attributes);
    }

    private static ShaderBytecode CreateBytecode(
        TargetDesc target,
        EntryPointReflection entryPointReflection,
        byte[] rawBytes)
    {
        byte[] contentHash = target.Format == SlangCompileTarget.Spirv
            ? ComputeSpirvSemanticHash(rawBytes)
            : SHA256.HashData(rawBytes);
        return new ShaderBytecode
        {
            Backend = BackendName(target),
            Stage = MapStage(entryPointReflection.Stage),
            EntryPoint = entryPointReflection.Name,
            Data = rawBytes,
            ContentHash = Convert.ToHexString(contentHash),
        };
    }

    private static void AddEntryPointAttributes(
        Schema.Shader asset,
        int variantIndex,
        IReadOnlyList<SlangEntryMeta.Attr> attributes)
    {
        for (int attributeIndex = 0; attributeIndex < attributes.Count; attributeIndex++)
        {
            asset.EntryPointAttributes!.Add(
                new Schema.ShaderEntryPointAttribute
                {
                    VariantIndex = variantIndex,
                    Name = attributes[attributeIndex].Name,
                    Args = attributes[attributeIndex].Args.Count == 0
                        ? []
                        : [.. attributes[attributeIndex].Args],
                });
        }
    }

    private static void AddEntryPointMetadata(
        Schema.Shader asset,
        int variantIndex,
        IReadOnlyList<SlangEntryMeta.Attr> attributes)
    {
        ShaderEntryPointMetadata? entryPointMetadata = SlangEntryMeta.Create(variantIndex, attributes);
        if (entryPointMetadata != null)
        {
            asset.EntryPointMetadata!.Add(entryPointMetadata);
        }
    }

    private static void AddBackendReflections(
        Schema.Shader asset,
        Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>> backendResourceMaps)
    {
        foreach (var kvp in backendResourceMaps)
        {
            var reflectionData = new Schema.ShaderReflectionData { Resources = [] };
            SlangBindingMap.Fill(kvp.Value, reflectionData);
            asset.Reflections!.Add(
                new Schema.BackendReflection
                {
                    Backend = kvp.Key,
                    Reflection = reflectionData,
                });
        }
    }

    private static void SaveShaderCache(
        ShaderImportContext context,
        Schema.Shader asset,
        string fingerprint,
        DependencyEntryData[] dependencies)
    {
        if (!context.WriteCache || !File.Exists(context.FilePath))
        {
            return;
        }

        try
        {
            AssetWriter.Write(asset, context.CachePath);
            AssetMetaFiles.Save(context.CachePath, CreateAssetMeta(context, asset, fingerprint, dependencies));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to save shader asset cache to {context.CachePath}: {ex.Message}");
        }
    }

    private static AssetMeta CreateAssetMeta(
        ShaderImportContext context,
        Schema.Shader asset,
        string fingerprint,
        DependencyEntryData[] dependencies)
    {
        return new AssetMeta
        {
            AssetGuid = AssetGuid.Parse(asset.AssetGuid ?? AssetGuid.Empty.ToFlatString()),
            SourceGuid = context.SourceMeta.SourceGuid,
            SubAssetKey = context.SubAssetKey,
            ContentFingerprint = fingerprint,
            Dependencies = dependencies,
            ImporterVersion = ImporterVersion,
            AssetPath = context.CachePath,
        };
    }

    private static string BackendName(TargetDesc target)
        => target.Format == SlangCompileTarget.Dxil ? "dxil" : "spirv";
}

public static partial class SlangShaderImporter
{


    private readonly record struct ShaderImportContext(
        string FilePath,
        string CachePath,
        string ProjectRoot,
        string SubAssetKey,
        AssetGuid AssetGuid,
        SourceMeta SourceMeta,
        AssetMeta? ExistingAsset,
        SlangShaderCookProfile Profile,
        string? Source,
        bool WriteCache);

    private sealed record ShaderImportState(
        ShaderImportContext Context,
        TargetDesc[] Targets,
        SlangImportLifetime Lifetime,
        ISession Session,
        IModule Module,
        DependencyEntryData[] Dependencies,
        string Fingerprint,
        ShaderMetadata Metadata,
        Schema.Shader Asset,
        Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>> BackendResourceMaps,
        List<string> MaterialScalarTypes);

    private readonly record struct LinkedEntryPoint(
        IEntryPoint EntryPoint,
        IComponentType LinkedProgram,
        IReadOnlyList<SlangEntryMeta.Attr> Attributes);

    private static SlangShaderCookProfile ResolveProfile(SourceMeta sourceMeta, string filePath)
    {
        SlangShaderImporterSettings settings = SlangShaderImporterSettings.Load(
            sourceMeta,
            Path.GetFullPath(filePath));
        return SlangShaderCookProfiles.Resolve(settings.CookProfile);
    }

    private static Schema.ShaderStage MapStage(SlangStage stage)
    {
        switch (stage)
        {
            case SlangStage.None:
                Console.WriteLine(
                    "Warning: Slang reported ShaderStage.None. Falling back to Vertex."
                );
                return Schema.ShaderStage.Vertex;
            case SlangStage.Vertex:
                return Schema.ShaderStage.Vertex;
            case SlangStage.Fragment:
                return Schema.ShaderStage.Pixel;
            case SlangStage.Compute:
                return Schema.ShaderStage.Compute;
            case SlangStage.Hull:
                return Schema.ShaderStage.Hull;
            case SlangStage.Domain:
                return Schema.ShaderStage.Domain;
            case SlangStage.Geometry:
                return Schema.ShaderStage.Geometry;
            case SlangStage.Amplification:
                return Schema.ShaderStage.Amplification;
            case SlangStage.Mesh:
                return Schema.ShaderStage.Mesh;
            case SlangStage.RayGeneration:
                return Schema.ShaderStage.RayGen;
            case SlangStage.Miss:
                return Schema.ShaderStage.RayMiss;
            case SlangStage.ClosestHit:
                return Schema.ShaderStage.RayClosestHit;
            case SlangStage.AnyHit:
                return Schema.ShaderStage.RayAnyHit;
            case SlangStage.Intersection:
                return Schema.ShaderStage.RayIntersection;
            case SlangStage.Callable:
                return Schema.ShaderStage.Callable;
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

    private static byte[] GetBytes(ISlangBlob blob)
    {
        unsafe
        {
            var span = new ReadOnlySpan<byte>(blob.GetBufferPointer(), (int)blob.GetBufferSize());
            return span.ToArray();
        }
    }

    /// <summary>
    /// Strip OpName (5) and OpMemberName (6) instructions from SPIR-V bytecode.
    /// These instructions encode debug variable/member names but don't affect
    /// execution semantics. Removing them ensures identical logic with different
    /// variable names produces the same content hash.
    /// SPIR-V binary format: header (5 words) + instructions.
    /// Each instruction: word0 = (wordCount &lt;&lt; 16) | opcode, followed by (wordCount-1) words.
    /// </summary>
    private static byte[] ComputeSpirvSemanticHash(ReadOnlySpan<byte> spirv)
    {
        if (spirv.Length < 20)
            return SHA256.HashData(spirv);

        const ushort OpName = 5;
        const ushort OpMemberName = 6;
        const int HeaderWords = 5;

        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
        // Validate SPIR-V magic number
        if (words[0] != 0x07230203)
            return SHA256.HashData(spirv);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(spirv[..(HeaderWords * sizeof(uint))]);

        // Process instructions, skip OpName and OpMemberName
        int pos = HeaderWords;
        while (pos < words.Length)
        {
            uint instrWord = words[pos];
            ushort opcode = (ushort)(instrWord & 0xFFFF);
            ushort wordCount = (ushort)(instrWord >> 16);

            if (wordCount == 0)
                break; // Malformed
            if (pos + wordCount > words.Length)
                break;

            if (opcode != OpName && opcode != OpMemberName)
                hash.AppendData(spirv.Slice(pos * sizeof(uint), wordCount * sizeof(uint)));

            pos += wordCount;
        }

        return hash.GetHashAndReset();
    }

}

