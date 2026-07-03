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
    public const uint ImporterVersion = 15;

    [ThreadStatic]
    private static IGlobalSession? t_globalSession;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (Schema.ShaderAsset Asset, DateTime LastModified)
    > _cache = new();

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

    public static Schema.ShaderAsset Import(string filePath, string? source = null)
    {
        SourceMeta sourceMeta = SourceMetaFiles.GetOrCreate(filePath);
        AssetMeta? existingAsset = AssetMetaFiles.TryLoad(
            Path.ChangeExtension(Path.GetFullPath(filePath), ".shader.asset")
        );
        return Import(filePath, sourceMeta, existingAsset, source);
    }

    public static Schema.ShaderAsset ImportTransient(string filePath)
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
        return Import(filePath, sourceMeta, existingAsset, source: null, writeCache: false);
    }

    public static Schema.ShaderAsset Import(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
        string? source = null,
        bool writeCache = true
    )
    {
        ShaderImportContext context = CreateImportContext(
            filePath,
            sourceMeta,
            existingAsset,
            source,
            writeCache);
        if (TryReadCachedAsset(context, out Schema.ShaderAsset? cachedAsset))
        {
            return cachedAsset!;
        }

        string importSource = context.Source ?? File.ReadAllText(context.FilePath);
        ShaderImportState state = CreateImportState(context, importSource);
        CollectModuleMaterialMetadata(state, importSource);
        CollectEntryPoints(state);
        AddBackendReflections(state.Asset, state.BackendResourceMaps);
        SaveShaderCache(context, state.Asset, state.Fingerprint, state.Dependencies);
        return state.Asset;
    }

    private static ShaderImportContext CreateImportContext(
        string filePath,
        SourceMeta sourceMeta,
        AssetMeta? existingAsset,
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
            source,
            writeCache);
    }

    private static bool TryReadCachedAsset(
        ShaderImportContext context,
        out Schema.ShaderAsset? cachedAsset)
    {
        cachedAsset = null;
        if (context.ExistingAsset == null || !File.Exists(context.CachePath))
        {
            return false;
        }

        AssetImportFingerprint? historicalFingerprint = SlangDeps.Refresh(
            context.ExistingAsset.Dependencies,
            context.ProjectRoot,
            ImporterVersion);
        if (historicalFingerprint?.ContentFingerprint != context.ExistingAsset.ContentFingerprint)
        {
            return false;
        }

        if (_cache.TryGetValue(context.FilePath, out var memoryCached)
            && SlangDeps.Matches(memoryCached.Asset, context.ExistingAsset))
        {
            cachedAsset = memoryCached.Asset;
            return true;
        }

        Schema.ShaderAsset diskAsset = ShaderAssetCodec.Load(context.CachePath);
        if (!SlangDeps.Matches(diskAsset, context.ExistingAsset))
        {
            return false;
        }

        _cache[context.FilePath] = (diskAsset, CacheSourceTime(context));
        cachedAsset = diskAsset;
        return true;
    }

    private static DateTime CacheSourceTime(ShaderImportContext context)
        => File.Exists(context.FilePath)
            ? File.GetLastWriteTime(context.FilePath)
            : File.GetLastWriteTime(context.CachePath);

    private static ShaderImportState CreateImportState(
        ShaderImportContext context,
        string source)
    {
        string name = Path.GetFileNameWithoutExtension(context.FilePath);
        IGlobalSession globalSession = GlobalSession;
        TargetDesc[] targets = CreateTargets(globalSession);
        SessionDesc sessionDesc = CreateSessionDesc(context.FilePath, targets);
        globalSession.CreateSession(sessionDesc, out ISession session);

        ISlangBlob sourceBlob = Slang.CreateBlob(Encoding.UTF8.GetBytes(source));
        IModule? module = session.LoadModuleFromSource(name, context.FilePath, sourceBlob, out var diagnostics);
        if (module == null)
        {
            throw new Exception($"Failed to load module {name}: {GetString(diagnostics)}");
        }

        DependencyEntryData[] dependencies = SlangDeps.Collect(module, context.FilePath, context.ProjectRoot);
        string fingerprint = SlangDeps.Fingerprint(dependencies, ImporterVersion);
        ShaderMetadata metadata = CreateMetadata();
        Schema.ShaderAsset asset = CreateShaderAsset(context, name, fingerprint, dependencies, metadata);
        return new ShaderImportState(
            context,
            targets,
            session,
            module,
            dependencies,
            fingerprint,
            metadata,
            asset,
            CreateBackendResourceMaps(targets),
            []);
    }

    private static TargetDesc[] CreateTargets(IGlobalSession globalSession)
    {
        SlangProfileID dxilProfile = globalSession.FindProfile("sm_6_5");
        SlangProfileID spirvProfile = globalSession.FindProfile("glsl_460");
        return
        [
            new() { Format = SlangCompileTarget.Dxil, Profile = dxilProfile },
            new() { Format = SlangCompileTarget.Spirv, Profile = spirvProfile },
        ];
    }

    private static SessionDesc CreateSessionDesc(string filePath, TargetDesc[] targets)
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
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.ColumnMajor,
            SearchPaths = [Path.GetDirectoryName(filePath) ?? ""],
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
        };
    }

    private static Schema.ShaderAsset CreateShaderAsset(
        ShaderImportContext context,
        string name,
        string fingerprint,
        DependencyEntryData[] dependencies,
        ShaderMetadata metadata)
    {
        return new Schema.ShaderAsset
        {
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

            AddEntryPointReflections(state, linkedEntryPoint);
            AddMaterialScalarLayouts(state, linkedEntryPoint.LinkedProgram);
            AddCompiledVariants(state, linkedEntryPoint);
        }
    }

    private static bool TryCreateLinkedEntryPoint(
        ShaderImportState state,
        int index,
        out LinkedEntryPoint linkedEntryPoint)
    {
        linkedEntryPoint = default;
        state.Module.GetDefinedEntryPoint(index, out IEntryPoint entryPoint);
        List<SlangEntryMeta.Attr> attributes = SlangEntryMeta.Read(entryPoint.GetFunctionReflection());
        state.Session.CreateCompositeComponentType(
            [state.Module, entryPoint],
            out IComponentType? composedProgram,
            out ISlangBlob? diagnostics);
        if (composedProgram == null)
        {
            Console.WriteLine($"Warning: Failed to compose entry point {index}: {GetString(diagnostics)}");
            return false;
        }

        composedProgram.Link(out IComponentType? linkedProgram, out ISlangBlob? linkDiagnostics);
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
            ShaderReflection reflection = linkedEntryPoint.LinkedProgram.GetLayout((nint)targetIndex, out _);
            if (reflection == ShaderReflection.Null)
            {
                continue;
            }

            AddEntryPointReflection(
                state,
                BackendName(state.Targets[targetIndex]),
                reflection);
        }
    }

    private static void AddEntryPointReflection(
        ShaderImportState state,
        string backendName,
        ShaderReflection reflection)
    {
        EntryPointReflection entryReflection = reflection.GetEntryPointByIndex(0);
        ShaderStage stage = entryReflection.Stage != SlangStage.None
            ? MapStage(entryReflection.Stage)
            : ShaderStage.Vertex;
        var entryResources = new Dictionary<SlangBindingMap.ResourceKey, uint>();
        HashSet<string> materialResourceNames = EntryPointMaterialResources(entryReflection, state.Metadata);
        CollectGlobalResources(reflection, stage, entryResources, backendName, materialResourceNames);
        CollectEntryPointResources(entryReflection, stage, entryResources, backendName);
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
        ShaderReflection baseReflection = linkedProgram.GetLayout(0, out _);
        if (baseReflection == ShaderReflection.Null)
        {
            baseReflection = linkedProgram.GetLayout(1, out _);
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
        ShaderReflection baseReflection = linkedEntryPoint.LinkedProgram.GetLayout(0, out _);
        if (baseReflection == ShaderReflection.Null)
        {
            baseReflection = linkedEntryPoint.LinkedProgram.GetLayout(1, out _);
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
        byte[] bytesToHash = target.Format == SlangCompileTarget.Spirv
            ? StripSpirvNames(rawBytes)
            : rawBytes;
        return new ShaderBytecode
        {
            Backend = BackendName(target),
            Stage = MapStage(entryPointReflection.Stage),
            EntryPoint = entryPointReflection.Name,
            Data = rawBytes,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytesToHash)),
        };
    }

    private static void AddEntryPointAttributes(
        Schema.ShaderAsset asset,
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
        Schema.ShaderAsset asset,
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
        Schema.ShaderAsset asset,
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
        Schema.ShaderAsset asset,
        string fingerprint,
        DependencyEntryData[] dependencies)
    {
        if (!context.WriteCache || !File.Exists(context.FilePath))
        {
            return;
        }

        _cache[context.FilePath] = (asset, File.GetLastWriteTime(context.FilePath));
        try
        {
            ShaderAssetCodec.Save(asset, context.CachePath);
            AssetMetaFiles.Save(context.CachePath, CreateAssetMeta(context, asset, fingerprint, dependencies));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to save shader asset cache to {context.CachePath}: {ex.Message}");
        }
    }

    private static AssetMeta CreateAssetMeta(
        ShaderImportContext context,
        Schema.ShaderAsset asset,
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
        string? Source,
        bool WriteCache);

    private sealed record ShaderImportState(
        ShaderImportContext Context,
        TargetDesc[] Targets,
        ISession Session,
        IModule Module,
        DependencyEntryData[] Dependencies,
        string Fingerprint,
        ShaderMetadata Metadata,
        Schema.ShaderAsset Asset,
        Dictionary<string, Dictionary<SlangBindingMap.ResourceKey, uint>> BackendResourceMaps,
        List<string> MaterialScalarTypes);

    private readonly record struct LinkedEntryPoint(
        IEntryPoint EntryPoint,
        IComponentType LinkedProgram,
        IReadOnlyList<SlangEntryMeta.Attr> Attributes);

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
    private static byte[] StripSpirvNames(byte[] spirv)
    {
        if (spirv.Length < 20)
            return spirv; // Too small for valid SPIR-V

        const ushort OpName = 5;
        const ushort OpMemberName = 6;
        const int HeaderWords = 5;

        var words = MemoryMarshal.Cast<byte, uint>(spirv.AsSpan());
        // Validate SPIR-V magic number
        if (words[0] != 0x07230203)
            return spirv;

        using var ms = new MemoryStream(spirv.Length);
        using var bw = new BinaryWriter(ms);

        // Copy header (5 words = 20 bytes)
        for (int i = 0; i < HeaderWords && i < words.Length; i++)
            bw.Write(words[i]);

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
            {
                for (int w = 0; w < wordCount; w++)
                    bw.Write(words[pos + w]);
            }

            pos += wordCount;
        }

        return ms.ToArray();
    }

}

