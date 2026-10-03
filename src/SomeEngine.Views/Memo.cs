using SomeEngine.Core.Diagnostics;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Views;

/// <summary>A view-owned value recomputed on read when its dependencies change.</summary>
public sealed class Memo<T> : VersionCell
{
    private readonly Func<T> _compute;
    private readonly Computation _computation;
    private T _value = default!;
    private bool _hasValue;
    private bool _evaluating;

    internal Memo(Func<T> compute)
    {
        _compute = compute;
        _computation = Scope.AddComputation(Recompute, evaluatesOnRead: true);
    }

    /// <summary>Gets the current result and records this Memo as the caller's dependency.</summary>
    public T Value
    {
        get
        {
            try
            {
                Refresh(Scope.Frame);
            }
            finally
            {
                Scope.Depend(this);
            }
            return _value;
        }
    }

    internal override Exception? Error => _computation.Error;

    internal override void Refresh(ulong frame)
    {
        DevelopmentCheck.Require(!_computation.Released && _computation.World.IsAlive(_computation.Entity),
            "Memo reads require the view and creating calculation to remain active.");
        DevelopmentCheck.Require(!_evaluating, "A Memo calculation must finish before reading itself.");
        _evaluating = true;
        try
        {
            if ((!_hasValue && Error is null) || _computation.HasChanges(frame))
                _computation.Execute(frame == 0 ? _computation.Frame : frame);
            else if (Error is { } error)
                ExceptionDispatchInfo.Throw(error);
        }
        finally
        {
            _evaluating = false;
        }
    }

    private void Recompute()
    {
        T value = _compute();
        if (!_hasValue || !EqualityComparer<T>.Default.Equals(_value, value))
        {
            _value = value;
            Advance();
        }
        _hasValue = true;
    }
}
