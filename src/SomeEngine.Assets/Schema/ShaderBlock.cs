using System.Globalization;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Schema;

/// <summary>Compiled shader metadata and independently acquired backend bytecode.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ShaderBlock"), BinarySerializable]
[BinaryFields(nameof(ImportTrace), nameof(Programs), nameof(EntryPointAttributes), nameof(Reflections),
    nameof(EntryPointReflections), nameof(Metadata), nameof(EntryPointMetadata), nameof(SchemaVersion))]
public sealed partial class ShaderBlock : AssetPageBlock
{
    public const uint Version = 9;
    public partial ImportTrace? ImportTrace { get; set; }
    public partial IList<CookedShaderProgram> Programs { get; set; }
    public partial IList<ShaderEntryPointAttribute>? EntryPointAttributes { get; set; }
    public partial IList<BackendReflection>? Reflections { get; set; }
    public partial IList<ShaderEntryPointReflection>? EntryPointReflections { get; set; }
    public partial ShaderMetadata? Metadata { get; set; }
    public partial IList<ShaderEntryPointMetadata>? EntryPointMetadata { get; set; }
    public partial uint SchemaVersion { get; set; }

    partial void Initialize() => SchemaVersion = Version;
    public static ulong BytecodeChunkKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return BinaryFieldKey.FromName("SomeEngine.Assets.Schema.Shader.Programs.Data."
            + index.ToString(CultureInfo.InvariantCulture));
    }

    public ValueTask<Shader> CreateAsync(string name = "", CancellationToken cancellationToken = default)
        => CreateCoreAsync(name, null, cancellationToken);
    public ValueTask<Shader> CreateAsync(ShaderTarget target, string name = "", CancellationToken cancellationToken = default)
        => CreateCoreAsync(name, target == ShaderTarget.Spirv ? "spirv" : "dxil", cancellationToken);

    private async ValueTask<Shader> CreateCoreAsync(string name, string? backend, CancellationToken cancellationToken)
    {
        using ShaderBlock copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        copy.ValidateContent();
        if (copy.Programs.Count == 0) throw new InvalidDataException("Creating a shader requires compiled bytecode.");
        var programs = ImmutableList.CreateBuilder<ShaderProgram?>();
        foreach (CookedShaderProgram code in copy.Programs)
        {
            if (backend is not null && code.Backend != backend) { programs.Add(null); continue; }
            using PageBuffer bytes = await copy.AcquireBufferAsync(code.DataChunkKey, cancellationToken).ConfigureAwait(false);
            if ((ulong)bytes.Length != code.DataDecodedLength)
                throw new InvalidDataException("Shader bytecode length differs from its asset declaration.");
            using var stream = new PageBufferStream(bytes, writable: false);
            ShaderProgram decoded = ShaderProgram.Read(stream);
            if (!string.Equals(code.Backend, decoded.Backend, StringComparison.Ordinal))
                throw new InvalidDataException("Shader bytecode backend differs from its asset declaration.");
            programs.Add(decoded);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var shader = new Shader
        {
            Name = name,
            Programs = programs.ToImmutable(),
            EntryPointAttributes = copy.EntryPointAttributes?.ToImmutableList(),
            Reflections = copy.Reflections?.ToImmutableList(),
            EntryPointReflections = copy.EntryPointReflections?.ToImmutableList(),
            Metadata = copy.Metadata,
            EntryPointMetadata = copy.EntryPointMetadata?.ToImmutableList(),
            SchemaVersion = copy.SchemaVersion,
        };
        Shader.Validate(shader);
        return shader;
    }

    public async ValueTask UpdateAsync(Shader shader, string name = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ObjectDisposedException.ThrowIf(shader.IsDisposed, shader);
        using Shader replacement = await CreateAsync(name, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        shader.Update(replacement);
    }

    protected override void ValidateContent()
    {
        var backends = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Programs.Count; index++)
        {
            CookedShaderProgram code = Programs[index];
            if (string.IsNullOrWhiteSpace(code.Backend) || !backends.Add(code.Backend))
                throw new InvalidDataException("Shader bytecode has a missing or duplicate backend.");
            if (code.DataChunkKey != BytecodeChunkKey(index) || code.DataDecodedLength == 0
                || code.DataDecodedLength > long.MaxValue)
                throw new InvalidDataException("Shader bytecode has an invalid chunk key or length.");
            if (Chunks.Contains(code.DataChunkKey) && (ulong)Chunks.GetLength(code.DataChunkKey) != code.DataDecodedLength)
                throw new InvalidDataException("Edited shader bytecode length differs from its declaration.");
        }
    }

    protected override async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        var keys = Programs.Select(static code => code.DataChunkKey).ToHashSet();
        foreach (ulong key in Chunks.Keys)
            if (!keys.Contains(key)) throw new InvalidDataException("An edited shader chunk has no bytecode declaration.");
        using AssetBlockSource? source = RetainSource();
        for (int index = 0; index < Programs.Count; index++)
        {
            ulong key = Programs[index].DataChunkKey;
            if (Chunks.Contains(key))
                writer.WriteChunk(key, Chunks, ChunkCompression.Brotli, ordinal: checked((uint)index));
            else
                await writer.CopyChunkAsync(source ?? throw new InvalidDataException("Shader bytecode has no content."),
                    key, cancellationToken, ordinal: checked((uint)index)).ConfigureAwait(false);
        }
    }
}
