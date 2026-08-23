using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan.Tests;

internal sealed class VulkanTestShaderProgram : IDisposable
{
    private static readonly Lazy<IGlobalSession> GlobalSession = new(CreateGlobalSession);
    private readonly List<ComObject> _owned;

    private VulkanTestShaderProgram(
        List<ComObject> owned,
        IComponentType program,
        ShaderReflection reflection,
        EntryPointReflection[] entries)
    {
        _owned = owned;
        Program = program;
        Reflection = reflection;
        Entries = entries;
    }

    internal IComponentType Program { get; }
    internal ShaderReflection Reflection { get; }
    internal EntryPointReflection[] Entries { get; }

    internal unsafe byte[] GetEntryPointCode(int index)
    {
        ISlangBlob? code = null;
        ISlangBlob? diagnostics = null;
        try
        {
            Require(
                Program.GetEntryPointCode(index, 0, out code!, out diagnostics),
                $"entry code {index}",
                diagnostics);
            if (code is null || code.GetBufferPointer() is null || code.GetBufferSize() == 0)
                throw new InvalidOperationException($"Entry point {index} produced no SPIR-V code.");
            return new ReadOnlySpan<byte>(
                (void*)code.GetBufferPointer(),
                checked((int)code.GetBufferSize())).ToArray();
        }
        finally
        {
            if ((object?)code is ComObject codeObject)
                codeObject.FinalRelease();
            if ((object?)diagnostics is ComObject diagnosticsObject)
                diagnosticsObject.FinalRelease();
        }
    }

    internal bool IsParameterLocationUsed(
        int entryPointIndex,
        SlangParameterCategory category,
        nuint space,
        nuint register)
    {
        IMetadata? metadata = null;
        ISlangBlob? diagnostics = null;
        try
        {
            Require(
                Program.GetEntryPointMetadata(
                    entryPointIndex,
                    0,
                    out metadata!,
                    out diagnostics),
                $"entry metadata {entryPointIndex}",
                diagnostics);
            Require(
                metadata.IsParameterLocationUsed(category, space, register, out bool used),
                $"entry metadata location {entryPointIndex}",
                null);
            return used;
        }
        finally
        {
            if ((object?)metadata is ComObject metadataObject)
                metadataObject.FinalRelease();
            if ((object?)diagnostics is ComObject diagnosticsObject)
                diagnosticsObject.FinalRelease();
        }
    }

    internal static VulkanTestShaderProgram Compile(
        string source,
        params (string Name, SlangStage Stage)[] entries)
    {
        var owned = new List<ComObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            IGlobalSession global = GlobalSession.Value;
            SessionDesc description = new()
            {
                Targets =
                [
                    new TargetDesc
                    {
                        Format = SlangCompileTarget.Spirv,
                        Profile = global.FindProfile("glsl_460"),
                        Flags = SlangTargetFlags.GenerateSpirvDirectly,
                        CompilerOptionEntries =
                        [
                            new(
                                CompilerOptionName.VulkanUseEntryPointName,
                                CompilerOptionValue.FromInt(1)),
                        ],
                    },
                ],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
                CompilerOptionEntries =
                [
                    new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
                    new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                ],
            };
            Require(global.CreateSession(description, out ISession session), "session", null);
            Track(session);
            ISlangBlob sourceBlob = Track(Slang.CreateBlob(source));
            IModule? module = session.LoadModuleFromSource(
                "vulkan_test",
                Path.Combine(AppContext.BaseDirectory, "vulkan_test.slang"),
                sourceBlob,
                out ISlangBlob? moduleDiagnostics);
            TrackOptional(moduleDiagnostics);
            if (module is null)
                throw new InvalidOperationException(Failure("module", moduleDiagnostics));
            Track(module);
            IComponentType[] components = new IComponentType[entries.Length + 1];
            components[0] = module;
            for (int index = 0; index < entries.Length; index++)
            {
                Require(
                    module.FindEntryPointByName(entries[index].Name, out IEntryPoint entry),
                    $"entry {entries[index].Name}",
                    null);
                Track(entry);
                components[index + 1] = entry;
            }
            Require(
                session.CreateCompositeComponentType(
                    components,
                    out IComponentType? composite,
                    out ISlangBlob? compositionDiagnostics),
                "composition",
                compositionDiagnostics);
            TrackOptional(compositionDiagnostics);
            Track(composite!);
            Require(
                composite!.Link(out IComponentType? linked, out ISlangBlob? linkDiagnostics),
                "link",
                linkDiagnostics);
            TrackOptional(linkDiagnostics);
            Track(linked!);
            ShaderReflection reflection = linked!.GetLayout(0, out ISlangBlob? layoutDiagnostics);
            TrackOptional(layoutDiagnostics);
            if (reflection == ShaderReflection.Null)
                throw new InvalidOperationException(Failure("layout", layoutDiagnostics));
            var reflectedEntries = new EntryPointReflection[entries.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                reflectedEntries[index] = reflection.GetEntryPointByIndex(checked((uint)index));
                if (reflectedEntries[index] == EntryPointReflection.Null ||
                    reflectedEntries[index].Stage != entries[index].Stage)
                    throw new InvalidOperationException($"Unexpected reflection for {entries[index].Name}.");
            }
            return new VulkanTestShaderProgram(owned, linked, reflection, reflectedEntries);

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

    public void Dispose() => Release(_owned);

    private static IGlobalSession CreateGlobalSession()
    {
        Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session), "global session", null);
        return session;
    }

    private static void Require(SlangResult result, string operation, ISlangBlob? diagnostics)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(Failure(operation, diagnostics));
    }

    private static string Failure(string operation, ISlangBlob? diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics?.AsString)
            ? operation
            : $"{operation}: {diagnostics.AsString}";

    private static void Release(List<ComObject> owned)
    {
        for (int index = owned.Count - 1; index >= 0; index--)
            owned[index].FinalRelease();
        owned.Clear();
    }
}
