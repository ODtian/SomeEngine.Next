using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{

    private static CompiledShaderLibrary CompileShaderLibrary(
        ReadOnlySequence<byte> code,
        ISlangBlob? codeBlob,
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries)
    {
        if (entries.IsEmpty)
            throw new ArgumentException("A state-object library requires at least one Slang entry point.", nameof(entries));
        for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            _ = GetStateObjectEntryPointIndex(reflection, entries[entryIndex]);
        byte[] library = CopyCode(code, codeBlob);
        if (library.Length == 0)
            throw new GraphicsException(
                GraphicsError.ShaderCompilation,
                "The state-object Slang target contains no DXIL.");

        return new CompiledShaderLibrary(library);
    }

    private static int GetStateObjectEntryPointIndex(
        ShaderReflection reflection,
        EntryPointReflection entry)
    {
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            if (reflection.GetEntryPointByIndex(index) == entry)
                return checked((int)index);
        }
        throw new ArgumentException(
            "The state-object entry-point reflection does not belong to the supplied Slang program.",
            nameof(entry));
    }

    internal static string GetStableEntryPointName(EntryPointReflection entryPoint) =>
        WorkGraphValidation.GetEffectiveEntryPointName(entryPoint);

    internal static string GetStableEntryPointName(string name, string? nameOverride) =>
        WorkGraphValidation.GetEffectiveEntryPointName(name, nameOverride);

    private static string ValidateStateObjectEntryPoint(
        ShaderReflection reflection,
        EntryPointReflection entryPoint,
        ReadOnlySpan<SlangStage> permittedStages,
        string role)
    {
        if (entryPoint == EntryPointReflection.Null)
            throw new ArgumentException($"The {role} entry point is null.", nameof(entryPoint));
        if (!permittedStages.Contains(entryPoint.Stage))
        {
            throw new ArgumentException(
                $"The selected {role} entry point has Slang stage {entryPoint.Stage}.",
                nameof(entryPoint));
        }

        bool found = false;
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            if (reflection.GetEntryPointByIndex(index) == entryPoint)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            throw new ArgumentException(
                $"The {role} entry-point reflection does not belong to the supplied linked program.",
                nameof(entryPoint));
        }

        string name = WorkGraphValidation.GetEffectiveEntryPointName(entryPoint);
        if (string.IsNullOrWhiteSpace(name))
            throw new GraphicsException(GraphicsError.ShaderCompilation, $"The {role} export has no Slang name.");
        return name;
    }

    private sealed class CompiledShaderLibrary
    {
        internal CompiledShaderLibrary(byte[] code) => Code = code;

        internal byte[] Code { get; }
    }

    private sealed class NativeStateObjectArena : IDisposable
    {
        private readonly List<nint> _allocations = [];

        internal T* Allocate<T>(int count = 1)
            where T : unmanaged
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            nuint bytes = checked((nuint)count * (nuint)sizeof(T));
            _allocations.EnsureCapacity(checked(_allocations.Count + 1));
            T* result = (T*)NativeMemory.AllocZeroed(bytes);
            if (result is null)
                throw new OutOfMemoryException();
            _allocations.Add((nint)result);
            return result;
        }

        internal char* String(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            char* result = Allocate<char>(checked(value.Length + 1));
            value.AsSpan().CopyTo(new Span<char>(result, value.Length));
            return result;
        }

        internal byte* Bytes(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty)
                throw new ArgumentException("A native byte allocation cannot be empty.", nameof(value));
            byte* result = Allocate<byte>(value.Length);
            value.CopyTo(new Span<byte>(result, value.Length));
            return result;
        }

        public void Dispose()
        {
            foreach (nint allocation in _allocations)
                NativeMemory.Free((void*)allocation);
            _allocations.Clear();
        }
    }
}
