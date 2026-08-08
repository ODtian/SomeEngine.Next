using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Assets.Importers;

/// <summary>
/// Owns the source-generated COM wrappers and compiled metadata created by one Slang import. Slang
/// child objects may depend on their session and module without independently keeping every parent
/// alive, so GC finalizer order is not a valid lifetime policy. Owners are released once, in reverse
/// creation order, before the import returns its managed asset.
/// </summary>
internal sealed class SlangImportLifetime : IDisposable
{
    private readonly List<object> _objects = [];
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly Action<object>? _trackObserver;
    private bool _disposed;

    internal SlangImportLifetime(Action<object>? trackObserver = null)
    {
        _trackObserver = trackObserver;
    }

    internal T? Track<T>(T? value)
        where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (value is (ComObject or Metadata) && _seen.Add(value))
        {
            _objects.Add(value);
            _trackObserver?.Invoke(value);
        }
        return value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<Exception>? failures = null;
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            try
            {
                switch (_objects[i])
                {
                    case Metadata metadata:
                        metadata.Dispose();
                        break;
                    case ComObject wrapper:
                        wrapper.FinalRelease();
                        break;
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        _objects.Clear();
        _seen.Clear();
        if (failures is not null)
        {
            throw new AggregateException(
                "One or more Slang COM objects failed to release after an asset import.",
                failures);
        }
    }
}
