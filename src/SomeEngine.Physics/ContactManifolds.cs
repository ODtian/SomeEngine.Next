using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>CPU contact columns and one concrete solver's persistent numerical values.</summary>
public sealed partial class ContactManifolds<TState> : IDisposable where TState : unmanaged
{
    private readonly TState _newPointState;
    private TState[] _states = [];
    public ContactManifolds(Scheduler scheduler, in TState newPointState)
        => (Columns, _newPointState) = (new ContactColumns(scheduler), newPointState);
    public ContactColumns Columns { get; }
    public PhysicsContacts Contacts => Columns.Contacts;
    public JobResourceAccess ReadAccess => Columns.ReadAccess;
    public JobResourceAccess WriteAccess => Columns.WriteAccess;
    public Span<TState> States { get { Columns.ValidateWrite(); return _states.AsSpan(0, Columns.PointCount); } }
    public ReadOnlySpan<TState> ReadOnlyStates { get { Columns.ValidateRead(); return _states.AsSpan(0, Columns.PointCount); } }
    public Span<ContactMaterial> Materials => Columns.Materials;
    public Span<ContactResponse> Responses => Columns.Responses;
    public Span<Vector3> Normals => Columns.Normals;
    public Span<float> Separations => Columns.Separations;
    public void Resize(int pairs, int points, int bodyLinks)
    {
        Columns.Resize(pairs, points, bodyLinks);
        ContactColumns.Grow(ref _states, points);
    }
    public void InitializeState(int point) { Columns.ValidateWrite(); _states[point] = _newPointState; }
    public void Publish(in PhysicsStepResult step) => Columns.Publish(step);
    public void Dispose() { Columns.Dispose(); _states = []; ClearHistory(); }
}
