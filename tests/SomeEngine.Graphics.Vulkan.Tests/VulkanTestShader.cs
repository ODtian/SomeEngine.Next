using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan.Tests;

internal sealed class VulkanTestShader
{
    private static readonly Lazy<IGlobalSession> GlobalSession = new(CreateGlobalSession);
    private readonly ISession _session;
    private readonly IModule _sourceUnit;
    private readonly IEntryPoint[] _slangEntries;
    private readonly IComponentType _combinedCode;
    private readonly byte[][] _entryCode;

    private VulkanTestShader(
        ISession session,
        IModule sourceUnit,
        IEntryPoint[] slangEntries,
        IComponentType combinedCode,
        IComponentType slangCode,
        ShaderReflection reflection,
        EntryPointReflection[] entries,
        byte[][] entryCode,
        byte[] targetCode)
    {
        _session = session;
        _sourceUnit = sourceUnit;
        _slangEntries = slangEntries;
        _combinedCode = combinedCode;
        SlangCode = slangCode;
        Reflection = reflection;
        Entries = entries;
        _entryCode = entryCode;
        TargetCode = new ReadOnlySequence<byte>(targetCode);
    }

    internal IComponentType SlangCode { get; }
    internal ShaderReflection Reflection { get; }
    internal EntryPointReflection[] Entries { get; }
    internal ReadOnlySequence<byte> TargetCode { get; }

    internal ReadOnlySequence<byte> GetEntryCode(int index) =>
        new(_entryCode[index]);

    internal bool IsParameterLocationUsed(
        int entryPointIndex,
        SlangParameterCategory category,
        nuint space,
        nuint register)
    {
        Require(
            SlangCode.GetEntryPointMetadata(
                entryPointIndex,
                0,
                out IMetadata metadata,
                out ISlangBlob? diagnostics),
            $"entry metadata {entryPointIndex}",
            diagnostics);
        Require(
            metadata.IsParameterLocationUsed(category, space, register, out bool used),
            $"entry metadata location {entryPointIndex}",
            null);
        return used;
    }

    internal static VulkanTestShader Compile(
        string source,
        params (string Name, SlangStage Stage)[] entries)
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
        ISlangBlob sourceBlob = Slang.CreateBlob(source);
        IModule? sourceUnit = session.LoadModuleFromSource(
            "vulkan_test",
            Path.Combine(AppContext.BaseDirectory, "vulkan_test.slang"),
            sourceBlob,
            out ISlangBlob? moduleDiagnostics);
        if (sourceUnit is null)
            throw new InvalidOperationException(Failure("module", moduleDiagnostics));

        var slangEntries = new IEntryPoint[entries.Length];
        IComponentType[] components = new IComponentType[entries.Length + 1];
        components[0] = sourceUnit;
        for (int index = 0; index < entries.Length; index++)
        {
            Require(
                sourceUnit.FindEntryPointByName(entries[index].Name, out IEntryPoint entry),
                $"entry {entries[index].Name}",
                null);
            slangEntries[index] = entry;
            components[index + 1] = entry;
        }
        Require(
            session.CreateCompositeComponentType(
                components,
                out IComponentType combinedCode,
                out ISlangBlob? compositionDiagnostics),
            "composition",
            compositionDiagnostics);
        Require(
            combinedCode.Link(out IComponentType linkedCode, out ISlangBlob? linkDiagnostics),
            "link",
            linkDiagnostics);
        ShaderReflection reflection = linkedCode.GetLayout(0, out ISlangBlob? layoutDiagnostics);
        if (reflection == ShaderReflection.Null)
            throw new InvalidOperationException(Failure("layout", layoutDiagnostics));
        var reflectedEntries = new EntryPointReflection[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            reflectedEntries[index] = reflection.GetEntryPointByIndex(checked((uint)index));
            if (reflectedEntries[index] == EntryPointReflection.Null ||
                reflectedEntries[index].Stage != entries[index].Stage)
            {
                throw new InvalidOperationException($"Unexpected reflection for {entries[index].Name}.");
            }
        }
        var entryCode = new byte[entries.Length][];
        for (int index = 0; index < entryCode.Length; index++)
            entryCode[index] = ReadEntryCode(linkedCode, index);
        byte[] targetCode = ReadTargetCode(linkedCode);
        return new VulkanTestShader(
            session,
            sourceUnit,
            slangEntries,
            combinedCode,
            linkedCode,
            reflection,
            reflectedEntries,
            entryCode,
            targetCode);
    }

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

    private static byte[] ReadEntryCode(IComponentType slangCode, int index)
    {
        SlangResult result = slangCode.GetEntryPointCode(
            index,
            0,
            out ISlangBlob code,
            out ISlangBlob? diagnostics);
        Require(result, $"entry code {index}", diagnostics);
        return code.Buffer.ToArray();
    }

    private static byte[] ReadTargetCode(IComponentType slangCode)
    {
        SlangResult result = slangCode.GetTargetCode(
            0,
            out ISlangBlob code,
            out ISlangBlob? diagnostics);
        Require(result, "target code", diagnostics);
        return code.Buffer.ToArray();
    }
}
