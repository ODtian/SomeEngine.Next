using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views.Tests;

public sealed class StateTests
{
    [Fact]
    public void ReadingAroundACrossThreadChangeRefreshesTheCompleteDisplayOnTheNextFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var state = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null,
            new RepeatedStateDisplay(state, () => Task.Run(() => state.Value = 7).GetAwaiter().GetResult()));

        frames.Frame();
        Assert.Equal("3+7", frames.World.Read<DisplayText>(root).Value);
        frames.Frame();
        Assert.Equal("3+7;7+7", frames.World.Read<DisplayText>(root).Value);
        frames.Frame();
        Assert.Equal("3+7;7+7", frames.World.Read<DisplayText>(root).Value);
    }

#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AccessingTheSameStateDuringAnUpdateReportsADevelopmentError(int access)
    {
        var state = new State<int>(3);
        uint version = state.Version;

        InvalidOperationException error = await Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
            state.Update((ref int value) =>
            {
                value = 9;
                switch (access)
                {
                    case 0: _ = state.Value; break;
                    case 1: state.Value = 7; break;
                    case 2: state.Update(static (ref int current) => current++); break;
                    case 3: state.Update(1, static (ref int current, int amount) => current += amount); break;
                }
            }))).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("same State", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, state.Value);
        Assert.Equal(version, state.Version);
        state.Update(static (ref int value) => value++);
        Assert.Equal(4, state.Value);
    }
#endif

    [Fact]
    public async Task ConcurrentReadersReceiveCompleteLargeValues()
    {
        var state = new State<CheckedValue>(CheckedValue.From(0));
        using var start = new Barrier(6);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task[] workers = Enumerable.Range(0, 6).Select(worker => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait(stop.Token);
            for (int index = 1; index <= 10000; index++)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (worker == 0)
                    state.Value = CheckedValue.From(index);
                else if (worker == 1)
                    state.Update(static (ref CheckedValue value) => value = CheckedValue.From(value.Number + 1));
                else if (worker == 2)
                    state.Update(1L, static (ref CheckedValue value, long amount) => value = CheckedValue.From(value.Number + amount));
                else
                    state.Value.Verify();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        state.Value.Verify();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentUpdatesAccumulateEveryChangeAndRefreshTheDisplay(bool withArgument)
    {
        using var frames = new HeadlessFrameDriver();
        var state = new State<int>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new StateDisplay(state));
        frames.Frame();
        using var start = new Barrier(4);
        Task[] writers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int index = 0; index < 2500; index++)
            {
                if (withArgument)
                    state.Update(1, static (ref int value, int amount) => value += amount);
                else
                    state.Update(static (ref int value) => value++);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(10000, state.Value);
        frames.Frame();
        Assert.Equal("0;10000", frames.World.Read<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EqualChangesRetainTheVersionAndCompletedDisplay(int changeKind)
    {
        using var frames = new HeadlessFrameDriver();
        var state = new State<string>(new string('a', 3));
        Entity root = Views.Spawn(frames.World, Entity.Null, new StringStateDisplay(state));
        frames.Frame();
        uint version = state.Version;

        switch (changeKind)
        {
            case 0: state.Value = new string('a', 3); break;
            case 1: state.Update(static (ref string value) => value = new string('a', 3)); break;
            case 2: state.Update('a', static (ref string value, char letter) => value = new string(letter, 3)); break;
        }
        frames.Frame();

        Assert.Equal(version, state.Version);
        Assert.Equal("aaa", frames.World.Read<DisplayText>(root).Value);
        state.Value = "bbb";
        frames.Frame();
        Assert.Equal("aaa;bbb", frames.World.Read<DisplayText>(root).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedChangeRetainsTheValueAndAllowsALaterUpdate(bool withArgument)
    {
        using var frames = new HeadlessFrameDriver();
        var state = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new StateDisplay(state));
        frames.Frame();
        uint version = state.Version;
        var failure = new InvalidOperationException("The change failed.");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (withArgument)
                state.Update(failure, static (ref int value, InvalidOperationException exception) =>
                {
                    value = 9;
                    throw exception;
                });
            else
                state.Update((ref int value) =>
                {
                    value = 9;
                    throw failure;
                });
        });

        Assert.Same(failure, error);
        Assert.Equal(3, state.Value);
        Assert.Equal(version, state.Version);
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);
        state.Update(4, static (ref int value, int amount) => value += amount);
        frames.Frame();
        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void AChangeCanReadAndUpdateAnotherState()
    {
        var first = new State<int>(3);
        var second = new State<int>(5);

        first.Update((ref int value) =>
        {
            second.Update(2, static (ref int current, int amount) => current += amount);
            value += second.Value;
        });

        Assert.Equal(10, first.Value);
        Assert.Equal(7, second.Value);
    }

    [Fact]
    public async Task WritingStateOnAnotherThreadUpdatesTheDisplayOnTheNextFrame()
    {
        using var frames = new HeadlessFrameDriver();
        var state = new State<int>(3);
        Entity root = Views.Spawn(frames.World, Entity.Null, new StateDisplay(state));
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        await Task.Run(() => state.Value = 7);
        Assert.Equal("3", frames.World.Read<DisplayText>(root).Value);

        frames.Frame();
        Assert.Equal("3;7", frames.World.Read<DisplayText>(root).Value);
    }

    private readonly record struct StateDisplay(State<int> Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            AppendDisplay(Source.Value.ToString());
            return default;
        }
    }

    private readonly record struct RepeatedStateDisplay(State<int> Source, Action ChangeValue) : IView, IComponent
    {
        public BuildTask Build()
        {
            int first = Source.Value;
            ChangeValue();
            int second = Source.Value;
            AppendDisplay($"{first}+{second}");
            return default;
        }
    }

    private readonly record struct StringStateDisplay(State<string> Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            AppendDisplay(Source.Value);
            return default;
        }
    }

    private static void AppendDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
        {
            text = $"{Scope.World.Read<DisplayText>(Scope.Entity).Value};{text}";
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        }
        else
        {
            Scope.World.Add(Scope.Entity, new DisplayText(text));
        }
    }

    private readonly record struct DisplayText(string Value) : IComponent;

    private readonly record struct CheckedValue(
        long Number, long Double, long Triple, long Complement,
        long Negative, long Offset, long Product, long Check)
    {
        internal static CheckedValue From(long number) =>
            new(number, number * 2, number * 3, ~number, -number, number + 37, number * 11, number ^ 0x5A5A5A5A);

        internal void Verify()
        {
            Assert.Equal(Number * 2, Double);
            Assert.Equal(Number * 3, Triple);
            Assert.Equal(~Number, Complement);
            Assert.Equal(-Number, Negative);
            Assert.Equal(Number + 37, Offset);
            Assert.Equal(Number * 11, Product);
            Assert.Equal(Number ^ 0x5A5A5A5A, Check);
        }
    }
}
