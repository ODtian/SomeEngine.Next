global using static SomeEngine.Views.Generators.Tests.CardFunctions;
global using static SomeEngine.Views.Generators.Tests.DefaultCardFunctions;
global using static SomeEngine.Views.Generators.Tests.CaptionFunctions;

using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.Generators.Tests;

public sealed class ViewFunctionTests
{
    [Fact]
    public void LambdaBranchesKeepTheirLocalStateSeparate()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new LambdaCards(false));
        frames.Frame();
        Entity first = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(first, new CardState(8));
        frames.World.Replace(root, new LambdaCards(true));

        frames.Frame();

        Entity second = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.NotEqual(first, second);
        Assert.Equal(0, frames.World.Read<CardState>(second).Value);
    }

    [Fact]
    public void BypassingInterceptionUsesTheDevelopmentCheckOrShippingZeroKey()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new UninterceptedCards(1));
#if DEBUG
        var error = Assert.Throws<InvalidOperationException>(frames.Frame);
        Assert.Contains("intercept", error.Message, StringComparison.OrdinalIgnoreCase);
#else
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(0UL, frames.World.Read<ElementKey>(child).Key);
        Assert.Equal(new Card("1"), frames.World.ReadSparse<Card>(child));
#endif
    }

    [Fact]
    public void NestedPrivateViewFunctionsKeepCallerIdentitiesSeparate()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NestedCards.Parent(false));
        frames.Frame();
        Entity first = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(first, new CardState(8));
        frames.World.Replace(root, new NestedCards.Parent(true));

        frames.Frame();

        Entity second = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.NotEqual(first, second);
        Assert.Equal(0, frames.World.Read<CardState>(second).Value);
    }

    [Fact]
    public void NamedFunctionPreservesTypeArgumentsAndOptionalParameters()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new Captions("two"));

        frames.Frame();

        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(new Caption<string>("two", 2), frames.World.ReadSparse<Caption<string>>(child));
    }

    [Fact]
    public void PartialViewUsesSparseStorageByDefault()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new DefaultCards("hello"));

        frames.Frame();

        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal("hello", frames.World.ReadSparse<DefaultCard>(child).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoCallsOnOneLineKeepSeparateStateWhenOneIsRemoved(bool named)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new SameLineCards(true, named));
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal(2, children.Length);
        Assert.NotEqual(frames.World.Read<ElementKey>(children[0]).Key,
            frames.World.Read<ElementKey>(children[1]).Key);
        frames.World.Replace(children[1], new CardState(7));
        frames.World.Replace(root, new SameLineCards(false, named));

        frames.Frame();

        Assert.Equal(children[1], Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(7, frames.World.Read<CardState>(children[1]).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingBranchesCreatesSeparateLocalState(bool named)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new BranchCards(false, named));
        frames.Frame();
        Entity first = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        frames.World.Replace(first, new CardState(9));
        frames.World.Replace(root, new BranchCards(true, named));

        frames.Frame();

        Entity second = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.NotEqual(first, second);
        Assert.Equal(0, frames.World.Read<CardState>(second).Value);
        Assert.False(frames.World.IsAlive(first));
    }

    [Fact]
    public void NamedFunctionAndViewStoreTheSameParameters()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new TwoCards("Inventory"));

        frames.Frame();

        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Equal(new Card("Inventory"), frames.World.ReadSparse<Card>(children[0]));
        Assert.Equal(frames.World.ReadSparse<Card>(children[0]), frames.World.ReadSparse<Card>(children[1]));
    }
}

public static partial class NestedCards
{
    public readonly record struct Parent(bool Alternate) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Alternate)
                HiddenCardFunctions.HiddenCard("same");
            else
                HiddenCardFunctions.HiddenCard("same");
            return default;
        }
    }

    private readonly record struct HiddenCard(string Title) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            if (!Scope.World.Has<CardState>(Scope.Entity))
            {
                Scope.World.Add(Scope.Entity, new CardState(0));
                Scope.World.AddTag<LocalState>(Scope.Entity);
            }
            return default;
        }
    }
}

public readonly record struct Card(string Title) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        if (!Scope.World.Has<CardState>(Scope.Entity))
        {
            Scope.World.Add(Scope.Entity, new CardState(0));
            Scope.World.AddTag<LocalState>(Scope.Entity);
        }
        return default;
    }
}

public readonly record struct CardState(int Value) : IComponent;

public readonly record struct LambdaCards(bool Alternate) : IView, IComponent
{
    public BuildTask Build()
    {
        bool alternate = Alternate;
        Action contents = () =>
        {
            if (alternate)
                Card("same");
            else
                Scope.View(new Card("same"));
        };
        contents();
        return default;
    }
}

public readonly record struct UninterceptedCards(int Value) : IView, IComponent
{
    private delegate Entity DeclareCard(in Card view);
#pragma warning disable SEV004 // Exercise the public fallback when a caller suppresses the compile diagnostic.
    private static readonly DeclareCard s_declare = Scope.View<Card>;
#pragma warning restore SEV004

    public BuildTask Build()
    {
        s_declare(new Card(Value.ToString()));
        return default;
    }
}

public readonly record struct Caption<T>(T Value, int Repeat = 2) : IView, ISparseComponent where T : notnull
{
    public BuildTask Build() => default;
}

public readonly record struct Captions(string Value) : IView, IComponent
{
    public BuildTask Build()
    {
        Caption(Value);
        return default;
    }
}

public readonly partial record struct DefaultCard(string Title) : IView
{
    public BuildTask Build() => default;
}

public readonly record struct DefaultCards(string Title) : IView, IComponent
{
    public BuildTask Build()
    {
        DefaultCard(Title);
        return default;
    }
}

public readonly record struct SameLineCards(bool ShowFirst, bool Named) : IView, IComponent
{
    public BuildTask Build()
    {
        if (Named)
        {
            if (ShowFirst) Card("same"); Card("same");
        }
        else
        {
            if (ShowFirst) Scope.View(new Card("same")); Scope.View(new Card("same"));
        }
        return default;
    }
}

public readonly record struct BranchCards(bool Alternate, bool Named) : IView, IComponent
{
    public BuildTask Build()
    {
        if (Named)
        {
            if (Alternate)
                Card("same");
            else
                Card("same");
        }
        else
        {
            if (Alternate)
                Scope.View(new Card("same"));
            else
                Scope.View(new Card("same"));
        }
        return default;
    }
}

public readonly record struct TwoCards(string Title) : IView, IComponent
{
    public BuildTask Build()
    {
        Card(Title);
        Scope.View(new Card(Title));
        return default;
    }
}
