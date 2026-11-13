using System.Collections.Concurrent;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public const uint ImporterVersion = 36;

    private static readonly Lazy<IGlobalSession> s_globalSession = new(Shader.CreateSlangGlobalSession);
    private static readonly ConcurrentBag<IGlobalSession> s_compilers = [];

    public static IGlobalSession GlobalSession => s_globalSession.Value;

    private static IGlobalSession TakeCompiler() =>
        s_compilers.TryTake(out IGlobalSession? compiler) ? compiler : Shader.CreateSlangGlobalSession();

    public static TargetDesc[] CreateTargets(IGlobalSession slang, IReadOnlyList<SlangCompileTarget> targets)
        => targets.Select(target => new TargetDesc
        {
            Format = target,
            Profile = slang.FindProfile(target switch
            {
                SlangCompileTarget.Dxil => "sm_6_6",
                SlangCompileTarget.Spirv => "glsl_460",
                _ => throw new NotSupportedException($"Shader target {target} has no engine profile."),
            }),
        }).ToArray();

    public static SessionDesc CreateSessionDescription(
        string sourcePath,
        TargetDesc[] targets,
        IReadOnlyList<string>? includeDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(targets);
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        string[] searchPaths =
            [
                directory,
                .. (includeDirectories ?? [])
                    .Select(path => Path.GetFullPath(Path.Combine(directory, path))),
            ];
        CompilerOptionEntry[] options =
        [
            new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0, 0)),
            new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
        ];
        return new SessionDesc
        {
            Targets = targets,
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            SearchPaths = searchPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CompilerOptionEntries = options,
        };
    }

    private static ImportTrace CreateImportTrace(
        string sourcePath,
        string projectRoot,
        string contentHash,
        FileDependency[] dependencies) =>
        new()
        {
            SourcePath = sourcePath,
            ContentFingerprint = contentHash,
            Dependencies = dependencies
                .Select(static dependency => new DependencyEntry
                {
                    Path = dependency.RelativePath,
                    ContentHash = dependency.ContentHash,
                })
                .ToImmutableList(),
            ImporterVersion = ImporterVersion,
        };

    private static string DiagnosticText(ISlangBlob? diagnostics) =>
        diagnostics is null || diagnostics.GetBufferSize() == 0
            ? "no diagnostics"
            : Encoding.UTF8.GetString(diagnostics.Buffer);

}
