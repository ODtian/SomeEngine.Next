using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Benchmarks;

internal readonly record struct BenchmarkShaderEntry(string Name, SlangStage Stage, string FileName);

[method: System.Text.Json.Serialization.JsonConstructor]
internal sealed record ShaderManifest(
    string SourceSha256,
    string Profile,
    string Compiler,
    Dictionary<string, string> EntrySha256);

internal sealed class BenchmarkShaderProgram : IDisposable
{
    private readonly List<ComObject> _owned;
    private bool _disposed;

    internal BenchmarkShaderProgram(
        List<ComObject> owned,
        IComponentType program,
        ShaderReflection reflection,
        EntryPointReflection[] entries,
        string manifestSha256)
    {
        _owned = owned;
        Program = program;
        Reflection = reflection;
        Entries = entries;
        ManifestSha256 = manifestSha256;
    }

    internal IComponentType Program { get; }
    internal ShaderReflection Reflection { get; }
    internal EntryPointReflection[] Entries { get; }
    internal string ManifestSha256 { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        for (int index = _owned.Count - 1; index >= 0; index--)
            _owned[index].FinalRelease();
        _owned.Clear();
        _disposed = true;
    }
}

internal static class BenchmarkShaders
{
    internal const string ManifestFileName = "manifest.json";
    internal const string ProfileName = "sm_6_0";
    internal const string Source = """
        float4 Tint;

        struct VertexOutput
        {
            float4 Position : SV_Position;
            float4 Color : COLOR0;
        };

        [shader("vertex")]
        VertexOutput vertexMain(uint vertexId : SV_VertexID)
        {
            const float2 positions[3] =
            {
                float2(0.0, 0.75),
                float2(0.75, -0.75),
                float2(-0.75, -0.75),
            };
            const float4 colors[3] =
            {
                float4(1.0, 0.0, 0.0, 1.0),
                float4(0.0, 1.0, 0.0, 1.0),
                float4(0.0, 0.0, 1.0, 1.0),
            };
            VertexOutput result;
            result.Position = float4(positions[vertexId], 0.0, 1.0);
            result.Color = colors[vertexId];
            return result;
        }

        [shader("fragment")]
        float4 pixelMain(VertexOutput input) : SV_Target0
        {
            return input.Color * Tint;
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void computeMain(uint3 dispatchThread : SV_DispatchThreadID)
        {
        }
        """;

    internal static readonly BenchmarkShaderEntry[] Entries =
    [
        new("vertexMain", SlangStage.Vertex, "vertex.dxil"),
        new("pixelMain", SlangStage.Fragment, "pixel.dxil"),
        new("computeMain", SlangStage.Compute, "compute.dxil"),
    ];

