using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Queries;
using Xunit;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class CommandBundleChangesTests
{
    [Fact]
    public void AComponentQueryObservesEveryCreatedBundleInItsChangeWindow()
    {
        using var world = new World();
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<BundlePoint>().Read<BundleValue>());
        uint baseline = 0;
        world.ExecuteReadQuery(query, ref baseline,
            static (scoped QueryCursor rows, ref uint version) => version = rows.CurrentSystemVersion);
        using var commands = new CommandBuffer(world);
        for (int index = 0; index < 1024; index++)
        {
            DeferredEntity point = commands.CreateEntity();
            using var additions = commands.AddComponents(point);
            commands.Add(point, new BundlePoint(index));
            commands.Add(point, new BundleValue(index * 2));
        }
        commands.Playback();
        var values = new List<int>();
        world.ExecuteReadQuery(query, baseline, ref values, static (scoped QueryCursor rows, ref List<int> values) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
                foreach (int index in chunk.RowIndices)
                    if (chunk.RowChangedSinceLastSystemVersion<BundlePoint>(index))
                    {
                        rows.TryGetRow(chunk.Entities[index], out QueryRow row);
                        Assert.Equal(row.Read<BundlePoint>().Value * 2, row.Read<BundleValue>().Value);
                        values.Add(row.Read<BundlePoint>().Value);
                    }
        });
        Assert.Equal(Enumerable.Range(0, 1024), values.Order());
        world.ReleaseQuery(query);
    }
}

public readonly record struct BundlePoint(int Value) : IComponent;
public readonly record struct BundleValue(int Value) : IComponent;
