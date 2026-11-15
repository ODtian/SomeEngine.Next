namespace SomeEngine.ECS.Serialization;

/// <summary>How a generated codec encodes one field.</summary>
public enum ValueFieldKind : byte
{
    Boolean,
    Byte,
    SByte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Char,
    Single,
    Double,
    Guid,
    String,
    Entity,
    Enum,
    Nested,
}

/// <summary>One field of a value written by a generated codec.</summary>
/// <param name="Id">The field's saved identity: its <see cref="SerializedFieldAttribute"/> value, or its name.</param>
/// <param name="Nested">The fields of the nested value when <paramref name="Kind"/> is <see cref="ValueFieldKind.Nested"/>.</param>
public sealed record ValueField(string Id, ValueFieldKind Kind, ValueSchema? Nested = null);

/// <summary>The fields of a value type, in the order its generated codec writes them.</summary>
/// <remarks>Saves record this list, so a later version of the type can read an older save field by field.</remarks>
/// <param name="Id">The type's <see cref="SerializableComponentAttribute"/> identity.</param>
/// <param name="Hash">Changes whenever a field is added, removed, renamed or changes kind.</param>
public sealed record ValueSchema(Guid Id, ulong Hash, ValueField[] Fields);

/// <summary>A codec generated for a <see cref="SerializableComponentAttribute"/> type.</summary>
public interface ISchemaValueCodec<T> : IDurableComponentCodec<T> where T : struct
{
    static abstract ValueSchema Schema { get; }
}

/// <summary>
/// Where each field of an older saved value goes in the current type: the index of the current field
/// with the same identity and kind, or -1 when the field was removed or changed kind and is skipped.
/// Current fields that the save does not have keep the value the type's constructor gives them.
/// </summary>
public sealed class SavedFieldMap
{
    private readonly ValueField[] _saved;
    private readonly int[] _targets;
    private readonly SavedFieldMap?[] _nested;

    internal SavedFieldMap(ValueSchema saved, ValueSchema current)
    {
        SavedHash = saved.Hash;
        _saved = saved.Fields;
        _targets = new int[_saved.Length];
        _nested = new SavedFieldMap?[_saved.Length];
        for (int field = 0; field < _saved.Length; field++)
        {
            ValueField savedField = _saved[field];
            _targets[field] = Array.FindIndex(current.Fields,
                candidate => candidate.Id == savedField.Id && candidate.Kind == savedField.Kind);
            if (_targets[field] >= 0 && savedField.Nested is { } savedNested
                && current.Fields[_targets[field]].Nested is { } currentNested && savedNested.Hash != currentNested.Hash)
                _nested[field] = new SavedFieldMap(savedNested, currentNested);
        }
    }

    internal ulong SavedHash { get; }

    /// <summary>The number of fields in the saved value.</summary>
    public int Count => _saved.Length;

    /// <summary>The current field that receives saved field <paramref name="field"/>, or -1.</summary>
    public int TargetOf(int field) => _targets[field];

    /// <summary>The map for a nested saved field whose nested type changed, or null when it did not.</summary>
    public SavedFieldMap? NestedMap(int field) => _nested[field];

    /// <summary>Reads past saved field <paramref name="field"/> without keeping it.</summary>
    public void Skip(ref DataReader reader, int field) => Skip(ref reader, _saved[field]);

    private static void Skip(ref DataReader reader, ValueField field)
    {
        switch (field.Kind)
        {
            case ValueFieldKind.Boolean or ValueFieldKind.Byte or ValueFieldKind.SByte: reader.Skip(1); break;
            case ValueFieldKind.Int16 or ValueFieldKind.UInt16 or ValueFieldKind.Char: reader.Skip(2); break;
            case ValueFieldKind.Int32 or ValueFieldKind.UInt32 or ValueFieldKind.Single: reader.Skip(4); break;
            case ValueFieldKind.Int64 or ValueFieldKind.UInt64 or ValueFieldKind.Double
                or ValueFieldKind.Entity or ValueFieldKind.Enum: reader.Skip(8); break;
            case ValueFieldKind.Guid: reader.Skip(16); break;
            case ValueFieldKind.String: _ = reader.ReadString(); break;
            case ValueFieldKind.Nested:
                foreach (ValueField nested in field.Nested!.Fields) Skip(ref reader, nested);
                break;
            default: throw new InvalidDataException("A saved field has an unknown kind.");
        }
    }
}
