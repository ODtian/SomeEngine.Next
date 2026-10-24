using System.Diagnostics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>Changes a value while its State holds exclusive write access.</summary>
public delegate void Change<T>(ref T value);

/// <summary>Changes a value using an argument while its State holds exclusive write access.</summary>
public delegate void Change<T, TArg>(ref T value, TArg arg);

/// <summary>A complete value shared by views and writers on any thread.</summary>
public sealed class State<T> : VersionCell
{
    private T _value;
#if DEBUG
    private int _writingThread;
#endif

    public State(T initial = default!) => _value = initial;

    /// <summary>Reads a complete value and records its version in the current computation.</summary>
    public T Value
    {
        get
        {
            CheckAccess();
            SpinWait wait = default;
            while (true)
            {
                uint version = Volatile.Read(ref Version);
                if ((version & 1) != 0)
                {
                    wait.SpinOnce();
                    continue;
                }

                T value = _value;
                // Finish copying every field before checking for a concurrent write.
                Thread.MemoryBarrier();
                if (version == Volatile.Read(ref Version))
                {
                    Scope.Depend(this, version);
                    return value;
                }
                wait.SpinOnce();
            }
        }
        set => Update(value, static (ref T current, T next) => current = next);
    }

    /// <summary>Atomically reads, changes and writes the value.</summary>
    public void Update(Change<T> change)
    {
        DevelopmentCheck.Require(change is not null, "State.Update requires a change callback.");
        Update(change, static (ref T value, Change<T> callback) => callback(ref value));
    }

    /// <summary>Atomically changes the value with an argument, preserving equal values and their versions.</summary>
    public void Update<TArg>(TArg arg, Change<T, TArg> change)
    {
        DevelopmentCheck.Require(change is not null, "State.Update requires a change callback.");
        uint version = BeginWrite();
        try
        {
            T value = _value;
            change(ref value, arg);
            if (!EqualityComparer<T>.Default.Equals(_value, value))
            {
                _value = value;
                version = unchecked(version + 2);
            }
        }
        finally
        {
#if DEBUG
            _writingThread = 0;
#endif
            Volatile.Write(ref Version, version);
        }
    }

    private uint BeginWrite()
    {
        CheckAccess();
        SpinWait wait = default;
        while (true)
        {
            uint version = Volatile.Read(ref Version);
            if ((version & 1) == 0 &&
                Interlocked.CompareExchange(ref Version, unchecked(version + 1), version) == version)
            {
#if DEBUG
                _writingThread = Environment.CurrentManagedThreadId;
#endif
                return version;
            }
            wait.SpinOnce();
        }
    }

    [Conditional("DEBUG")]
    private void CheckAccess()
    {
#if DEBUG
        DevelopmentCheck.Require(_writingThread != Environment.CurrentManagedThreadId,
            "A State.Update callback must access the same State through its ref value.");
#endif
    }
}
