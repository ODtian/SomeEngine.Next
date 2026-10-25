using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Frame;
using SomeEngine.Job;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection("Render job configuration")]
public sealed class InstanceSparseUpdateTests
{
    [Fact]
    public void RejectedGroupingLeavesTheCallerAbleToRunAnotherJob()
    {
        try
        {
            JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0, MaxCompletionStates = 1 });
            using var data = new InstanceData();
            Entity entity = data.World.CreateEntity(new BatchGroup(1));
            data.World.Add(entity, Current(1));
            data.World.Add(entity, Previous(11));
            QueryHandle query = data.World.Query(new QueryDefinitionBuilder()
                .Read<BatchGroup>().Read<RenderTransform>().Read<RenderPreviousTransform>().Build());
            try
            {
                var builder = new RenderInstanceBatchBuilder<int>(groupingPartitions: 1, rowsPerPacket: 1);
                var composer = new GroupComposer();
                int[] result = [0];
                data.Prepare.Add(new BatchWriter
                {
                    Update = (context, _) =>
                    {
                        Assert.Throws<InvalidOperationException>(() => builder.Update(context, query, composer));
                        Assert.Null(builder.Current);
                        JobSystem.Schedule(new WriteResult(result)).Complete();
                        Assert.Equal(17, result[0]);
                    }
                });

                data.PrepareFrame();
                Assert.Equal(17, result[0]);
            }
            finally
            {
                data.World.ReleaseQuery(query);
            }
        }
        finally
        {
            try { JobSystem.Shutdown(); }
            finally { JobSystem.Initialize(JobRuntimeConfig.Default); }
        }
    }

    [Fact]
    public void EditingOneGroupPreservesTheOtherGroupsValuesAndRevision()
    {
        using var data = new InstanceData();
        var composition = new RenderInstanceCompositionSystem<int, GroupComposer>(
            new QueryDefinitionBuilder().Read<BatchGroup>().Read<RenderTransform>()
                .Read<RenderPreviousTransform>().Build(),
            new GroupComposer(), groupingPartitions: 1, rowsPerPacket: 1);
        data.Prepare.Add(composition);
        data.Batches = () => composition.Current!.Groups.ToArray().Select(group => group.Batch).ToArray();
        Entity first = data.World.CreateEntity(new BatchGroup(1));
        data.World.Add(first, Current(1));
        data.World.Add(first, Previous(11));
        Entity second = data.World.CreateEntity(new BatchGroup(2));
        data.World.Add(second, Current(2));
        data.World.Add(second, Previous(12));
        data.ExecuteFrame();
        RenderInstanceBatch unchanged = composition.Current!.Groups.ToArray().Single(group => group.Key == 2).Batch;
        ulong revision = unchanged.ContentRevision;

        data.World.Replace(first, Current(9));
        data.ExecuteFrame();

        RenderInstanceBatch changed = composition.Current!.Groups.ToArray().Single(group => group.Key == 1).Batch;
        Assert.Equal(new[] { Current(9) }, data.Values(changed, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Current(2) }, data.Values(unchanged, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(12) }, data.Values(unchanged, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Same(unchanged, composition.Current.Groups.ToArray().Single(group => group.Key == 2).Batch);
        Assert.Equal(revision, unchanged.ContentRevision);
    }

    [Fact]
    public void UnwrittenOptionalColumnKeepsItsSharedBindingAndGpuValues()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindShared(CurrentProperty, Current(7));
            rows.BindPerInstance(PreviousProperty);
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        BufferRange changedValue = data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform, 1);
        BufferRange changedBinding = data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform);
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(PreviousProperty, 1, Previous(19));
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(7), Current(7) }, data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(19) }, data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Equal(changedValue, data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform, 1));
        Assert.Equal(new uint[] { 1 }, data.ChangedRows(writer.Batch!));
        Assert.Equal(changedBinding, data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public void UnwrittenOptionalUpdateKeepsTheBatchRevisionAndGpuValues()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        RenderInstanceBatch original = writer.Batch!;
        ulong revision = original.ContentRevision;
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Same(original, writer.Batch);
        Assert.Equal(revision, writer.Batch!.ContentRevision);
        Assert.Empty(data.ChangedRows(writer.Batch!));
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(writer.Batch, RenderInstanceTransformProperties.CurrentTransform));
        var changed = new List<uint>();
        writer.Batch.CollectChangedInstances(revision, changed);
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void NewBatchRejectsMissingBindingAndCanPublishAfterBinding(int count)
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        var current = Enumerable.Range(0, count).Select(index => Current(index + 1)).ToArray();
        var previous = Enumerable.Range(0, count).Select(index => Previous(index + 11)).ToArray();
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, count);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.Write(CurrentProperty, current);

            Assert.Throws<InvalidOperationException>(() => create.Publish());
            Assert.True(create.IsActive);

            rows.BindPerInstance(PreviousProperty);
            rows.Write(PreviousProperty, previous);
            batch.Batch = create.Publish();
            Assert.False(create.IsActive);
        };

        data.ExecuteFrame();

        Assert.Equal(count, writer.Batch!.InstanceCount);
        Assert.Equal(current, data.Values(writer.Batch, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(previous, data.Values(writer.Batch, RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public void NewBatchRejectsMissingRowsAndCanPublishAfterWritingTheRemainingRow()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Slice(0, 1).Write(PreviousProperty, new[] { Previous(11) });

            Assert.Throws<InvalidOperationException>(() => create.Publish());
            Assert.True(create.IsActive);

            rows.Slice(1, 1).Write(PreviousProperty, new[] { Previous(12) });
            batch.Batch = create.Publish();
            Assert.False(create.IsActive);
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(1), Current(2) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RequiredReplacementRejectsMissingRowsAndPreservesGpuValues(int writtenRows)
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        ulong revision = writer.Batch!.ContentRevision;
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout, replacedProperties: Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(8), Current(9) });
            rows.Slice(0, writtenRows).Write(PreviousProperty,
                new[] { Previous(18), Previous(19) }.AsSpan(0, writtenRows));
            Assert.Throws<InvalidOperationException>(() => update.Publish());
        };

        data.ExecuteFrame();

        Assert.Equal(revision, writer.Batch!.ContentRevision);
        Assert.Empty(data.ChangedRows(writer.Batch!));
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(writer.Batch, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(writer.Batch, RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public void NestedRestrictedSlicesWriteOnlyTheirRowsAndProperties()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 4);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2), Current(3), Current(4) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12), Previous(13), Previous(14) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        BufferRange changedValue = data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform, 2);
        BufferRange changedBinding = data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform);

        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Register<RenderTransform>("Test.Current",
            RenderInstanceTransformProperties.CurrentTransform.Key,
            RenderInstanceTransformProperties.CurrentTransform.Encoding);
        RenderInstancePropertyLayout currentOnly = builder.Freeze();
        var current = currentOnly.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout);
            var rows = update.OpenWrite(Layout).Restrict(currentOnly).Restrict(currentOnly);
            rows.BindPerInstance(current);
            rows.Slice(1, 3).Slice(1, 1).GetWriteSpan(current)[0] = Current(9);
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(1), Current(2), Current(9), Current(4) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12), Previous(13), Previous(14) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Equal(changedValue, data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform, 2));
        Assert.Equal(new uint[] { 2 }, data.ChangedRows(writer.Batch!));
        Assert.Equal(changedBinding, data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
    }

    [Fact]
    public void RestrictedSlicesCannotWidenPropertiesOrRows()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Register<RenderTransform>("Test.Current",
            RenderInstanceTransformProperties.CurrentTransform.Key,
            RenderInstanceTransformProperties.CurrentTransform.Encoding);
        RenderInstancePropertyLayout currentOnly = builder.Freeze();
        writer.Update = (context, _) =>
        {
            using var create = context.AllocateBatch(Layout, 4);
            var rows = create.OpenWrite(Layout).Restrict(currentOnly).Slice(1, 2);

            Assert.Same(currentOnly, rows.Properties);
            Assert.Equal(2, rows.Count);
            Assert.Throws<ArgumentException>(() => rows.Restrict(Layout));
            Assert.Throws<ArgumentException>(() => rows.Write(PreviousProperty, 0, Previous(10)));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.Slice(2, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rows.Slice(-1, 1));
        };

        data.ExecuteFrame();
    }

    [Fact]
    public void DerivedSlicesIncludingEmptyPropertiesExpireAfterPublication()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        RenderInstancePropertyLayout emptyProperties = new RenderInstancePropertyLayoutBuilder().Freeze();
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            var oneRow = rows.Slice(1, 1);
            RenderInstancePropertyLayout oneRowProperties = oneRow.Properties;
            var empty = rows.Restrict(emptyProperties).Slice(2, 0);
            Assert.Same(emptyProperties, empty.Properties);
            Assert.Equal(0, empty.Count);

            batch.Batch = create.Publish();

            Assert.Throws<ObjectDisposedException>(() => oneRow.Slice(0, 1));
            Assert.Throws<ObjectDisposedException>(() => oneRow.Restrict(oneRowProperties));
            Assert.Throws<ObjectDisposedException>(() => empty.Slice(0, 0));
        };

        data.ExecuteFrame();
    }

    [Fact]
    public void DefaultSliceRejectsRowsBeforeUninitializedAccess()
    {
        RenderInstanceWriteSlice rows = default;

        Assert.Throws<ArgumentOutOfRangeException>(() => rows.Slice(0, 1));
        Assert.Throws<InvalidOperationException>(() => rows.Slice(0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SeparatelyFrozenLayoutReplacesBatchValues(int count)
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        var initialCurrent = Enumerable.Range(0, count).Select(index => Current(index + 1)).ToArray();
        var initialPrevious = Enumerable.Range(0, count).Select(index => Previous(index + 11)).ToArray();
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, count);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, initialCurrent);
            rows.Write(PreviousProperty, initialPrevious);
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        RenderInstanceBatch original = writer.Batch!;
        ulong revision = original.ContentRevision;

        RenderInstancePropertyLayout replacement = RenderInstancePropertyLayout.Compose(Layout);
        Assert.NotSame(Layout, replacement);
        var current = replacement.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        var previous = replacement.Resolve(RenderInstanceTransformProperties.PreviousTransform);
        var updatedCurrent = Enumerable.Range(0, count).Select(index => Current(index + 21)).ToArray();
        var updatedPrevious = Enumerable.Range(0, count).Select(index => Previous(index + 31)).ToArray();
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, replacement, replacement);
            var rows = update.OpenWrite(replacement);
            rows.BindPerInstance(current);
            rows.BindPerInstance(previous);
            rows.Write(current, updatedCurrent);
            rows.Write(previous, updatedPrevious);
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Same(original, writer.Batch);
        Assert.True(writer.Batch!.ContentRevision > revision);
        Assert.Equal(updatedCurrent, data.Values(writer.Batch, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(updatedPrevious, data.Values(writer.Batch, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Equal(Enumerable.Range(0, count).Select(row => (uint)row), data.ChangedRows(writer.Batch));
    }

    [Fact]
    public void SubsetUpdateKeepsTheBatchPropertyOffsets()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        BufferRange changedValue = data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform, 1);
        BufferRange changedBinding = data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform);

        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Register<RenderPreviousTransform>("Test.Previous",
            RenderInstanceTransformProperties.PreviousTransform.Key,
            RenderInstanceTransformProperties.PreviousTransform.Encoding);
        RenderInstancePropertyLayout previousOnly = builder.Freeze();
        var previous = previousOnly.Resolve(RenderInstanceTransformProperties.PreviousTransform);
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, previousOnly);
            var rows = update.OpenWrite(previousOnly);
            rows.BindPerInstance(previous);
            rows.Write(previous, 1, Previous(29));
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(1), Current(2) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(29) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Equal(changedValue, data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform, 1));
        Assert.Equal(new uint[] { 1 }, data.ChangedRows(writer.Batch!));
        Assert.Equal(changedBinding, data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public void RejectedUpdateLayoutLeavesTheBatchAvailableForAValidRewrite()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();

        var builder = new RenderInstancePropertyLayoutBuilder();
        RenderInstancePropertyEncoding encoding = RenderInstanceTransformProperties.PreviousTransform.Encoding;
        builder.Register<RenderPreviousTransform>("Test.Previous",
            RenderInstanceTransformProperties.PreviousTransform.Key,
            new RenderInstancePropertyEncoding("test.incompatible.previous", encoding.ValueSize,
                encoding.StorageAlignment, encoding.StorageStride, encoding.MetadataWordCount));
        RenderInstancePropertyLayout incompatible = builder.Freeze();
        writer.Update = (context, batch) =>
        {
            ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            {
                using var rejected = context.RewriteBatch(batch.Batch!, incompatible);
            });
            Assert.Equal("properties", failure.ParamName);

            using var update = context.RewriteBatch(batch.Batch!, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.Write(CurrentProperty, 0, Current(9));
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(9), Current(2) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public void BatchValuesReachGpuWhenStorageAddsEarlierProperties()
    {
        var builder = new RenderInstancePropertyLayoutBuilder();
        builder.Include(Layout);
        builder.Register<uint>("Test.Extra", new RenderInstancePropertyKey("a.extra"),
            new RenderInstancePropertyEncoding("test.uint.v1", valueSize: 4,
                storageAlignment: 4, storageStride: 4, metadataWordCount: 1));
        RenderInstancePropertyLayout storageLayout = builder.Freeze();
        var storageCurrent = storageLayout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        var storagePrevious = storageLayout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
        Assert.NotEqual(CurrentProperty.Ordinal, storageCurrent.Ordinal);
        Assert.NotEqual(PreviousProperty.Ordinal, storagePrevious.Ordinal);
        Assert.NotEqual(CurrentProperty.Descriptor.MetadataWordOffset,
            storageCurrent.Descriptor.MetadataWordOffset);
        Assert.NotEqual(PreviousProperty.Descriptor.MetadataWordOffset,
            storagePrevious.Descriptor.MetadataWordOffset);

        using var data = new InstanceData(storageLayout);
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(1), Current(2) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) },
            data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
    }

    private static RenderInstancePropertyLayout Layout => RenderInstanceTransformProperties.Layout;
    private static ResolvedRenderInstanceProperty<RenderTransform> CurrentProperty =>
        Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
    private static ResolvedRenderInstanceProperty<RenderPreviousTransform> PreviousProperty =>
        Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
    private static RenderTransform Current(float value) =>
        new(Quaternion.Identity, new Vector3(value, 0, 0), 1, Vector3.One);
    private static RenderPreviousTransform Previous(float value) => new(Current(value));
    private readonly record struct BatchGroup(int Value) : IComponent;

    private readonly struct GroupComposer : IRenderInstanceBatchComposer<int>
    {
        public RenderInstanceChanges GetChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
            packet.ChangedSince<BatchGroup>(lastSystemVersion) ? RenderInstanceChanges.Structure :
            packet.ChangedSince<RenderTransform>(lastSystemVersion) ||
            packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)
                ? RenderInstanceChanges.Values : RenderInstanceChanges.None;
        public int CountGroups(ReadOnlyQueryPacket packet, int entityRow) => 1;
        public int GetGroup(ReadOnlyQueryPacket packet, int entityRow, int groupIndex) =>
            packet.Read<BatchGroup>()[entityRow].Value;
        public RenderInstancePropertyLayout GetLayout(in int group) => Layout;
        public void Bind(in int group, RenderInstanceWriteSlice destination)
        {
            destination.BindPerInstance(CurrentProperty);
            destination.BindPerInstance(PreviousProperty);
        }
        public void Write(in int group, int groupIndex, ReadOnlyQueryPacket packet, int entityRow,
            RenderInstanceWriteSlice destination)
        {
            destination.Write(CurrentProperty, packet.Read<RenderTransform>().Slice(entityRow, 1));
            destination.Write(PreviousProperty, packet.Read<RenderPreviousTransform>().Slice(entityRow, 1));
        }
    }

    private readonly struct WriteResult(int[] result) : IJob
    {
        public void Execute() => result[0] = 17;
    }

    private sealed class BatchWriter : ISystem<RenderPrepareSystemContext>
    {
        internal RenderInstanceBatch? Batch;
        internal Action<RenderPrepareSystemContext, BatchWriter>? Update;
        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context) => Update?.Invoke(context, this);
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (Batch is not null) context.Retire(Batch);
        }
    }

    private sealed class InstanceData : IDisposable
    {
        private readonly RenderTestDevice _device = new(DeviceFeatures.None,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)]);
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly RenderInstanceStorageSystem _storage;
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderFrameSystems _frames;
        private readonly BytesReceiver _properties = new();
        private readonly RenderInstanceDataTransfer _transfer;
        private readonly Dictionary<RenderInstanceBatch, byte[]> _metadataBytes = [];
        private readonly Dictionary<RenderInstanceBatch, ulong> _submittedRevisions = [];
        private readonly Dictionary<RenderInstanceBatch, uint[]> _changes = [];
        private readonly Dictionary<RenderInstanceBatch, RenderInstanceBatchView> _views = [];
        internal RenderWorld World { get; } = new();
        internal RenderPrepareSystems Prepare { get; }
        internal Func<RenderInstanceBatch[]> Batches { get; set; } = () => [];
        internal uint[] ChangedRows(RenderInstanceBatch batch) => _changes[batch];

        internal InstanceData(RenderInstancePropertyLayout? storageLayout = null)
        {
            _graph = new(_device.Backend, _device.Device,
                [_device.Backend.GetQueue(_device.Device, QueueType.Graphics),
                    _device.Backend.GetQueue(_device.Device, QueueType.Compute),
                    _device.Backend.GetQueue(_device.Device, QueueType.Copy)]);
            _storage = new(_device.Backend, _device.Device, _device.Coordinator, World, storageLayout ?? Layout,
                new RenderInstanceOptions { RowCapacity = 8, BatchCapacity = 4 });
            _transfer = _storage.GetDataTransfer(_graph);
            using (var edit = _graph.BeginEdit())
            {
                edit.AddReadback(_transfer.Data, BufferRange.Whole, _properties, "Sparse instance values");
                edit.Commit();
            }
            _extraction = new(World);
            Prepare = new(World, _storage, _extraction);
            _frames = new(World, _storage);
            _frames.Add(new BatchReader(this));
        }

        internal void PrepareFrame()
        {
            Assert.True(_graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(10)));
            using (next)
            {
                Assert.True(_device.Coordinator.TryBeginPrepare(out var prepare));
                using (prepare) { Prepare.Update(prepare); prepare.Commit(); }
            }
        }

        internal void ExecuteFrame()
        {
            Assert.True(_graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(10)));
            using (next)
            {
                Assert.True(_device.Coordinator.TryBeginPrepare(out var prepare));
                using (prepare) { Prepare.Update(prepare); prepare.Commit(); }
                Assert.True(_device.Coordinator.TryBeginFrame(out var read));
                using (read)
                using (var frame = _graph.BeginFrame(next))
                {
                    _frames.Update(read, frame);
                    var transferFrame = frame;
                    _changes.Clear();
                    foreach (var entry in _views)
                    {
                        _submittedRevisions.TryGetValue(entry.Key, out ulong previous);
                        var changed = new List<uint>();
                        entry.Key.CollectChangedInstances(previous, changed);
                        _changes.Add(entry.Key, changed.ToArray());
                        using (var metadata = _device.Backend.Map(entry.Value.Metadata, MapType.Read, BufferRange.Whole))
                            _metadataBytes[entry.Key] = metadata.Bytes.ToArray();
                        _transfer.Stage(ref transferFrame, entry.Key, entry.Value);
                    }
                    var completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
                    int count;
                    try { count = frame.Execute(completions); }
                    catch
                    {
                        QueueCompletion[] partial = completions.Where(value => value != default).ToArray();
                        if (partial.Length != 0 && !read.IsClosed) read.Complete(partial);
                        throw;
                    }
                    read.Complete(completions.AsSpan(0, count));
                    foreach (var entry in _views) _submittedRevisions[entry.Key] = entry.Value.ContentRevision;
                    foreach (var completion in completions.AsSpan(0, count))
                        Assert.Equal(WaitStatus.Completed, _device.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                }
            }
            _graph.CollectCompleted();
        }

        internal T[] Values<T>(RenderInstanceBatch batch, RenderInstanceProperty<T> property) where T : unmanaged
        {
            var values = new T[batch.InstanceCount];
            for (int row = 0; row < values.Length; row++)
                values[row] = MemoryMarshal.Read<T>(_properties.Bytes.AsSpan(
                    checked((int)PropertyRange(batch, property, row).Offset)));
            return values;
        }

        internal BufferRange MetadataRange<T>(RenderInstanceBatch batch, RenderInstanceProperty<T> property) where T : unmanaged
        {
            var descriptor = Layout.Resolve(property).Descriptor;
            return new(_views[batch].MetadataRange.Offset + checked((ulong)descriptor.MetadataWordOffset * sizeof(uint)),
                checked((ulong)descriptor.Encoding.MetadataWordCount * sizeof(uint)));
        }

        internal BufferRange PropertyRange<T>(RenderInstanceBatch batch, RenderInstanceProperty<T> property, int row)
            where T : unmanaged
        {
            var descriptor = Layout.Resolve(property).Descriptor;
            uint binding = MemoryMarshal.Read<uint>(_metadataBytes[batch].AsSpan(checked((int)MetadataRange(batch, property).Offset)));
            ulong offset = binding & RenderInstanceLinearMetadata.AddressMask;
            if ((binding & RenderInstanceLinearMetadata.PerInstanceBit) != 0)
                offset += checked((ulong)(row * descriptor.Encoding.StorageStride));
            return new(offset, checked((ulong)descriptor.Encoding.StorageStride));
        }

        public void Dispose()
        {
            _frames.Dispose();
            Assert.True(_device.Coordinator.TryBeginPrepare(out var prepare));
            using (prepare)
            {
                Prepare.Shutdown(prepare);
                _storage.Shutdown(prepare);
                prepare.Commit();
            }
            Prepare.Dispose();
            _storage.Dispose();
            _graph.Dispose();
            _extraction.Dispose();
            World.Dispose();
            _device.Dispose();
        }

        private sealed class BatchReader(InstanceData data) : ISystem<RenderFrameSystemContext>
        {
            public void OnCreate(ref RenderFrameSystemContext context) { }
            public void OnDestroy(ref RenderFrameSystemContext context) { }
            public void OnUpdate(ref RenderFrameSystemContext context)
            {
                using var instances = context.OpenInstances();
                data._views.Clear();
                foreach (var batch in data.Batches())
                    data._views.Add(batch, instances.Bind(batch, Layout));
            }
        }
    }

    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}

[CollectionDefinition("Render job configuration", DisableParallelization = true)]
public sealed class RenderJobConfigurationCollection
{
}
