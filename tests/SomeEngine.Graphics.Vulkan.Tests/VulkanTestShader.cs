using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan.Tests;

internal sealed class VulkanTestShader
{
    private readonly ISession _session;
    private readonly IModule _sourceUnit;
    private readonly IEntryPoint[] _slangEntries;
    private readonly IComponentType _combinedCode;
    private readonly byte[][] _entryCode;

    internal VulkanTestShader(
        IGlobalSession global,
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
        SlangCode = new SlangShaderData(global, session, slangCode);
        Reflection = reflection;
        Entries = entries;
        _entryCode = entryCode;
        TargetCode = new ReadOnlySequence<byte>(targetCode);
    }

    internal SlangShaderData SlangCode { get; }
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
        VulkanTestCompiler.Require(
            SlangCode.Code.GetEntryPointMetadata(
                entryPointIndex,
                0,
                out IMetadata metadata,
                out ISlangBlob? diagnostics),
            $"entry metadata {entryPointIndex}",
            diagnostics);
        VulkanTestCompiler.Require(
            metadata.IsParameterLocationUsed(category, space, register, out bool used),
            $"entry metadata location {entryPointIndex}",
            null);
        return used;
    }

}
