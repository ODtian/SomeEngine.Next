using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceContactMatches
{
    private readonly List<int> _newInputs = [];
    private readonly List<int> _removedParameterRows = [];

    private async ValueTask<QueueCompletion> SubmitAsync(PhysicsStepContext context)
    {
        QueueCompletion completion = Submit(in context);
        if (!_detection.ReadsContactChanges) return completion;
        await _backend.WaitAsync(completion).ConfigureAwait(false);
        if (_inputsAreChanges)
        {
            int added, removed;
            using (MappedBuffer mapping = _backend.Map(_changeCounts, MapType.Read, BufferRange.Whole))
            {
                mapping.Invalidate(mapping.Range);
                ReadOnlySpan<uint> counts = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
                added = (int)counts[0]; removed = (int)counts[1];
            }
            ulong referenceBytes = _identityReferences ? (ulong)added * 16 : 0;
            ulong bytes = (ulong)(added + removed) * 4 + referenceBytes;
            if (bytes != 0)
            {
                if (_changesReadback.Info.Size < bytes)
                {
                    _changesReadback.Dispose();
                    _changesReadback = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopyDestination), MemoryType.Readback);
                }
                _backend.Begin(_commands, new(0, 0, 8));
                foreach (SomeEngine.Graphics.Buffer buffer in new[] { _newInputRows, _newReferences, _removedRows })
                    _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
                        ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                if (added != 0) _backend.CopyBuffer(_commands, new(_newInputRows, 4, _changesReadback, 0, (ulong)added * 4));
                if (referenceBytes != 0) _backend.CopyBuffer(_commands, new(_newReferences, 0, _changesReadback, (ulong)added * 4, referenceBytes));
                if (removed != 0) _backend.CopyBuffer(_commands, new(_removedRows, 4, _changesReadback, (ulong)added * 4 + referenceBytes, (ulong)removed * 4));
                foreach (SomeEngine.Graphics.Buffer buffer in new[] { _newInputRows, _newReferences, _removedRows })
                    _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                        ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
                using RecordedCommands recorded = _backend.End(_commands);
                completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
                await _backend.WaitAsync(completion).ConfigureAwait(false);
            }
            AcceptChanges(added, removed);
        }
        else
        {
            AcceptMatches();
            _newInputs.Clear();
            for (int index = 0; index < _detection.ContactCount; index++)
                if (_values[index].Point == Entity.Null) _newInputs.Add(index);
        }
        if (_identityReferences || _newInputs.Count == 0) return completion;
        return _detection.SubmitContactChanges(_newInputs);
    }

    private void AcceptChanges(int added, int removed)
    {
        ChangesNumber++;
        _newInputs.Clear(); _removedPoints.Clear(); _removedParameterRows.Clear();
        if (added + removed == 0) return;
        int referenceWords = _identityReferences ? added * 4 : 0;
        using MappedBuffer mapping = _backend.Map(_changesReadback, MapType.Read, new(0, (ulong)(added + removed + referenceWords) * 4));
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
        foreach (uint input in values[..added]) _newInputs.Add((int)input);
        if (!_identityReferences) _newInputs.Sort();
        foreach (uint row in values[(added + referenceWords)..])
        {
            _removedPoints.Add(_rowEntities[row]);
            _removedParameterRows.Add((int)row);
            ReleaseRow((int)row);
        }
    }

    private void AcceptMatches()
    {
        _removedPoints.Clear(); _removedParameterRows.Clear(); _inputRows.Clear();
        int rows = Math.Max(_rowCount, _parameters.RowCount);
        if (_rowReferences.Length < rows) Array.Resize(ref _rowReferences, rows);
        Array.Clear(_rowReferences);
        ChangesNumber++;
        if (_values.Length < _detection.ContactCount) Array.Resize(ref _values, Math.Max(_detection.ContactCount, _values.Length * 2));
        if (_detection.ContactCount != 0)
        {
            using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, new(0, (ulong)_detection.ContactCount * 16));
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<MatchedContact> values = MemoryMarshal.Cast<byte, MatchedContact>(mapping.Bytes);
            int output = 0;
            for (int pair = 0; pair < (_detection.ReadsContactChanges ? 1 : _detection.Count); pair++)
                for (int point = 0; point < (_detection.ReadsContactChanges ? _detection.ContactCount : _detection.GetContactCount(pair)); point++)
                {
                    int input = _detection.ReadsContactChanges ? point : _detection.GetContactOffset(pair) + point;
                    MatchedContact value = values[input];
                    _values[output++] = value; _inputRows.Add(input);
                    if (value.Point != Entity.Null) _rowReferences[value.ParameterIndex]++;
                }
        }
        for (int row = 0; row < _rowCount; row++)
            if (_rowEntities[row] != Entity.Null && _rowReferences[row] == 0)
            {
                _removedPoints.Add(_rowEntities[row]); _removedParameterRows.Add(row);
                RemoveIdentity(row);
                _parameters.ReleaseRow(row);
            }
        for (int input = 0; input < _detection.ContactCount; input++)
            if (_values[input].Point != Entity.Null) StoreIdentity((int)_values[input].ParameterIndex, _values[input].Point);
    }

    public WriteDeviceMatchedCollisionContacts<TOutput> WriteContacts<TOutput>(TOutput output,
        ReadOnlyMemory<JobResourceAccess> outputAccesses) where TOutput : struct, ICollisionContactOutput => new(this, output, outputAccesses);

    internal JobHandle ScheduleWrite<TOutput>(in PhysicsStepContext context, TOutput output, ReadOnlyMemory<JobResourceAccess> outputAccesses, JobHandle dependency)
        where TOutput : struct, ICollisionContactOutput
    {
        if (!_detection.ReadsContactChanges) return _detection.WriteContacts(output, outputAccesses).Schedule(in context, dependency);
        JobResourceAccess[] accesses = [ReadAccess, .. _detection.GetReadbackAccesses(_scheduler), .. _pointAccesses, .. outputAccesses.Span];
        return _scheduler.Schedule(new WriteContactRows<TOutput>(this, output), accesses, dependency);
    }
    private readonly struct WriteContactRows<TOutput>(DeviceContactMatches matches, TOutput output) : IJob where TOutput : struct, ICollisionContactOutput
    {
        public void Execute()
        {
            var write = new WriteRows<TOutput>(matches, output);
            if (matches.InputsAreChanges) write.Write(default);
            else matches._world.ExecuteReadQuery(matches._pointRows, ref write,
                static (scoped QueryCursor rows, ref WriteRows<TOutput> state) => state.Write(rows));
        }
    }
    private readonly ref struct WriteRows<TOutput>(DeviceContactMatches matches, TOutput output) where TOutput : struct, ICollisionContactOutput
    {
        internal void Write(scoped QueryCursor rows)
        {
            int changes = matches._newInputs.Count;
            using MappedBuffer? mapping = changes == 0 ? null : matches._detection.MapContactChanges();
            ReadOnlySpan<byte> bytes = mapping is null ? [] : mapping.Value.Bytes;
            int bodyCountOffset = changes * (int)DeviceCollisionDetection.ContactStride;
            ReadOnlySpan<CollisionContact> contacts = MemoryMarshal.Cast<byte, CollisionContact>(bytes[..bodyCountOffset]);
            ReadOnlySpan<uint> counts = matches._detection.ContactBodies is null ? []
                : MemoryMarshal.Cast<byte, uint>(bytes.Slice(bodyCountOffset, changes * 4));
            ReadOnlySpan<DeviceContactBody> endpoints = matches._detection.ContactBodies is null ? []
                : MemoryMarshal.Cast<byte, DeviceContactBody>(bytes[(bodyCountOffset + changes * 4)..]);
            int changed = 0;
            Span<ContactBody> first = stackalloc ContactBody[3], second = stackalloc ContactBody[3];
            int inputCount = matches.InputsAreChanges ? changes : matches._detection.ContactCount;
            for (int item = 0; item < inputCount; item++)
            {
                int input = matches.InputsAreChanges ? matches._newInputs[item] : item;
                Entity point = matches.InputsAreChanges ? Entity.Null : matches._values[input].Point;
                if (point != Entity.Null)
                {
                    rows.TryGetRow(point, out QueryRow row);
                    ref readonly var ends = ref row.ReadRef<UndirectedRelationEndpoints<ContactPoint>>();
                    ref readonly ContactPoint previous = ref row.ReadRef<ContactPoint>();
                    var retained = new CollisionContact { First = ends.EndpointA, Second = ends.EndpointB,
                        WorldPoint = previous.WorldPoint, Normal = previous.Normal, Separation = previous.Separation };
                    output.Add(in retained, [], []);
                    continue;
                }
                int change = changed++;
                ref readonly CollisionContact contact = ref contacts[change];
                if (matches._detection.ContactBodies is null)
                {
                    first[0] = new(contact.First, contact.FirstLocalPoint, 1);
                    second[0] = new(contact.Second, contact.SecondLocalPoint, 1);
                    output.Add(in contact, first[..1], second[..1]);
                    continue;
                }
                ReadOnlySpan<DeviceContactBody> bodies = endpoints.Slice(change * matches._detection.Capacity.BodiesPerContact, (int)counts[change]);
                for (int index = 0; index < contact.FirstBodyCount; index++) first[index] = bodies[index].Body;
                int secondCount = bodies.Length - contact.FirstBodyCount;
                for (int index = 0; index < secondCount; index++) second[index] = bodies[contact.FirstBodyCount + index].Body;
                output.Add(in contact, first[..contact.FirstBodyCount], second[..secondCount]);
            }
        }
    }
}

public readonly struct WriteDeviceMatchedCollisionContacts<TOutput>(DeviceContactMatches matches, TOutput output,
    ReadOnlyMemory<JobResourceAccess> outputAccesses) : IPhysicsStage where TOutput : struct, ICollisionContactOutput
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => matches.ScheduleWrite(in context, output, outputAccesses, dependency);
}
