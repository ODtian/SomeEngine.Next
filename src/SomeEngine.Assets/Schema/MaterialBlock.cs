using System.Runtime.CompilerServices;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

/// <summary>Material type, references and local parameter overrides, encoded with the material parameter codec.</summary>
[AssetBlock("SomeEngine.Assets.MaterialBlock")]
[BinarySerializable(LogicalName = "SomeEngine.Assets.MaterialBlock")]
public sealed partial class MaterialBlock : AssetPageBlock, IAssetBlockWriter,
    IBinarySerializable<MaterialBlock>, IBinaryObjectReader<MaterialBlock>
{
    private readonly PageBuffer _values = null!;
    private readonly PageList<MaterialParameter> _parameters = null!;
    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Assets.MaterialBlock");
    public static ulong SchemaFingerprint => 0x4D41544276310001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public MaterialBlock() : this(PageArena.Shared) { }
    public MaterialBlock(PageArena pages, Guid materialType = default) : base(pages)
    {
        try
        {
            _values = new(Pages, 48, Access);
            _parameters = new(Pages, Access);
            _values.Write(0, materialType);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }

    private MaterialBlock(MaterialBlock source, PageAccess? access = null) : base(source, access)
    {
        try
        {
            _values = source._values.Duplicate(Access);
            _parameters = source._parameters.Duplicate(Access);
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }

    public Guid MaterialTypeId => _values.Read<Guid>(0);
    public AssetId<ShaderAsset> Shader
    {
        get => new(_values.Read<Guid>(16));
        set => _values.Write(16, value.Value);
    }
    public AssetId<MaterialAsset> Parent
    {
        get => new(_values.Read<Guid>(32));
        set => _values.Write(32, value.Value);
    }
    public IList<MaterialParameter> Parameters => _parameters;

    public override MaterialBlock Duplicate()
    {
        lock (Access.SyncRoot) { RequireIdle(); return new(this); }
    }
    public override MaterialBlock Duplicate(PageAccess access)
    {
        lock (Access.SyncRoot) { RequireIdle(); return new(this, access); }
    }
    protected override void ReleaseContent()
    {
        List<Exception>? errors = null;
        try { _values?.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        try { _parameters?.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        try { base.ReleaseContent(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null)
        {
            if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            throw new AggregateException(errors);
        }
    }

    public bool HasOverride(string name) => GetParameter(name).HasValue;
    public bool Reset(string name)
    {
        lock (Access.SyncRoot)
        {
            RequireIdle();
            for (int index = 0; index < _parameters.Count; index++)
                if (_parameters[index].Name == name) { _parameters.RemoveAt(index); return true; }
            return false;
        }
    }
    public MaterialParameter? GetParameter(string name)
    {
        lock (Access.SyncRoot)
        {
            RequireIdle();
            foreach (MaterialParameter value in _parameters) if (value.Name == name) return value;
            return null;
        }
    }
    public void SetParameter(MaterialParameter value)
    {
        lock (Access.SyncRoot)
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
        writer.WriteGuid(value.MaterialTypeId);
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
        Guid type = reader.ReadGuid();
        if (target.MaterialTypeId != Guid.Empty && target.MaterialTypeId != type)
            throw new InvalidDataException("The block contains a different concrete material type.");
        target._values.Write(0, type);
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
        var result = new MaterialBlock(source.Pages);
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
