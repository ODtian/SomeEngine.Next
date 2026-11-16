using SomeEngine.ECS;

namespace SomeEngine.ECS.Benchmarks;

internal static partial class EcsBenchmarkSuite
{
    private readonly struct DefaultPositionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int state)
        {
            var position = default(Position);
            view.Write(in position);
        }
    }

    private readonly struct SequentialPositionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int state)
        {
            var position = new Position(view.Index, view.Index + 1);
            view.Write(in position);
        }
    }

    private readonly struct CapturedPositionWriter : IBundleWriter<EntityCaptureState>
    {
        public void Write(BundleWriteView view, in EntityCaptureState state)
        {
            var position = new Position(view.Index, view.Index + 1);
            view.Write(in position);
            if (view.Index < state.Entities.Length)
                state.Entities[view.Index] = view.Entity;
        }
    }

    private readonly struct JobComponentsWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int state)
        {
            var position = new BenchmarkJobPosition
            {
                X = view.Index,
                Y = -view.Index,
            };
            var velocity = new BenchmarkJobVelocity
            {
                X = view.Index % 7 + 1,
                Y = 2,
            };
            view.Write(in position);
            view.Write(in velocity);
        }
    }

    private readonly struct FilterComponentsWriter : IBundleWriter<FilterPopulateState>
    {
        public void Write(BundleWriteView view, in FilterPopulateState state)
        {
            var position = new FilterPosition(view.Index);
            var visibility = new FilterVisibility(view.Index);
            view.Write(in position);
            view.Write(in visibility);
            state.Entities[view.Index] = view.Entity;
        }
    }

    private readonly struct StorageBufferWriter : IBundleWriter<StoragePopulateState>
    {
        public void Write(BundleWriteView view, in StoragePopulateState state)
        {
            int item = state.Offset + view.Index;
            var index = new StorageIndex(item % IndexBucketCount);
            var bucket = new StorageBucket(item % SharedBucketCount);
            var sparse = new StorageSparse(item);
            view.Write(in bucket);
            view.Write(in index);
            view.Write(in sparse);
            ReadOnlyMemory<BenchmarkBufferElement> buffer = InitialBufferValues.AsMemory();
            view.WriteBuffer(in buffer);
        }
    }

    private readonly struct StorageSparseWriter : IBundleWriter<StoragePopulateState>
    {
        public void Write(BundleWriteView view, in StoragePopulateState state)
        {
            int item = state.Offset + view.Index;
            var index = new StorageIndex(item % IndexBucketCount);
            var bucket = new StorageBucket(item % SharedBucketCount);
            var sparse = new StorageSparse(item);
            view.Write(in bucket);
            view.Write(in index);
            view.Write(in sparse);
        }
    }

    private readonly struct DurablePositionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int state)
        {
            var position = new BenchmarkDurablePosition(view.Index, view.Index + 1);
            view.Write(in position);
        }
    }

    private readonly struct AddedPositionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int state)
            => view.Write(new Position(state, -state));
    }
}
