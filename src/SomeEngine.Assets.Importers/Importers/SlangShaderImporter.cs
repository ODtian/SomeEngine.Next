using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public const uint ImporterVersion = 33;

    private static readonly Lazy<IGlobalSession> s_globalSession = new(CreateGlobalSession);

    public static IGlobalSession GlobalSession => s_globalSession.Value;

    private static IGlobalSession CreateGlobalSession()
    {
        SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session);
        if (result.Failed || session is null)
            throw new InvalidOperationException($"Slang global session creation failed with {result}.");
        return session;
    }

    public static TargetDesc[] CreateDefaultTargets(IGlobalSession slang)
    {
        ArgumentNullException.ThrowIfNull(slang);
        return
        [
            new TargetDesc
            {
                Format = SlangCompileTarget.Dxil,
                Profile = slang.FindProfile("sm_6_6"),
            },
            new TargetDesc
            {
                Format = SlangCompileTarget.Spirv,
                // ShaderLayer is core in SPIR-V 1.5; the Vulkan backend requires Vulkan 1.3.
                Profile = slang.FindProfile("spirv_1_5"),
            },
        ];
    }

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
            new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
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
