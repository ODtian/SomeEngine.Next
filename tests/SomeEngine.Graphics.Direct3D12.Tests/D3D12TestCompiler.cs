using System.Buffers;
using System.Security.Cryptography;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12.Tests;

// Slang's native global session outlives all sessions created by this collection.
// Each compilation creates its own session, source and component data.
public sealed class D3D12TestCompiler : IDisposable
{
    private readonly IGlobalSession _globalSession = CreateGlobalSession();

    public void Dispose()
    {
        // Release the collection's COM compilation data while its native parent remains alive.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.KeepAlive(_globalSession);
    }

    private const string SlangStandardModuleDirectory =
        "slang-standard-module-2026.17.1";
    private const string SlangWorkGraphSourceSha256 =
        "5AC051E0BBB9E78CAD3D7D368AA76045ADEFE417133182E4B9A783ED711345D7";
    private const string SlangWorkGraphModuleSha256 =
        "D254CEE09A8E31D4327DD2193E7FFBDA71EFC79574B52A6192B00D0AE097CFC9";
    private const string SlangCompilerSha256 =
        "A3A46665A22B63412396CC6327B39D920AEED5B55F0FF473FF21739173EF14A9";
    private const string SlangGlslangSha256 =
        "0D739D7246BF970A4E0F04FA7AC467354FE508B03A1869843910C6BB03EABB36";


    internal D3D12TestShader Compile(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries) =>
        CompileSource(
            sourceName,
            source,
            entries,
            ".slang",
            SlangCompileTarget.Dxil,
            "sm_6_8");

    internal D3D12TestShader Compile(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries,
        string profileName) =>
        CompileSource(
            sourceName,
            source,
            entries,
            ".slang",
            SlangCompileTarget.Dxil,
            profileName);

    internal D3D12TestShader CompileWithSearchPaths(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries,
        string profileName,
        params string[] searchPaths) =>
        CompileSource(
            sourceName,
            source,
            entries,
            ".slang",
            SlangCompileTarget.Dxil,
            profileName,
            searchPaths: searchPaths);

    internal D3D12TestShader CompileExperimental(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries,
        string profileName = "sm_6_8")
    {
        string standardModuleRoot = Path.Combine(
            AppContext.BaseDirectory,
            SlangStandardModuleDirectory);
        RequireExperimentalWorkGraphFixture(
            standardModuleRoot,
            "workgraph.slang",
            SlangWorkGraphSourceSha256);
        RequireExperimentalWorkGraphFixture(
            standardModuleRoot,
            "workgraph.slang-module",
            SlangWorkGraphModuleSha256);
        string nativeRoot = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native");
        RequirePinnedSlangFile(
            Path.Combine(nativeRoot, "slang-compiler.dll"),
            SlangCompilerSha256);
        RequirePinnedSlangFile(
            Path.Combine(nativeRoot, "slang-glslang.dll"),
            SlangGlslangSha256);
        return CompileSource(
            sourceName,
            source,
            entries,
            ".slang",
            SlangCompileTarget.Dxil,
            profileName,
            standardModuleRoot);
    }

    internal D3D12TestShader CompileForReflection(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries,
        SlangCompileTarget target,
        string profileName) =>
        CompileSource(sourceName, source, entries, ".slang", target, profileName);

    internal static SlangTargetFlags TargetFlagsFor(SlangCompileTarget target) =>
        target == SlangCompileTarget.Spirv
            ? SlangTargetFlags.GenerateSpirvDirectly
            : 0;

