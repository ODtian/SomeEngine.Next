using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Systems.Tests;

internal readonly struct CallbackData<T> : IDisposable where T : class
{
    private readonly GCHandle _data;

    public CallbackData(T data) => _data = GCHandle.Alloc(data);

    public T Value => (T)_data.Target!;

    public void Dispose()
    {
        try
        {
            (Value as IDisposable)?.Dispose();
        }
        finally
        {
            _data.Free();
        }
    }
}
