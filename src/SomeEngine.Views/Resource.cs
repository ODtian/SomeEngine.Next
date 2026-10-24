namespace SomeEngine.Views;

/// <summary>A view's fetched value, loading state and request error.</summary>
public sealed class Resource<T> : VersionCell
{
    private readonly Lock _gate = new();
    private T _value = default!;
    private bool _loading;
    private Exception? _error;

    internal Resource() { }

    /// <summary>Reads the most recently completed value and records a dependency.</summary>
    public T Value
    {
        get
        {
            lock (_gate)
            {
                Scope.Depend(this);
                return _value;
            }
        }
    }

    /// <summary>Reads whether the current source is being fetched and records a dependency.</summary>
    public bool Loading
    {
        get
        {
            lock (_gate)
            {
                Scope.Depend(this);
                return _loading;
            }
        }
    }

    /// <summary>Reads the current request error and records a dependency.</summary>
    public Exception? Error
    {
        get
        {
            lock (_gate)
            {
                Scope.Depend(this);
                return _error;
            }
        }
    }

    internal void BeginLoad()
    {
        lock (_gate)
        {
            if (_loading && _error is null)
                return;
            _loading = true;
            _error = null;
            Advance();
        }
    }

    internal void CompleteLoad(T value)
    {
        lock (_gate)
        {
            _value = value;
            _loading = false;
            _error = null;
            Advance();
        }
    }

    internal void FailLoad(Exception error)
    {
        lock (_gate)
        {
            _loading = false;
            _error = error;
            Advance();
        }
    }
}
