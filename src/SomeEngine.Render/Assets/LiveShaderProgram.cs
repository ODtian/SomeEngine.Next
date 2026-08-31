using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Assets;

public enum LiveShaderStage : byte
{
    Vertex,
    Pixel,
    Compute,
}

public readonly record struct LiveShaderEntry(string Name, LiveShaderStage Stage);

/// <summary>
/// Owns one Slang source module and its linked entry-point composition. The linked Slang program
/// is the sole shader and binding-layout authority consumed by the RHI.
/// </summary>
public sealed class LiveShaderProgram : IDisposable
{
    private static readonly Lazy<IGlobalSession> s_globalSession = new(
        CreateGlobalSession,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly List<ComObject> _objects;
    private readonly EntryPointReflection[] _entryPoints;
    private bool _disposed;

    private LiveShaderProgram(
        List<ComObject> objects,
        IComponentType program,
        ShaderReflection reflection,
        EntryPointReflection[] entryPoints,
        VariableLayoutReflection parameterLayout,
        string sourcePath)
    {
        _objects = objects;
        Program = program;
        Reflection = reflection;
        _entryPoints = entryPoints;
        ParameterLayout = parameterLayout;
        SourcePath = sourcePath;
    }

    public IComponentType Program { get; }

    public ShaderReflection Reflection { get; }

    public VariableLayoutReflection ParameterLayout { get; }

    public bool HasParameterBlock => ParameterLayout != VariableLayoutReflection.Null;

    public string SourcePath { get; }

    public int EntryPointCount => _entryPoints.Length;

    public EntryPointReflection GetEntryPoint(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _entryPoints[index];
    }

    public static LiveShaderProgram Link(
        Shader shader,
        ReadOnlySpan<LiveShaderEntry> entries,
        ShaderTarget shaderTarget = ShaderTarget.Dxil)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (entries.IsEmpty)
            throw new ArgumentException("At least one shader entry point is required.", nameof(entries));

        string sourcePath = ResolveSourcePath(shader);
        string sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidDataException($"Shader source '{sourcePath}' has no directory.");
        string contentRoot = ResolveContentRoot(sourcePath);
        string libraryDirectory = Path.Combine(contentRoot, "assets", "Shaders");
        string[] searchPaths = string.Equals(
            sourceDirectory,
            libraryDirectory,
            StringComparison.OrdinalIgnoreCase)
                ? [sourceDirectory]
                : [sourceDirectory, libraryDirectory];

        var owned = new List<ComObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            IGlobalSession globalSession = s_globalSession.Value;
            SlangCompileTarget target = shaderTarget == ShaderTarget.Spirv
                ? SlangCompileTarget.Spirv
                : SlangCompileTarget.Dxil;
            SlangProfileID profile = globalSession.FindProfile(
                shaderTarget == ShaderTarget.Spirv ? "glsl_460" : "sm_6_6");
            var sessionDescription = new SessionDesc
            {
                Targets =
                [
                    new TargetDesc
                    {
                        Format = target,
                        Profile = profile,
                        Flags = shaderTarget == ShaderTarget.Spirv
                            ? SlangTargetFlags.GenerateSpirvDirectly
                            : 0,
                        CompilerOptionEntries = shaderTarget == ShaderTarget.Spirv
                            ?
                            [
                                new(
                                    CompilerOptionName.VulkanUseEntryPointName,
                                    CompilerOptionValue.FromInt(1)),
                            ]
                            : [],
                    },
                ],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
                SearchPaths = searchPaths,
                CompilerOptionEntries =
                [
                    new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                    new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
                ],
            };
            SlangResult sessionResult = globalSession.CreateSession(
                sessionDescription,
                out ISession session);
            RequireSuccess(sessionResult, "Slang session creation", null);
            TrackOwned(session);

            ISlangBlob source = TrackOwned(Slang.CreateBlob(File.ReadAllBytes(sourcePath)));
            IModule? module = session.LoadModuleFromSource(
                Path.GetFileNameWithoutExtension(sourcePath),
                sourcePath,
                source,
                out ISlangBlob? moduleDiagnostics);
            TrackOptional(moduleDiagnostics);
            if (module is null)
            {
                throw new InvalidDataException(
                    FormatFailure($"Slang module load failed for '{sourcePath}'", moduleDiagnostics));
            }
            TrackOwned(module);

            IComponentType[] components = new IComponentType[checked(entries.Length + 1)];
            components[0] = module;
            for (int index = 0; index < entries.Length; index++)
            {
                LiveShaderEntry request = entries[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
                SlangResult entryResult = module.FindEntryPointByName(
                    request.Name,
                    out IEntryPoint entryPoint);
                RequireSuccess(
                    entryResult,
                    $"Slang entry-point lookup '{request.Name}'",
                    null);
                TrackOwned(entryPoint);
                components[index + 1] = entryPoint;
            }

            SlangResult composeResult = session.CreateCompositeComponentType(
                components,
                out IComponentType? composite,
                out ISlangBlob? composeDiagnostics);
            TrackOptional(composeDiagnostics);
            if (!composeResult.Succeeded || composite is null)
            {
                throw new InvalidDataException(FormatFailure(
                    "Slang program composition failed",
                    composeDiagnostics));
            }
            TrackOwned(composite);

            SlangResult linkResult = composite.Link(
                out IComponentType? linked,
                out ISlangBlob? linkDiagnostics);
            TrackOptional(linkDiagnostics);
            if (!linkResult.Succeeded || linked is null)
            {
                throw new InvalidDataException(FormatFailure(
                    "Slang program link failed",
                    linkDiagnostics));
            }
            TrackOwned(linked);

            ShaderReflection reflection = linked.GetLayout(
                0,
                out ISlangBlob? layoutDiagnostics);
            TrackOptional(layoutDiagnostics);
            if (reflection == ShaderReflection.Null)
            {
                throw new InvalidDataException(FormatFailure(
                    "Slang linked-program reflection failed",
                    layoutDiagnostics));
            }
            if (reflection.EntryPointCount != checked((nuint)entries.Length))
            {
                throw new InvalidDataException(
                    $"Slang linked program exposes {reflection.EntryPointCount} entry points; " +
                    $"{entries.Length} were requested.");
            }

            var entryReflections = new EntryPointReflection[entries.Length];
            for (int index = 0; index < entryReflections.Length; index++)
            {
                EntryPointReflection entry = reflection.GetEntryPointByIndex(checked((uint)index));
                LiveShaderEntry request = entries[index];
                if (entry == EntryPointReflection.Null ||
                    !string.Equals(entry.Name, request.Name, StringComparison.Ordinal) ||
                    entry.Stage != MapStage(request.Stage))
                {
                    throw new InvalidDataException(
                        $"Slang reflection for entry {index} does not match " +
                        $"'{request.Name}' ({request.Stage}).");
                }
                entryReflections[index] = entry;
            }

            VariableLayoutReflection parameterLayout =
                reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            return new LiveShaderProgram(
                owned,
                linked,
                reflection,
                entryReflections,
                parameterLayout,
                sourcePath);
        }
        catch
        {
            Release(owned);
            throw;
        }

        T TrackOwned<T>(T value)
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
        Release(_objects);
        _disposed = true;
    }