    private static readonly Lazy<IGlobalSession> GlobalSession = new(
        CreateGlobalSession,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void EmitSharedArtifacts(string directory)
    {
        Directory.CreateDirectory(directory);
        RepresentativeFrameProfile.EmitSharedArtifacts(directory);
        Compiled compiled = Compile();
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < Entries.Length; index++)
            {
                string path = Path.Combine(directory, Entries[index].FileName);
                WriteIfDifferent(path, compiled.Code[index]);
                hashes.Add(Entries[index].Name, BenchmarkEnvironment.Sha256Bytes(compiled.Code[index]));
            }
            ShaderManifest manifest = new(
                BenchmarkEnvironment.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(Source)),
                ProfileName,
                "repository-pinned SlangShaderSharp/Slang 2026.4.2",
                hashes);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                ProcessRunJsonContext.Default.ShaderManifest);
            WriteIfDifferent(Path.Combine(directory, ManifestFileName), bytes);
        }
        finally
        {
            Release(compiled.Owned);
        }
    }

    internal static BenchmarkShaderProgram Open(string directory)
    {
        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The shared Slang shader manifest is missing.", manifestPath);
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        ShaderManifest manifest = ReadManifest(manifestBytes);
        string sourceHash = BenchmarkEnvironment.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(Source));
        if (!string.Equals(sourceHash, manifest.SourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The shared shader source hash does not match this runner build.");

        Compiled compiled = Compile();
        try
        {
            for (int index = 0; index < Entries.Length; index++)
            {
                BenchmarkShaderEntry entry = Entries[index];
                string filePath = Path.Combine(directory, entry.FileName);
                byte[] emitted = File.ReadAllBytes(filePath);
                if (!emitted.AsSpan().SequenceEqual(compiled.Code[index]))
                {
                    throw new InvalidDataException(
                        $"The Slang-produced DXIL for '{entry.Name}' differs from the shared artifact.");
                }
                string hash = BenchmarkEnvironment.Sha256Bytes(emitted);
                if (!manifest.EntrySha256.TryGetValue(entry.Name, out string? expected) ||
                    !string.Equals(hash, expected, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The shader manifest hash for '{entry.Name}' is invalid.");
                }
            }
            return new BenchmarkShaderProgram(
                compiled.Owned,
                compiled.Program,
                compiled.Reflection,
                compiled.ReflectedEntries,
                BenchmarkEnvironment.Sha256Bytes(manifestBytes));
        }
        catch
        {
            Release(compiled.Owned);
            throw;
        }
    }

    internal static BenchmarkShaderProgram OpenVulkan()
    {
        Compiled compiled = Compile(SlangCompileTarget.Spirv);
        try
        {
            string identity = BenchmarkEnvironment.Sha256Bytes(
                System.Text.Encoding.UTF8.GetBytes(Source + "\nspirv/glsl_460"));
            return new BenchmarkShaderProgram(
                compiled.Owned,
                compiled.Program,
                compiled.Reflection,
                compiled.ReflectedEntries,
                identity);
        }
        catch
        {
            Release(compiled.Owned);
            throw;
        }
    }

    private static ShaderManifest ReadManifest(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The shared shader manifest root must be an object.");

        string sourceSha256 = ReadRequiredString(root, "sourceSha256");
        string profile = ReadRequiredString(root, "profile");
        string compiler = ReadRequiredString(root, "compiler");
        if (!root.TryGetProperty("entrySha256", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The shared shader manifest entry hashes are invalid.");
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String ||
                entry.Value.GetString() is not string hash ||
                !hashes.TryAdd(entry.Name, hash))
            {
                throw new InvalidDataException("The shared shader manifest contains an invalid entry hash.");
            }
        }
        return new ShaderManifest(sourceSha256, profile, compiler, hashes);
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string result)
        {
            throw new InvalidDataException($"The shared shader manifest property '{name}' is invalid.");
        }
        return result;
    }

    internal static (byte[] Vertex, byte[] Pixel, byte[] Compute, string ManifestSha256) LoadNativeArtifacts(
        string directory)
    {
        string manifestPath = Path.Combine(directory, ManifestFileName);
        byte[] manifest = File.ReadAllBytes(manifestPath);
        return (
            File.ReadAllBytes(Path.Combine(directory, Entries[0].FileName)),
            File.ReadAllBytes(Path.Combine(directory, Entries[1].FileName)),
            File.ReadAllBytes(Path.Combine(directory, Entries[2].FileName)),
            BenchmarkEnvironment.Sha256Bytes(manifest));
    }

    private static Compiled Compile(SlangCompileTarget target = SlangCompileTarget.Dxil)
    {
        var owned = new List<ComObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            IGlobalSession global = GlobalSession.Value;
            SlangProfileID profile = global.FindProfile(
                target == SlangCompileTarget.Spirv ? "glsl_460" : ProfileName);
            SessionDesc description = new()
            {
                Targets =
                [
                    new TargetDesc
                    {
                        Format = target,
                        Profile = profile,
                        Flags = target == SlangCompileTarget.Spirv
                            ? SlangTargetFlags.GenerateSpirvDirectly
                            : 0,
                        CompilerOptionEntries = target == SlangCompileTarget.Spirv
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
                CompilerOptionEntries =
                [
                    new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                ],
            };
            RequireSuccess(global.CreateSession(description, out ISession session), "Slang session creation", null);
            Track(session);
            ISlangBlob source = Track(Slang.CreateBlob(Source));
            string virtualPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "graphics_benchmark.slang"));
            IModule? module = session.LoadModuleFromSource(
                "graphics_benchmark",
                virtualPath,
                source,
                out ISlangBlob? moduleDiagnostics);
            TrackOptional(moduleDiagnostics);
            if (module is null)
                throw new InvalidDataException(FormatFailure("Slang module load failed", moduleDiagnostics));
            Track(module);

            IComponentType[] components = new IComponentType[Entries.Length + 1];
            components[0] = module;
            for (int index = 0; index < Entries.Length; index++)
            {
                RequireSuccess(
                    module.FindEntryPointByName(Entries[index].Name, out IEntryPoint entry),
                    $"Slang entry lookup '{Entries[index].Name}'",
                    null);
                Track(entry);
                components[index + 1] = entry;
            }
            SlangResult compose = session.CreateCompositeComponentType(
                components,
                out IComponentType? composite,
                out ISlangBlob? composeDiagnostics);
            TrackOptional(composeDiagnostics);
            if (!compose.Succeeded || composite is null)
                throw new InvalidDataException(FormatFailure("Slang composition failed", composeDiagnostics));
            Track(composite);
            SlangResult link = composite.Link(out IComponentType? linked, out ISlangBlob? linkDiagnostics);
            TrackOptional(linkDiagnostics);
            if (!link.Succeeded || linked is null)
                throw new InvalidDataException(FormatFailure("Slang link failed", linkDiagnostics));
            Track(linked);

            ShaderReflection reflection = linked.GetLayout(0, out ISlangBlob? layoutDiagnostics);
            TrackOptional(layoutDiagnostics);
            if (reflection == ShaderReflection.Null || reflection.EntryPointCount != checked((nuint)Entries.Length))
                throw new InvalidDataException(FormatFailure("Slang reflection failed", layoutDiagnostics));
            var reflected = new EntryPointReflection[Entries.Length];
            var code = new byte[Entries.Length][];
            for (int index = 0; index < Entries.Length; index++)
            {
                reflected[index] = reflection.GetEntryPointByIndex(checked((uint)index));
                if (reflected[index] == EntryPointReflection.Null ||
                    !string.Equals(reflected[index].Name, Entries[index].Name, StringComparison.Ordinal) ||
                    reflected[index].Stage != Entries[index].Stage)
                {
                    throw new InvalidDataException($"Slang reflection entry {index} is not '{Entries[index].Name}'.");
                }
                RequireSuccess(
                    linked.GetEntryPointCode(index, 0, out ISlangBlob blob, out ISlangBlob? diagnostics),
                    $"Slang {target} emission '{Entries[index].Name}'",
                    diagnostics);
                Track(blob);
                TrackOptional(diagnostics);
                code[index] = blob.Buffer.ToArray();
            }
            return new Compiled(owned, linked, reflection, reflected, code);

            T Track<T>(T value) where T : class
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
        catch
        {
            Release(owned);
            throw;
        }
    }

    private static IGlobalSession CreateGlobalSession()
    {
        SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session);
        RequireSuccess(result, "Slang global-session creation", null);
        return session;
    }

    private static void RequireSuccess(SlangResult result, string operation, ISlangBlob? diagnostics)
    {
        if (!result.Succeeded)
            throw new InvalidDataException(FormatFailure($"{operation} failed", diagnostics));
    }

    private static string FormatFailure(string prefix, ISlangBlob? diagnostics)
    {
        string? detail = diagnostics?.AsString;
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail.Trim()}";
    }

    private static void WriteIfDifferent(string path, ReadOnlySpan<byte> bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return;
        File.WriteAllBytes(path, bytes);
    }

    private static void Release(List<ComObject> objects)
    {
        for (int index = objects.Count - 1; index >= 0; index--)
            objects[index].FinalRelease();
        objects.Clear();
    }

    private sealed record Compiled(
        List<ComObject> Owned,
        IComponentType Program,
        ShaderReflection Reflection,
        EntryPointReflection[] ReflectedEntries,
        byte[][] Code);
}
