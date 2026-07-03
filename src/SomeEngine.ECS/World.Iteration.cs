namespace SomeEngine.ECS;

public partial class World
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void BeginIteration() => _iteration.Begin();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void EndIteration() => _iteration.End();

    internal bool IsIterating => _iteration.Active;

    internal void ThrowIfIterating()
    {
        _iteration.Throw();
    }
}

