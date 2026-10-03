using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Compose.Compose;

namespace SomeEngine.Views.Generators.Tests;

public sealed partial class RememberTests
{
    [Fact]
    public void RepeatedRememberValuesKeepTheirOccurrencesOnEachView()
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = Views.Spawn(frames.World, Entity.Null, new RememberedScreens.Values_View(3, 10, false));
        Entity second = Views.Spawn(frames.World, Entity.Null, new RememberedScreens.Values_View(3, 20, false));
        frames.Frame();
        var firstValues = frames.World.ReadSparse<RememberedNumbers>(first);
        firstValues.Change(0, 31);
        firstValues.Change(1, 37);
        firstValues.Change(2, 41);
        frames.World.ReplaceSparse(first, new RememberedScreens.Values_View(2, 50, false));
        frames.Frame();
        Assert.Equal([31, 37], frames.World.ReadSparse<RememberedNumbers>(first).Values);
        Assert.Equal([20, 21, 22], frames.World.ReadSparse<RememberedNumbers>(second).Values);

        frames.World.ReplaceSparse(first, new RememberedScreens.Values_View(3, 70, false));
        frames.Frame();

        Assert.Equal([31, 37, 41], frames.World.ReadSparse<RememberedNumbers>(first).Values);
    }

    [Fact]
    public void KeyGroupsKeepRememberValuesWithTheirItemsWhenOrderChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RememberedScreens.Values_View(3, 10, true));
        frames.Frame();
        var values = frames.World.ReadSparse<RememberedNumbers>(root);
        values.Change(0, 31);
        values.Change(2, 41);

        frames.World.ReplaceSparse(root, new RememberedScreens.Values_View(3, 20, true));
        frames.Frame();

        Assert.Equal([41, 11, 31], frames.World.ReadSparse<RememberedNumbers>(root).Values);
    }

    [Fact]
    public void RememberCanRetainValueTypesAndNullFromItsInitializer()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RememberedScreens.Simple_View(3));
        frames.Frame();

        frames.World.ReplaceSparse(root, new RememberedScreens.Simple_View(7));
        frames.Frame();

        Assert.Equal("3:empty", frames.World.ReadSparse<ComposeText>(root).Value);
    }
}

public readonly record struct RememberedNumbers(int[] Values, Action<int, int> Change) : ISparseComponent;

public static partial class RememberedScreens
{
    [Composable]
    public static void Values(int count, int initial, bool keyed)
    {
        var states = new State<int>[count];
        var values = new int[count];
        for (int index = 0; index < count; index++)
        {
            int item = keyed && initial == 20 ? count - index - 1 : index;
            if (keyed)
            {
                using (Scope.Key((ulong)item))
                    states[index] = Remember(() => MutableStateOf(initial + item));
            }
            else
                states[index] = Remember(() => MutableStateOf(initial + item));
            values[index] = states[index].Value;
        }
        Scope.Write<RememberedNumbers>(Scope.Entity) = new(values, (index, value) => states[index].Value = value);
    }

    [Composable]
    public static void Simple(int initial)
    {
        int value = Remember(() => initial);
        string? text = Remember<string?>(() => initial == 3 ? null : "changed");
        Scope.Write<ComposeText>(Scope.Entity) = new(value + ":" + (text ?? "empty"));
    }
}
