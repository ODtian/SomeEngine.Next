using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal readonly record struct D3D12TestEntry(
    string Name,
    SlangStage Stage,
    string? NameOverride = null);

internal sealed class D3D12TestShader
{
    private readonly ISession _session;
    private readonly IComponentType[] _components;
    private readonly IComponentType _combinedCode;
    private readonly ICompileRequest? _compileRequest;
    private readonly EntryPointReflection[] _entries;

    internal D3D12TestShader(
        ISession session,
        IComponentType[] components,
        IComponentType combinedCode,
        ICompileRequest? compileRequest,
        IComponentType slangCode,
        ShaderReflection reflection,
        EntryPointReflection[] entries,
        byte[][] entryCode,
        byte[] targetCode)
    {
        _session = session;
        _components = components;
        _combinedCode = combinedCode;
        _compileRequest = compileRequest;
        SlangCode = slangCode;
        Reflection = reflection;
        _entries = entries;
        _entryCode = entryCode;
        TargetCode = new ReadOnlySequence<byte>(targetCode);
    }

    private readonly byte[][] _entryCode;
    internal IComponentType SlangCode { get; }
    internal ShaderReflection Reflection { get; }
    internal ReadOnlySequence<byte> TargetCode { get; }

    internal EntryPointReflection GetEntryPoint(int index)
        => _entries[index];

    internal ReadOnlySequence<byte> GetEntryCode(int index)
        => new(_entryCode[index]);

}
