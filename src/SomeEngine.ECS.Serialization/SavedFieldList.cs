namespace SomeEngine.ECS.Serialization;

/// <summary>How a generated serializer encodes one field.</summary>
public enum SavedFieldKind : byte
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

/// <summary>One field of a value written by a generated serializer.</summary>
/// <param name="Id">The field's saved identity: its <see cref="SaveNameAttribute"/> value, or its name.</param>
/// <param name="Nested">The fields of the nested value when <paramref name="Kind"/> is <see cref="SavedFieldKind.Nested"/>.</param>
public sealed record SavedField(string Id, SavedFieldKind Kind, SavedFieldList? Nested = null);

/// <summary>The fields of a value type, in the order its generated serializer writes them.</summary>
/// <remarks>Saves record this list, so a later version of the type can read an older save field by field.</remarks>
/// <param name="Id">The type's <see cref="SaveIdAttribute"/> identity.</param>
/// <param name="Hash">Changes whenever a field is added, removed, renamed or changes kind.</param>
public sealed record SavedFieldList(Guid Id, ulong Hash, SavedField[] Fields);

/// <summary>A serializer that publishes the fields it writes.</summary>
public interface IFieldSerializer
{
    SavedFieldList FieldList { get; }
}

/// <summary>A serializer generated for a <see cref="SaveIdAttribute"/> type.</summary>
public interface IFieldSerializer<T> : IValueSerializer<T>, IFieldSerializer where T : struct
{
    static abstract SavedFieldList Schema { get; }
}

/// <summary>
/// Where each field of an older saved value goes in the current type: the index of the current field
/// with the same identity and kind, or -1 when the field was removed or changed kind and is skipped.
/// Current fields that the save does not have keep the value the type's constructor gives them.
/// </summary>
public sealed class FieldMapping
{
    private readonly SavedField[] _saved;
    private readonly int[] _targets;
    private readonly FieldMapping?[] _nested;

    internal FieldMapping(SavedFieldList saved, SavedFieldList current)
    {
        SavedHash = saved.Hash;
        _saved = saved.Fields;
        _targets = new int[_saved.Length];
        _nested = new FieldMapping?[_saved.Length];
        for (int field = 0; field < _saved.Length; field++)
        {
            SavedField savedField = _saved[field];
            _targets[field] = Array.FindIndex(current.Fields,
                candidate => candidate.Id == savedField.Id && candidate.Kind == savedField.Kind);
            if (_targets[field] >= 0 && savedField.Nested is { } savedNested
                && current.Fields[_targets[field]].Nested is { } currentNested && savedNested.Hash != currentNested.Hash)
                _nested[field] = new FieldMapping(savedNested, currentNested);
        }
    }

    internal ulong SavedHash { get; }

    /// <summary>The number of fields in the saved value.</summary>
    public int Count => _saved.Length;

    /// <summary>The current field that receives saved field <paramref name="field"/>, or -1.</summary>
    public int TargetOf(int field) => _targets[field];

    /// <summary>The map for a nested saved field whose nested type changed, or null when it did not.</summary>
    public FieldMapping? NestedMap(int field) => _nested[field];

    /// <summary>Reads past saved field <paramref name="field"/> without keeping it.</summary>
    public void Skip(ref SaveReader reader, int field) => Skip(ref reader, _saved[field]);

    private static void Skip(ref SaveReader reader, SavedField field)
    {
        switch (field.Kind)
        {
            case SavedFieldKind.Boolean or SavedFieldKind.Byte or SavedFieldKind.SByte: reader.Skip(1); break;
            case SavedFieldKind.Int16 or SavedFieldKind.UInt16 or SavedFieldKind.Char: reader.Skip(2); break;
            case SavedFieldKind.Int32 or SavedFieldKind.UInt32 or SavedFieldKind.Single: reader.Skip(4); break;
            case SavedFieldKind.Int64 or SavedFieldKind.UInt64 or SavedFieldKind.Double
                or SavedFieldKind.Entity or SavedFieldKind.Enum: reader.Skip(8); break;
            case SavedFieldKind.Guid: reader.Skip(16); break;
            case SavedFieldKind.String: _ = reader.ReadString(); break;
            case SavedFieldKind.Nested:
                foreach (SavedField nested in field.Nested!.Fields) Skip(ref reader, nested);
                break;
            default: throw new InvalidDataException("A saved field has an unknown kind.");
        }
    }
}
