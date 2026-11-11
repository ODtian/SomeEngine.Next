namespace SomeEngine.ECS.Relations;

public interface IRelationEnumerable<TRow>
{
    /// <summary>Returns true on completion and false when the output requests a stop.</summary>
    bool Enumerate<TOutput>(ref TOutput output)
        where TOutput : struct, IRelationOutput<TRow>, allows ref struct;
}

public interface IRelationOutput<TRow>
{
    /// <summary>Returns true to continue enumeration and false to stop.</summary>
    bool Add(in TRow row);
}