    private D3D12TestShader CompileSource(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries,
        string extension,
        SlangCompileTarget target,
        string profileName,
        string? experimentalStandardModuleRoot = null,
        string[]? searchPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (entries.IsEmpty)
            throw new ArgumentException("At least one test shader entry is required.", nameof(entries));

        IGlobalSession global = _globalSession;
        SlangProfileID profile = global.FindProfile(profileName);
        CompilerOptionEntry[] options = experimentalStandardModuleRoot is not null
            ?
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                    new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
            ]
            :
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            ];
        SessionDesc description = new()
        {
            Targets =
            [
                new TargetDesc
                    {
                        Format = target,
                        Profile = profile,
                        Flags = TargetFlagsFor(target) |
                            (RequiresTargetCode(entries)
                                ? SlangTargetFlags.GenerateWholeProgram
                                : 0),
                    },
                ],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            SearchPaths = experimentalStandardModuleRoot is not null
                ? [experimentalStandardModuleRoot]
                : searchPaths,
            CompilerOptionEntries = options,
        };
        RequireSuccess(
            global.CreateSession(description, out ISession session),
            "Slang test-session creation",
            null);
        ISlangBlob sourceBlob = Slang.CreateBlob(source);
        string virtualPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            $"{sourceName}{extension}"));
        IModule? module = session.LoadModuleFromSource(
            sourceName,
            virtualPath,
            sourceBlob,
            out ISlangBlob? moduleDiagnostics);
        if (module is null)
        {
            throw new InvalidDataException(
                FormatFailure("Slang test-module load failed", moduleDiagnostics));
        }
        IComponentType[] components = new IComponentType[checked(entries.Length + 1)];
        components[0] = module;
        for (int index = 0; index < entries.Length; index++)
        {
            D3D12TestEntry request = entries[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
            RequireSuccess(
                module.FindEntryPointByName(request.Name, out IEntryPoint entryPoint),
                $"Slang test entry-point lookup '{request.Name}'",
                null);
            IComponentType component = entryPoint;
            if (!string.IsNullOrWhiteSpace(request.NameOverride))
            {
                RequireSuccess(
                    entryPoint.RenameEntryPoint(
                        request.NameOverride,
                        out IComponentType renamedEntryPoint),
                    $"Slang test entry-point rename '{request.Name}' to " +
                    $"'{request.NameOverride}'",
                    null);
                component = renamedEntryPoint;
            }
            components[index + 1] = component;
        }

        SlangResult compose = session.CreateCompositeComponentType(
            components,
            out IComponentType? composite,
            out ISlangBlob? composeDiagnostics);
        if (!compose.Succeeded || composite is null)
        {
            throw new InvalidDataException(
                FormatFailure("Slang test-program composition failed", composeDiagnostics));
        }
        SlangResult link = composite.Link(
            out IComponentType? linked,
            out ISlangBlob? linkDiagnostics);
        if (!link.Succeeded || linked is null)
        {
            throw new InvalidDataException(
                FormatFailure("Slang test-program link failed", linkDiagnostics));
        }
        ShaderReflection reflection = linked.GetLayout(
            0,
            out ISlangBlob? layoutDiagnostics);
        if (reflection == ShaderReflection.Null)
        {
            throw new InvalidDataException(
                FormatFailure("Slang test-program reflection failed", layoutDiagnostics));
        }
        if (reflection.EntryPointCount != checked((nuint)entries.Length))
        {
            throw new InvalidDataException(
                $"The linked test program reports {reflection.EntryPointCount} entry points; " +
                $"{entries.Length} were requested.");
        }

        var reflectedEntries = new EntryPointReflection[entries.Length];
        for (int index = 0; index < reflectedEntries.Length; index++)
        {
            EntryPointReflection reflected = reflection.GetEntryPointByIndex(checked((uint)index));
            D3D12TestEntry requested = entries[index];
            if (reflected == EntryPointReflection.Null ||
                !string.Equals(reflected.Name, requested.Name, StringComparison.Ordinal) ||
                reflected.Stage != requested.Stage)
            {
                throw new InvalidDataException(
                    $"Slang reflection entry {index} does not match " +
                    $"'{requested.Name}' ({requested.Stage}); it reports " +
                    $"'{reflected.Name}' ({reflected.Stage}).");
            }
            reflectedEntries[index] = reflected;
        }

        return CreateTestShader(
            session,
            components,
            composite,
            null,
            linked,
            reflection,
            reflectedEntries);
    }


    private static void RequireExperimentalWorkGraphFixture(
        string standardModuleRoot,
        string fileName,
        string expectedSha256)
    {
        string fixturePath = Path.Combine(
            standardModuleRoot,
            "experimental",
            fileName);
        RequirePinnedSlangFile(fixturePath, expectedSha256);
    }

    internal static void RequirePinnedSlangFile(
        string path,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Pinned Slang file is missing. " +
                $"Expected SHA-256 {expectedSha256}; path '{path}'.",
                path);
        }

        using FileStream stream = File.OpenRead(path);
        string actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pinned Slang file identity mismatch at '{path}'. " +
                $"Expected SHA-256 {expectedSha256}; actual SHA-256 {actualSha256}.");
        }
    }

    internal D3D12TestShader CompileHlslPassThrough(
        string sourceName,
        string source,
        ReadOnlySpan<D3D12TestEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (entries.IsEmpty)
            throw new ArgumentException("At least one test shader entry is required.", nameof(entries));

        IGlobalSession global = _globalSession;
        SlangProfileID profile = global.FindProfile("sm_6_8");
        SessionDesc description = new()
        {
            Targets =
            [
                new TargetDesc
                    {
                        Format = SlangCompileTarget.Dxil,
                        Profile = profile,
                        Flags = 0,
                    },
                ],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
        };
        RequireSuccess(
            global.CreateSession(description, out ISession session),
            "Slang pass-through session creation",
            null);
        RequireSuccess(
            session.CreateCompileRequest(out ICompileRequest request),
            "Slang pass-through request creation",
            null);
        request.SetCodeGenTarget(SlangCompileTarget.Dxil);
        request.SetTargetProfile(0, profile);
        request.SetMatrixLayoutMode(SlangMatrixLayoutMode.RowMajor);
        request.SetPassThrough(SlangPassThrough.Dxc);
        int translationUnit = request.AddTranslationUnit(
            SlangSourceLanguage.Hlsl,
            sourceName);
        string virtualPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            $"{sourceName}.hlsl"));
        request.AddTranslationUnitSourceString(translationUnit, virtualPath, source);
        for (int index = 0; index < entries.Length; index++)
        {
            D3D12TestEntry entry = entries[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Name);
            if (request.AddEntryPoint(translationUnit, entry.Name, entry.Stage) < 0)
            {
                throw new InvalidDataException(
                    $"Slang rejected pass-through entry point '{entry.Name}'.");
            }
        }

        SlangResult compile = request.Compile();
        if (!compile.Succeeded)
        {
            throw new InvalidDataException(
                $"Slang HLSL pass-through compilation failed: " +
                request.GetDiagnosticOutput().Trim());
        }
        RequireSuccess(
            request.GetProgramWithEntryPoints(out IComponentType program),
            "Slang pass-through program retrieval",
            null);
        RequireSuccess(
            request.GetEntryPointCodeBlob(0, 0, out ISlangBlob targetCode),
            "Slang pass-through entry-point code retrieval",
            null);
        ShaderReflection reflection = program.GetLayout(
            0,
            out ISlangBlob? layoutDiagnostics);
        if (reflection == ShaderReflection.Null)
        {
            throw new InvalidDataException(
                FormatFailure("Slang pass-through reflection failed", layoutDiagnostics));
        }
        if (reflection.EntryPointCount != checked((nuint)entries.Length))
        {
            throw new InvalidDataException(
                $"The pass-through program reports {reflection.EntryPointCount} entry points; " +
                $"{entries.Length} were requested.");
        }

        var reflectedEntries = new EntryPointReflection[entries.Length];
        for (int index = 0; index < reflectedEntries.Length; index++)
        {
            EntryPointReflection reflected = reflection.GetEntryPointByIndex(checked((uint)index));
            D3D12TestEntry requested = entries[index];
            if (reflected == EntryPointReflection.Null ||
                !string.Equals(reflected.Name, requested.Name, StringComparison.Ordinal) ||
                reflected.Stage != requested.Stage)
            {
                throw new InvalidDataException(
                    $"Slang pass-through entry {index} does not match " +
                    $"'{requested.Name}' ({requested.Stage}); it reports " +
                    $"'{reflected.Name}' ({reflected.Stage}).");
            }
            reflectedEntries[index] = reflected;
        }
        return CreateTestShader(
            session,
            [],
            program,
            request,
            program,
            reflection,
            reflectedEntries,
            targetCode.Buffer.ToArray());
    }

    private static IGlobalSession CreateGlobalSession()
    {
        SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session);
        RequireSuccess(result, "Slang global-session creation", null);
        return session;
    }

    private static void RequireSuccess(
        SlangResult result,
        string operation,
        ISlangBlob? diagnostics)
    {
        if (!result.Succeeded)
            throw new InvalidDataException(FormatFailure($"{operation} failed", diagnostics));
    }

    private static string FormatFailure(string prefix, ISlangBlob? diagnostics)
    {
        string? detail = diagnostics?.AsString;
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail.Trim()}";
    }

    private D3D12TestShader CreateTestShader(
        ISession session,
        IComponentType[] components,
        IComponentType combinedCode,
        ICompileRequest? compileRequest,
        IComponentType slangCode,
        ShaderReflection reflection,
        EntryPointReflection[] entries,
        byte[]? suppliedTargetCode = null)
    {
        var entryCode = new byte[entries.Length][];
        for (int index = 0; index < entries.Length; index++)
        {
            SlangResult result = slangCode.GetEntryPointCode(
                index,
                0,
                out ISlangBlob code,
                out ISlangBlob? diagnostics);
            if (result.Failed || code is null || code.GetBufferSize() == 0)
            {
                if (index == 0 && suppliedTargetCode is { Length: > 0 })
                {
                    entryCode[index] = suppliedTargetCode;
                    continue;
                }
                throw new InvalidDataException(
                    FormatFailure($"Slang test entry code generation failed at {index}", diagnostics));
            }
            entryCode[index] = code.Buffer.ToArray();
        }

        byte[] targetCode = suppliedTargetCode ?? (RequiresTargetCode(entries)
            ? GetTargetCode()
            : []);
        return new D3D12TestShader(
            _globalSession,
            session,
            components,
            combinedCode,
            compileRequest,
            slangCode,
            reflection,
            entries,
            entryCode,
            targetCode);

        byte[] GetTargetCode()
        {
            SlangResult result = slangCode.GetTargetCode(
                0,
                out ISlangBlob code,
                out ISlangBlob? diagnostics);
            if (result.Failed || code is null || code.GetBufferSize() == 0)
                throw new InvalidDataException(
                    FormatFailure("Slang test target code generation failed", diagnostics));
            return code.Buffer.ToArray();
        }
    }

    private static bool RequiresTargetCode(ReadOnlySpan<EntryPointReflection> entries)
    {
        foreach (EntryPointReflection entry in entries)
        {
            if (RequiresTargetCode(entry.Stage))
                return true;
        }
        return false;
    }

    private static bool RequiresTargetCode(ReadOnlySpan<D3D12TestEntry> entries)
    {
        foreach (ref readonly D3D12TestEntry entry in entries)
        {
            if (RequiresTargetCode(entry.Stage))
                return true;
        }
        return false;
    }

    private static bool RequiresTargetCode(SlangStage stage) => stage is
        SlangStage.RayGeneration or
        SlangStage.Intersection or
        SlangStage.AnyHit or
        SlangStage.ClosestHit or
        SlangStage.Miss or
        SlangStage.Callable or
        SlangStage.Dispatch or
        SlangStage.Node;
}
