using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private static CompiledProgramLibrary CompileProgramLibrary(IComponentType program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.GetSpecializationParamCount() != 0)
        {
            throw new GraphicsException(
                GraphicsError.ShaderCompilation,
                "State-object creation requires a fully specialized Slang program.");
        }

        ISlangBlob? code = null;
        ISlangBlob? diagnostics = null;
        try
        {
            SlangResult result = program.GetTargetCode(0, out code!, out diagnostics);
            if (result.Failed || code is null || code.GetBufferPointer() is null ||
                code.GetBufferSize() == 0)
            {
                throw new GraphicsException(
                    GraphicsError.ShaderCompilation,
                    FormatSlangFailure("Slang state-object DXIL generation failed", diagnostics),
                    result);
            }

            byte[] bytes = new ReadOnlySpan<byte>(
                (void*)code.GetBufferPointer(),
                checked((int)code.GetBufferSize())).ToArray();
            return new CompiledProgramLibrary(bytes, SHA256.HashData(bytes));
        }
        finally
        {
            ReleaseSlang(code);
            ReleaseSlang(diagnostics);
        }
    }

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

        string name = string.IsNullOrWhiteSpace(entryPoint.NameOverride)
            ? entryPoint.Name
            : entryPoint.NameOverride;
        if (string.IsNullOrWhiteSpace(name))
            throw new GraphicsException(GraphicsError.ShaderCompilation, $"The {role} export has no Slang name.");
        return name;
    }

    private sealed class CompiledProgramLibrary
    {
        internal CompiledProgramLibrary(byte[] code, byte[] hash)
        {
            Code = code;
            Hash = hash;
        }

        internal byte[] Code { get; }
        internal byte[] Hash { get; }
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

        public void Dispose()
        {
            foreach (nint allocation in _allocations)
                NativeMemory.Free((void*)allocation);
            _allocations.Clear();
        }
    }
}
