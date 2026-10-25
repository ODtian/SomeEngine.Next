using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reusable derived detection output, with flattened weighted endpoint ranges.</summary>
public sealed class ContactPointBuffer : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private ContactDetectionRange[] _contacts;
    private ContactBody[] _bodies;
    private int _bodyCount;

    public ContactPointBuffer(Scheduler scheduler, int contactCapacity, int bodyCapacity)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResourceToken("Detected contact points");
        _contacts = new ContactDetectionRange[contactCapacity];
        _bodies = new ContactBody[bodyCapacity];
    }

    public int Count { get; private set; }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    public JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);

    public void Clear() => Count = _bodyCount = 0;

    internal void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second)
    {
        if (_contacts.Length <= Count) Array.Resize(ref _contacts, Math.Max(Count + 1, _contacts.Length * 2));
        int count = first.Length + second.Length;
        if (_bodies.Length < _bodyCount + count) Array.Resize(ref _bodies, Math.Max(_bodyCount + count, _bodies.Length * 2));
        CollisionContact value = contact;
        value.FirstBodyCount = first.Length;
        _contacts[Count++] = new(value, _bodyCount, count);
        first.CopyTo(_bodies.AsSpan(_bodyCount));
        second.CopyTo(_bodies.AsSpan(_bodyCount + first.Length));
        _bodyCount += count;
    }

    internal ref readonly ContactDetectionRange Read(int index) => ref _contacts[index];
    internal ReadOnlySpan<ContactBody> Bodies(int index) => _bodies.AsSpan(_contacts[index].BodyStart, _contacts[index].BodyCount);

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
}

internal readonly struct ContactDetectionRange(CollisionContact contact, int bodyStart, int bodyCount)
{
    internal readonly CollisionContact Contact = contact;
    internal readonly int BodyStart = bodyStart;
    internal readonly int BodyCount = bodyCount;
}

public readonly struct ContactPointOutput(ContactPointBuffer contacts) : ICollisionContactOutput
{
    public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
        contacts.Add(in contact, first, second);
}
