namespace SomeEngine.ECS.Comparison;

/// <summary>A three-column callback shared by the reference libraries' native query paths.</summary>
internal interface IColumnPass<T1, T2, T3>
{
    void Execute(Span<T1> first, ReadOnlySpan<T2> second, ReadOnlySpan<T3> third);
}

/// <summary>A pass value that a generated job updates through its declared resource write.</summary>
internal sealed class JobResult<T> where T : struct
{
    internal T Value;
}
