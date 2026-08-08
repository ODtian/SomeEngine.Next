using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal readonly record struct D3D12TestShaderEntry(string Name, SlangStage Stage);

internal sealed class D3D12TestShaderProgram : IDisposable
{
    private static readonly Lazy<IGlobalSession> s_globalSession = new(
        CreateGlobalSession,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly List<ComObject> _owned;
    private readonly EntryPointReflection[] _entries;
    private bool _disposed;

    private D3D12TestShaderProgram(
        List<ComObject> owned,
        IComponentType program,
        ShaderReflection reflection,
        EntryPointReflection[] entries)
    {
        _owned = owned;
        Program = program;
        Reflection = reflection;
        _entries = entries;
    }

    internal IComponentType Program { get; }
    internal ShaderReflection Reflection { get; }

    internal EntryPointReflection GetEntryPoint(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _entries[index];
    }

    internal static D3D12TestShaderProgram Compile(
        string moduleName,
        string source,
        ReadOnlySpan<D3D12TestShaderEntry> entries) =>
        CompileModule(moduleName, source, entries, ".slang");

    private static D3D12TestShaderProgram CompileModule(
        string moduleName,
        string source,
        ReadOnlySpan<D3D12TestShaderEntry> entries,
        string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (entries.IsEmpty)
            throw new ArgumentException("At least one test shader entry is required.", nameof(entries));

        var owned = new List<ComObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            IGlobalSession global = s_globalSession.Value;
            SlangProfileID profile = global.FindProfile("sm_6_8");
            SessionDesc description = new()
            {
                Targets =
                [
                    new TargetDesc
                    {
                        Format = SlangCompileTarget.Dxil,
                        Profile = profile,
                    },
                ],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
                CompilerOptionEntries =
                [
                    new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                ],
            };
            RequireSuccess(
                global.CreateSession(description, out ISession session),
                "Slang test-session creation",
                null);
            Track(session);

            ISlangBlob sourceBlob = Track(Slang.CreateBlob(source));
            string virtualPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                $"{moduleName}{extension}"));
            IModule? module = session.LoadModuleFromSource(
                moduleName,
                virtualPath,
                sourceBlob,
                out ISlangBlob? moduleDiagnostics);
            TrackOptional(moduleDiagnostics);
            if (module is null)
            {
                throw new InvalidDataException(
                    FormatFailure("Slang test-module load failed", moduleDiagnostics));
            }
            Track(module);

            IComponentType[] components = new IComponentType[checked(entries.Length + 1)];
            components[0] = module;
            for (int index = 0; index < entries.Length; index++)
            {
                D3D12TestShaderEntry request = entries[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
                RequireSuccess(
                    module.FindEntryPointByName(request.Name, out IEntryPoint entryPoint),
                    $"Slang test entry-point lookup '{request.Name}'",
                    null);
                Track(entryPoint);
                components[index + 1] = entryPoint;
            }

            SlangResult compose = session.CreateCompositeComponentType(
                components,
                out IComponentType? composite,
                out ISlangBlob? composeDiagnostics);
            TrackOptional(composeDiagnostics);
            if (!compose.Succeeded || composite is null)
            {
                throw new InvalidDataException(
                    FormatFailure("Slang test-program composition failed", composeDiagnostics));
            }
            Track(composite);

            SlangResult link = composite.Link(
                out IComponentType? linked,
                out ISlangBlob? linkDiagnostics);
            TrackOptional(linkDiagnostics);
            if (!link.Succeeded || linked is null)
            {
                throw new InvalidDataException(
                    FormatFailure("Slang test-program link failed", linkDiagnostics));
            }
            Track(linked);

            ShaderReflection reflection = linked.GetLayout(
                0,
                out ISlangBlob? layoutDiagnostics);
            TrackOptional(layoutDiagnostics);
            if (reflection == ShaderReflection.Null)
            {
                throw new InvalidDataException(
                    FormatFailure("Slang test-program reflection failed", layoutDiagnostics));
            }
            if (reflection.EntryPointCount != entries.Length)
            {
                throw new InvalidDataException(
                    $"The linked test program reports {reflection.EntryPointCount} entry points; " +
                    $"{entries.Length} were requested.");
            }

            var reflectedEntries = new EntryPointReflection[entries.Length];
            for (int index = 0; index < reflectedEntries.Length; index++)
            {
                EntryPointReflection reflected = reflection.GetEntryPointByIndex(checked((uint)index));
                D3D12TestShaderEntry requested = entries[index];
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

            return new D3D12TestShaderProgram(owned, linked, reflection, reflectedEntries);
        }
        catch
        {
            Release(owned);
            throw;
        }

        T Track<T>(T value)
            where T : class
        {
            if (value is ComObject wrapper && seen.Add(wrapper))
                owned.Add(wrapper);
            return value;
        }

        void TrackOptional(object? value)
        {
            if (value is ComObject wrapper && seen.Add(wrapper))
                owned.Add(wrapper);
        }
    }