    private static IGlobalSession CreateGlobalSession()
    {
        SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session);
        RequireSuccess(result, "Slang global-session creation", null);
        return session;
    }

    private static string ResolveSourcePath(Shader shader)
    {
        string expectedName = $"{shader.Name}.slang";
        DependencyEntry[] matches = (shader.ImportTrace?.Dependencies ?? [])
            .Where(dependency =>
                !string.IsNullOrWhiteSpace(dependency.Path) &&
                string.Equals(
                    Path.GetFileName(dependency.Path),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Shader asset '{shader.Name}' must identify exactly one source dependency " +
                $"named '{expectedName}'.");
        }

        string relative = matches[0].Path!.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Shader source dependency '{matches[0].Path}' is not a contained publication path.");
        }

        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (DirectoryInfo? directory = new(Path.GetFullPath(start));
                 directory is not null;
                 directory = directory.Parent)
            {
                string candidate = Path.GetFullPath(Path.Combine(directory.FullName, relative));
                string contained = Path.GetRelativePath(directory.FullName, candidate);
                if (!Path.IsPathRooted(contained) &&
                    contained != ".." &&
                    !contained.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"Published Slang source '{matches[0].Path}' for shader '{shader.Name}' was not found.");
    }

    private static string ResolveContentRoot(string sourcePath)
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(sourcePath)!);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            $"Shader source '{sourcePath}' is not contained by an assets/Shaders publication.");
    }

    private static SlangStage MapStage(LiveShaderStage stage) => stage switch
    {
        LiveShaderStage.Vertex => SlangStage.Vertex,
        LiveShaderStage.Pixel => SlangStage.Fragment,
        LiveShaderStage.Compute => SlangStage.Compute,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

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
        List<Exception>? failures = null;
        for (int index = objects.Count - 1; index >= 0; index--)
        {
            try
            {
                objects[index].FinalRelease();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        objects.Clear();
        if (failures is not null)
        {
            throw new AggregateException(
                "One or more Slang program objects failed to release.",
                failures);
        }
    }
}
