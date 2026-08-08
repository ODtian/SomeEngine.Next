using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private static readonly byte[] StateObjectReplayMagic = "SERHISO1"u8.ToArray();

    private static CompiledProgramLibrary CompileProgramLibrary(
        IComponentType program,
        ShaderReflection reflection)
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
            return new CompiledProgramLibrary(
                bytes,
                SHA256.HashData(bytes),
                CreateSlangProgramIdentity(program, reflection));
        }
        finally
        {
            ReleaseSlang(code);
            ReleaseSlang(diagnostics);
        }
    }

    private static byte[] CreateSlangProgramIdentity(
        IComponentType program,
        ShaderReflection reflection)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(reflection.EntryPointCount);
            for (uint index = 0; index < reflection.EntryPointCount; index++)
            {
                EntryPointReflection entryPoint = reflection.GetEntryPointByIndex(index);
                writer.Write((int)entryPoint.Stage);
                WriteCanonicalString(writer, GetStableEntryPointName(entryPoint));
                WriteCanonicalBytes(
                    writer,
                    GetSlangEntryPointIdentity(program, checked((int)index)));
            }
        }

        return SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static byte[] GetSlangEntryPointIdentity(IComponentType program, int entryPointIndex)
    {
        ISlangBlob? identity = null;
        try
        {
            program.GetEntryPointHash(entryPointIndex, 0, out identity!);
            if (identity is null || identity.GetBufferPointer() is null ||
                identity.GetBufferSize() == 0)
            {
                throw new GraphicsException(
                    GraphicsError.ShaderCompilation,
                    "Slang did not produce an entry-point target-settings identity.");
            }

            return new ReadOnlySpan<byte>(
                (void*)identity.GetBufferPointer(),
                checked((int)identity.GetBufferSize())).ToArray();
        }
        finally
        {
            ReleaseSlang(identity);
        }
    }

    private static string GetStableEntryPointName(EntryPointReflection entryPoint) =>
        string.IsNullOrWhiteSpace(entryPoint.NameOverride)
            ? entryPoint.Name
            : entryPoint.NameOverride;

    private static void WriteCompiledProgramIdentity(
        BinaryWriter writer,
        CompiledProgramLibrary library)
    {
        WriteCanonicalBytes(writer, library.ProgramIdentity);
        WriteCanonicalBytes(writer, library.CodeHash);
    }

    private static byte[] ResolveStateObjectReplayCode(
        D3D12PipelineCache? cache,
        byte family,
        ReadOnlySpan<byte> key,
        CompiledProgramLibrary library)
    {
        if (cache is null || !cache.TryGet(family, key, out byte[] payload))
            return library.Code;

        try
        {
            ReadOnlySpan<byte> source = payload;
            int minimumLength = checked(
                StateObjectReplayMagic.Length + sizeof(uint) + sizeof(byte) +
                32 + 32 + 32 + sizeof(uint));
            if (source.Length < minimumLength ||
                !source[..StateObjectReplayMagic.Length].SequenceEqual(StateObjectReplayMagic))
            {
                throw new InvalidDataException("The state-object replay magic is invalid.");
            }

            int offset = StateObjectReplayMagic.Length;
            uint version = ReadReplayUInt32(source, ref offset);
            if (version != StateObjectReplaySchemaVersion)
                throw new InvalidDataException("The state-object replay schema is unsupported.");
            if (ReadReplayByte(source, ref offset) != family)
                throw new InvalidDataException("The state-object replay family is invalid.");
            if (!ReadReplayBytes(source, ref offset, 32).SequenceEqual(key))
                throw new InvalidDataException("The state-object replay key is invalid.");
            if (!ReadReplayBytes(source, ref offset, 32).SequenceEqual(library.ProgramIdentity))
                throw new InvalidDataException("The state-object Slang program identity is invalid.");
            ReadOnlySpan<byte> expectedCodeHash = ReadReplayBytes(source, ref offset, 32);
            uint codeLength = ReadReplayUInt32(source, ref offset);
            ReadOnlySpan<byte> code = ReadReplayBytes(
                source,
                ref offset,
                checked((int)codeLength));
            if (offset != source.Length ||
                !expectedCodeHash.SequenceEqual(library.CodeHash) ||
                !SHA256.HashData(code).AsSpan().SequenceEqual(expectedCodeHash))
            {
                throw new InvalidDataException("The state-object replay code is invalid.");
            }

            return code.ToArray();
        }
        catch (Exception exception) when (exception is not GraphicsException)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "The state-object replay cache section is corrupt.",
                innerException: exception);
        }
    }

    private static void StoreStateObjectReplay(
        D3D12PipelineCache? cache,
        byte family,
        ReadOnlySpan<byte> key,
        CompiledProgramLibrary library)
    {
        if (cache is null)
            return;

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(StateObjectReplayMagic);
            writer.Write(StateObjectReplaySchemaVersion);
            writer.Write(family);
            writer.Write(key);
            writer.Write(library.ProgramIdentity);
            writer.Write(library.CodeHash);
            writer.Write(checked((uint)library.Code.Length));
            writer.Write(library.Code);
        }
        cache.Store(
            family,
            key,
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static uint ReadReplayUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        if (source.Length - offset < sizeof(uint))
            throw new EndOfStreamException();
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += sizeof(uint);
        return value;
    }

    private static byte ReadReplayByte(ReadOnlySpan<byte> source, ref int offset)
    {
        if ((uint)offset >= (uint)source.Length)
            throw new EndOfStreamException();
        return source[offset++];
    }

    private static ReadOnlySpan<byte> ReadReplayBytes(
        ReadOnlySpan<byte> source,
        ref int offset,
        int length)
    {
        if (length < 0 || source.Length - offset < length)
            throw new EndOfStreamException();
        ReadOnlySpan<byte> value = source.Slice(offset, length);
        offset += length;
        return value;
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
        internal CompiledProgramLibrary(
            byte[] code,
            byte[] codeHash,
            byte[] programIdentity)
        {
            Code = code;
            CodeHash = codeHash;
            ProgramIdentity = programIdentity;
        }

        internal byte[] Code { get; }
        internal byte[] CodeHash { get; }
        internal byte[] ProgramIdentity { get; }
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
