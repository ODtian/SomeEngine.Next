using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

internal sealed class ContactValues<TState> where TState : unmanaged
{
    internal ContactPair[] Pairs = [];
    internal ContactPoint[] Points = [];
    internal ContactBodyLink[][] Links = [];
    internal ContactMaterial[] Materials = [];
    internal ContactResponse[] Responses = [];
    internal TState[] States = [];
    internal readonly Dictionary<Entity, int[]> BodyPairs = [];
}

internal static class ContactTestData
{
    internal static ContactManifolds<TState> Create<TState>(Scheduler scheduler, Entity first, Entity second,
        ContactPoint point, ReadOnlySpan<ContactBodyLink> links, ContactMaterial material, TState state) where TState : unmanaged
    {
        var contacts = new ContactManifolds<TState>(scheduler, in state);
        Add(scheduler, contacts, first, second, point, links, material, state); return contacts;
    }
    internal static void Add<TState>(Scheduler scheduler, ContactManifolds<TState> contacts, Entity first, Entity second,
        ContactPoint point, ReadOnlySpan<ContactBodyLink> links, ContactMaterial material, TState state) where TState : unmanaged =>
        scheduler.Schedule(new AddPoint<TState>(contacts, first, second, point, links.ToArray(), material, state), [contacts.WriteAccess]).Complete();
    private readonly struct AddPoint<TState>(ContactManifolds<TState> contacts, Entity first, Entity second,
        ContactPoint point, ContactBodyLink[] links, ContactMaterial material, TState state) : IJob where TState : unmanaged
    {
        public void Execute()
        {
            ContactColumns columns = contacts.Columns;
            int pointIndex = columns.PointCount, bodyStart = columns.BodyLinkCount, pairCount = columns.PairCount;
            bool appendPair = pairCount == 0 || columns.Pairs[pairCount - 1].FirstGeometry != first || columns.Pairs[pairCount - 1].SecondGeometry != second;
            int pairIndex = appendPair ? pairCount : pairCount - 1;
            contacts.Resize(pairCount + (appendPair ? 1 : 0), pointIndex + 1, bodyStart + links.Length);
            columns.Pairs[pairIndex] = appendPair ? new(first, second, 0, 0, pointIndex, 1)
                : columns.Pairs[pairIndex] with { PointCount = columns.Pairs[pairIndex].PointCount + 1 };
            ContactPoint value = point; value.PairIndex = pairIndex; value.PointIndex = pointIndex - columns.Pairs[pairIndex].FirstPoint;
            columns.SetPoint(pointIndex, in value); columns.BodyStarts[pointIndex] = bodyStart; columns.BodyCounts[pointIndex] = links.Length;
            links.CopyTo(columns.BodyLinks[bodyStart..]); columns.Materials[pointIndex] = material; contacts.States[pointIndex] = state;
            columns.RebuildBodyPairs();
        }
    }
    internal static ContactValues<TState> Read<TState>(Scheduler scheduler, ContactManifolds<TState> contacts) where TState : unmanaged
    {
        var result = new ContactValues<TState>();
        scheduler.Schedule(new ReadValues<TState>(contacts, result), [contacts.ReadAccess]).Complete(); return result;
    }
    private readonly struct ReadValues<TState>(ContactManifolds<TState> contacts, ContactValues<TState> result) : IJob where TState : unmanaged
    {
        public void Execute()
        {
            ContactView view = contacts.Contacts.OpenView(); result.Pairs = view.Pairs.ToArray();
            result.Materials = view.Materials.ToArray(); result.Responses = view.Responses.ToArray(); result.States = contacts.ReadOnlyStates.ToArray();
            result.Points = new ContactPoint[view.PointCount]; result.Links = new ContactBodyLink[view.PointCount][];
            for (int point = 0; point < view.PointCount; point++)
            {
                result.Points[point] = view.GetPoint(point); result.Links[point] = view.BodyLinksOf(point).ToArray();
                foreach (ContactBodyLink link in result.Links[point])
                    if (!result.BodyPairs.ContainsKey(link.Body)) result.BodyPairs.Add(link.Body, view.PairsOf(link.Body).ToArray());
            }
        }
    }
    internal static void SetPoint<TState>(Scheduler scheduler, ContactManifolds<TState> contacts, int point, in ContactPoint value) where TState : unmanaged =>
        scheduler.Schedule(new WritePoint<TState>(contacts, point, value), [contacts.WriteAccess]).Complete();
    private readonly struct WritePoint<TState>(ContactManifolds<TState> contacts, int point, ContactPoint value) : IJob where TState : unmanaged
    { public void Execute() => contacts.Columns.SetPoint(point, in value); }
    internal static void SetState<TState>(Scheduler scheduler, ContactManifolds<TState> contacts, int point, in TState value) where TState : unmanaged =>
        scheduler.Schedule(new WriteState<TState>(contacts, point, value), [contacts.WriteAccess]).Complete();
    private readonly struct WriteState<TState>(ContactManifolds<TState> contacts, int point, TState value) : IJob where TState : unmanaged
    { public void Execute() => contacts.States[point] = value; }
    internal static void SetFlags<TState>(Scheduler scheduler, ContactManifolds<TState> contacts, int point, ContactPointFlags value) where TState : unmanaged =>
        scheduler.Schedule(new WriteFlags<TState>(contacts, point, value), [contacts.WriteAccess]).Complete();
    private readonly struct WriteFlags<TState>(ContactManifolds<TState> contacts, int point, ContactPointFlags value) : IJob where TState : unmanaged
    { public void Execute() => contacts.Columns.Flags[point] = value; }
    internal static void Clear<TState>(Scheduler scheduler, ContactManifolds<TState> contacts) where TState : unmanaged =>
        scheduler.Schedule(new ClearValues<TState>(contacts), [contacts.WriteAccess]).Complete();
    private readonly struct ClearValues<TState>(ContactManifolds<TState> contacts) : IJob where TState : unmanaged
    { public void Execute() { contacts.Resize(0, 0, 0); contacts.Columns.RebuildBodyPairs(); } }
}
