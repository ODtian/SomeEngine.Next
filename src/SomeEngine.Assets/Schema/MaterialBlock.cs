using System.Runtime.CompilerServices;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>Shader and parent references with local material parameter overrides.</summary>
[AssetBlock("SomeEngine.Assets.MaterialBlock")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.MaterialBlock")]
public sealed partial class MaterialBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<MaterialBlock>, IBinaryObjectReader<MaterialBlock>
{
    private AssetId<ShaderAsset> _shader;
    private AssetId<MaterialAsset> _parent;
    private readonly List<MaterialParameter> _parameters = [];
    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Assets.MaterialBlock");
    public static ulong SchemaFingerprint => 0x4D41544276320001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 2;

    public MaterialBlock() { }
    private MaterialBlock(MaterialBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _shader = source._shader;
        _parent = source._parent;
        Name = source.Name;
        _parameters.AddRange(source._parameters);
    }
    /// <summary>The asset name supplied when resolving complete material content.</summary>
    [BinaryIgnore] public string? Name { get; internal set; }
    public AssetId<ShaderAsset> Shader { get => _shader; set { _shader = value; MarkChanged(); } }
    public AssetId<MaterialAsset> Parent { get => _parent; set { _parent = value; MarkChanged(); } }
    public IList<MaterialParameter> Parameters => _parameters;
    public override MaterialBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override MaterialBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    public bool HasOverride(string name) => GetParameter(name).HasValue;
    public bool Reset(string name)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            for (int index = 0; index < _parameters.Count; index++)
                if (_parameters[index].Name == name) { _parameters.RemoveAt(index); return true; }
            return false;
        }
    }
    public MaterialParameter? GetParameter(string name)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            foreach (MaterialParameter value in _parameters) if (value.Name == name) return value;
            return null;
        }
    }
    public void SetParameter(MaterialParameter value)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            for (int index = 0; index < _parameters.Count; index++)
                if (_parameters[index].Name == value.Name) { _parameters[index] = value; return; }
            _parameters.Add(value);
        }
    }
    public override IEnumerable<Guid> References()
    {
        yield return Shader.Value;
        yield return Parent.Value;
        foreach (MaterialParameter value in _parameters)
            if (value.Kind == MaterialParameterKind.Texture) yield return value.Texture.Value;
    }

    public static void Write(ref BinaryDataWriter writer, MaterialBlock value)
    {
        writer.WriteGuid(value.Shader.Value);
        writer.WriteGuid(value.Parent.Value);
        writer.WriteInt32(value._parameters.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (MaterialParameter parameter in value._parameters)
        {
            if (!names.Add(parameter.Name)) throw new InvalidDataException($"Duplicate material parameter '{parameter.Name}'.");
            MaterialParameter.Encode(ref writer, parameter);
        }
    }
    public static MaterialBlock Read(ref BinaryDataReader reader)
    {
        var value = new MaterialBlock();
        try { ReadInto(ref reader, value); return value; }
        catch (Exception error)
        {
            try { value.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }
    public static void ReadInto(ref BinaryDataReader reader, MaterialBlock target)
    {
        target.Shader = new(reader.ReadGuid());
        target.Parent = new(reader.ReadGuid());
        int count = reader.ReadCollectionCount("material parameters", Unsafe.SizeOf<MaterialParameter>(), 0);
        target._parameters.Clear();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            MaterialParameter value = MaterialParameter.Decode(ref reader);
            if (!names.Add(value.Name)) throw new InvalidDataException($"Duplicate material parameter '{value.Name}'.");
            target._parameters.Add(value);
        }
    }
    public static async ValueTask<MaterialBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new MaterialBlock();
        try
        {
            await source.ReadIntoAsync(result, cancellationToken).ConfigureAwait(false);
            result.AttachSource(source);
            return result;
        }
        catch (Exception error)
        {
            try { result.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }
    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        writer.WriteMetadata(BlockTypeId, this);
        await WriteChunksAsync(writer, cancellationToken).ConfigureAwait(false);
    }
}
