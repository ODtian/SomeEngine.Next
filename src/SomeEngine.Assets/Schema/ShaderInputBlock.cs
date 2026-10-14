using System.Globalization;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Slang source names and their serialized IR, shared by strongly typed shader input blocks.</summary>
public abstract class ShaderInputBlock : AssetBlock
{
    private IList<string> _sourceNames = [];
    private IList<ShaderInputLocation> _inputs = [];

    public IList<string> SourceNames
    {
        get { lock (SyncRoot) { RequireIdle(); return _sourceNames; } }
        set { lock (SyncRoot) { RequireIdle(); _sourceNames = new List<string>(value); MarkChanged(); } }
    }

    protected IList<ShaderInputLocation> SelectedInputs
    {
        get { lock (SyncRoot) { RequireIdle(); return _inputs; } }
        set { lock (SyncRoot) { RequireIdle(); _inputs = new List<ShaderInputLocation>(value); MarkChanged(); } }
    }

    protected ShaderInputBlock() { }
    protected ShaderInputBlock(ShaderInputBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _sourceNames = new List<string>(source._sourceNames);
        _inputs = new List<ShaderInputLocation>(source._inputs);
    }

    public static ulong SerializedSourceKey(int sourceIndex) => BinaryFieldKey.FromName(
        "SomeEngine.Assets.Schema.Shader.Source.Slang." + sourceIndex.ToString(CultureInfo.InvariantCulture));

    public void SetSerializedSource(int sourceIndex, ReadOnlySpan<byte> bytes)
        => SetBytes(SerializedSourceKey(sourceIndex), bytes.ToArray());

    protected async ValueTask ReadInputDataAsync(AssetBlockSource source, CancellationToken cancellationToken)
    {
        var data = new ShaderInputData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        _sourceNames = data.SourceNames;
        _inputs = data.Inputs;
        AttachSource(source);
    }

    protected async ValueTask WriteInputDataAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        writer.WriteMetadata(BlockId, new ShaderInputData { SourceNames = _sourceNames, Inputs = _inputs });
        await WriteChunksAsync(writer, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The position of one actual Slang input in its containing block.</summary>
[BinarySerializable, TextSerializable, BinaryValue]
[BinaryFields(nameof(BlockTypeId), nameof(SourceIndex))]
public readonly partial record struct ShaderInputLocation(Guid BlockTypeId, int SourceIndex);

[BinarySerializable]
[BinaryFields(nameof(SourceNames), nameof(Inputs))]
internal sealed partial class ShaderInputData
{
    public IList<string> SourceNames { get; set; } = [];
    public IList<ShaderInputLocation> Inputs { get; set; } = [];
}

internal readonly record struct ShaderInputSource(string Name, ReadOnlyMemory<byte> Serialized);
