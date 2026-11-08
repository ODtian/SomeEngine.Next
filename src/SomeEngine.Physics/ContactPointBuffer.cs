using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reusable derived detection output, with flattened weighted endpoint ranges.</summary>
public sealed class ContactPointBuffer : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private readonly ContactWrites _output = new();
    private ContactWrites[] _partitions = [];
    private int _partitionCount;
    internal bool WritingPartitions { get; private set; }

    public ContactPointBuffer(Scheduler scheduler, int contactCapacity, int bodyCapacity)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResourceToken("Detected contact points");

    }

    public int Count => _output.Count;
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    public JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);

    public void Clear() => _output.Clear();

    internal void BeginParallelWrites() { _partitionCount = 0; WritingPartitions = true; }
    internal int ReservePartitions(int count)
    {
        int first = _partitionCount;
        _partitionCount += count;
        if (_partitions.Length < _partitionCount)
        {
            int previous = _partitions.Length;
            Array.Resize(ref _partitions, Math.Max(_partitionCount, previous * 2));
            for (int index = previous; index < _partitions.Length; index++) _partitions[index] = new();
        }
        for (int index = first; index < _partitionCount; index++) _partitions[index].Clear();
        return first;
    }
    internal void CompleteParallelWrites()
    {
        WritingPartitions = false;
        for (int index = 0; index < _partitionCount; index++)
        {
            ContactWrites part = _partitions[index];
            for (int point = 0; point < part.Count; point++)
            {
                ref readonly CollisionContact contact = ref part.Read(point).Contact;
                ReadOnlySpan<ContactBody> bodies = part.Bodies(point);
                _output.Add(in contact, bodies[..contact.FirstBodyCount], bodies[contact.FirstBodyCount..]);
            }
        }
    }

    internal void Add(int partition, in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second)
    {
        if (partition < 0) Add(in contact, first, second);
        else _partitions[partition].Add(in contact, first, second);
    }

    private sealed class ContactWrites
    {
        private const int ContactsPerPage = 1024;
        private readonly List<ContactPage> _pages = [];
        internal int Count;
        internal void Clear() => Count = 0;
        internal ref readonly ContactDetectionRange Read(int index) => ref _pages[index / ContactsPerPage].Contacts[index % ContactsPerPage];
        internal ReadOnlySpan<ContactBody> Bodies(int index)
        {
            ContactPage page = _pages[index / ContactsPerPage];
            ref readonly ContactDetectionRange contact = ref page.Contacts[index % ContactsPerPage];
            return page.Bodies.AsSpan(contact.BodyStart, contact.BodyCount);
        }
        internal void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second)
        {
            int pageIndex = Count / ContactsPerPage, point = Count % ContactsPerPage;
            if (pageIndex == _pages.Count) _pages.Add(new());
            ContactPage page = _pages[pageIndex];
            if (point == 0) page.BodyCount = 0;
            int count = first.Length + second.Length;
            if (page.Bodies.Length < page.BodyCount + count)
                Array.Resize(ref page.Bodies, Math.Max(page.BodyCount + count, Math.Max(ContactsPerPage * 2, page.Bodies.Length * 2)));
            CollisionContact value = contact; value.FirstBodyCount = first.Length;
            page.Contacts[point] = new(value, page.BodyCount, count);
            first.CopyTo(page.Bodies.AsSpan(page.BodyCount)); second.CopyTo(page.Bodies.AsSpan(page.BodyCount + first.Length));
            page.BodyCount += count;
            Count++;
        }
        private sealed class ContactPage
        {
            internal readonly ContactDetectionRange[] Contacts = new ContactDetectionRange[ContactsPerPage];
            internal ContactBody[] Bodies = [];
            internal int BodyCount;
        }
    }

    internal void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
        _output.Add(in contact, first, second);

    internal ref readonly ContactDetectionRange Read(int index) => ref _output.Read(index);
    internal ReadOnlySpan<ContactBody> Bodies(int index) => _output.Bodies(index);

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
}

internal readonly struct ContactDetectionRange(CollisionContact contact, int bodyStart, int bodyCount)
{
    internal readonly CollisionContact Contact = contact;
    internal readonly int BodyStart = bodyStart;
    internal readonly int BodyCount = bodyCount;
}

public struct ContactPointOutput(ContactPointBuffer contacts) : ICollisionContactOutput
{
    private int _partition = -1, _firstPartition;
    public bool SupportsParallel => contacts.WritingPartitions;
    public void BeginParallel(int partitions) => _firstPartition = contacts.ReservePartitions(partitions);
    public void SelectPartition(int partition) => _partition = _firstPartition + partition;
    public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
        contacts.Add(_partition, in contact, first, second);
}