    internal static D3D12TestShaderProgram CompileHlslPassThrough(
        string moduleName,
        string source,
        ReadOnlySpan<D3D12TestShaderEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (entries.IsEmpty)
            throw new ArgumentException("At least one test shader entry is required.", nameof(entries));

        var owned = new List<ComObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            IGlobalSession global = s_globalSession.Value;
            SlangProfileID profile = global.FindProfile("sm_6_8");
            SessionDesc description = new()
            {
                Targets =
                [
                    new TargetDesc
                    {
                        Format = SlangCompileTarget.Dxil,
                        Profile = profile,
                    },
                ],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            };
            RequireSuccess(
                global.CreateSession(description, out ISession session),
                "Slang pass-through session creation",
                null);
            Track(session);
            RequireSuccess(
                session.CreateCompileRequest(out ICompileRequest request),
                "Slang pass-through request creation",
                null);
            Track(request);
            request.SetCodeGenTarget(SlangCompileTarget.Dxil);
            request.SetTargetProfile(0, profile);
            request.SetMatrixLayoutMode(SlangMatrixLayoutMode.RowMajor);
            request.SetPassThrough(SlangPassThrough.Dxc);
            int translationUnit = request.AddTranslationUnit(
                SlangSourceLanguage.Hlsl,
                moduleName);
            string virtualPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                $"{moduleName}.hlsl"));
            request.AddTranslationUnitSourceString(translationUnit, virtualPath, source);
            for (int index = 0; index < entries.Length; index++)
            {
                D3D12TestShaderEntry entry = entries[index];
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
            Track(program);
            RequireSuccess(
                request.GetEntryPointCodeBlob(0, 0, out ISlangBlob targetCode),
                "Slang pass-through entry-point code retrieval",
                null);
            Track(targetCode);
            IComponentType targetProgram = new TargetCodeComponent(
                program,
                targetCode.Buffer.ToArray());
            ShaderReflection reflection = program.GetLayout(
                0,
                out ISlangBlob? layoutDiagnostics);
            TrackOptional(layoutDiagnostics);
            if (reflection == ShaderReflection.Null)
            {
                throw new InvalidDataException(
                    FormatFailure("Slang pass-through reflection failed", layoutDiagnostics));
            }
            if (reflection.EntryPointCount != entries.Length)
            {
                throw new InvalidDataException(
                    $"The pass-through program reports {reflection.EntryPointCount} entry points; " +
                    $"{entries.Length} were requested.");
            }

            var reflectedEntries = new EntryPointReflection[entries.Length];
            for (int index = 0; index < reflectedEntries.Length; index++)
            {
                EntryPointReflection reflected = reflection.GetEntryPointByIndex(checked((uint)index));
                D3D12TestShaderEntry requested = entries[index];
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
            return new D3D12TestShaderProgram(owned, targetProgram, reflection, reflectedEntries);
        }
        catch
        {
            Release(owned);
            throw;
        }

        T Track<T>(T value)
            where T : class
        {
            if (value is ComObject wrapper && seen.Add(wrapper))
                owned.Add(wrapper);
            return value;
        }

        void TrackOptional(object? value)
        {
            if (value is ComObject wrapper && seen.Add(wrapper))
                owned.Add(wrapper);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Release(_owned);
        _disposed = true;
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

    private static void Release(List<ComObject> objects)
    {
        for (int index = objects.Count - 1; index >= 0; index--)
            objects[index].FinalRelease();
        objects.Clear();
    }

    private sealed class TargetCodeComponent(
        IComponentType inner,
        byte[] targetCode) : IComponentType
    {
        public ISession GetSession() => inner.GetSession();

        public ShaderReflection GetLayout(nint targetIndex, out ISlangBlob? diagnostics) =>
            inner.GetLayout(targetIndex, out diagnostics);

        public nint GetSpecializationParamCount() => inner.GetSpecializationParamCount();

        public SlangResult GetEntryPointCode(
            nint entryPointIndex,
            nint targetIndex,
            out ISlangBlob code,
            out ISlangBlob? diagnostics) =>
            inner.GetEntryPointCode(entryPointIndex, targetIndex, out code, out diagnostics);

        public SlangResult GetResultAsFileSystem(
            nint entryPointIndex,
            nint targetIndex,
            out ISlangMutableFileSystem fileSystem) =>
            inner.GetResultAsFileSystem(entryPointIndex, targetIndex, out fileSystem);

        public void GetEntryPointHash(
            nint entryPointIndex,
            nint targetIndex,
            out ISlangBlob hash) =>
            inner.GetEntryPointHash(entryPointIndex, targetIndex, out hash);

        public SlangResult Specialize(
            SpecializationArg[] specializationArgs,
            nint specializationArgCount,
            out IComponentType specializedComponentType,
            out ISlangBlob? diagnostics) =>
            inner.Specialize(
                specializationArgs,
                specializationArgCount,
                out specializedComponentType,
                out diagnostics);

        public SlangResult Link(
            out IComponentType linkedComponentType,
            out ISlangBlob? diagnostics) =>
            inner.Link(out linkedComponentType, out diagnostics);

        public SlangResult GetEntryPointHostCallable(
            int entryPointIndex,
            int targetIndex,
            out ISlangSharedLibrary sharedLibrary,
            out ISlangBlob? diagnostics) =>
            inner.GetEntryPointHostCallable(
                entryPointIndex,
                targetIndex,
                out sharedLibrary,
                out diagnostics);

        public SlangResult RenameEntryPoint(
            string newName,
            out IComponentType entryPoint) =>
            inner.RenameEntryPoint(newName, out entryPoint);

        public SlangResult LinkWithOptions(
            out IComponentType linkedComponentType,
            uint compilerOptionEntryCount,
            CompilerOptionEntry[] compilerOptionEntries,
            out ISlangBlob? diagnostics) =>
            inner.LinkWithOptions(
                out linkedComponentType,
                compilerOptionEntryCount,
                compilerOptionEntries,
                out diagnostics);

        public SlangResult GetTargetCode(
            nint targetIndex,
            out ISlangBlob code,
            out ISlangBlob? diagnostics)
        {
            if (targetIndex != 0)
                return inner.GetTargetCode(targetIndex, out code, out diagnostics);
            code = Slang.CreateBlob(targetCode);
            diagnostics = null;
            return SlangResult.SLANG_OK;
        }

        public SlangResult GetTargetMetadata(
            nint targetIndex,
            out Metadata? metadata,
            out ISlangBlob? diagnostics) =>
            inner.GetTargetMetadata(targetIndex, out metadata, out diagnostics);

        public SlangResult GetEntryPointMetadata(
            nint entryPointIndex,
            nint targetIndex,
            out Metadata? metadata,
            out ISlangBlob? diagnostics) =>
            inner.GetEntryPointMetadata(
                entryPointIndex,
                targetIndex,
                out metadata,
                out diagnostics);
    }
}
