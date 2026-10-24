using System.Diagnostics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Solid;

public static partial class Solid
{
    /// <summary>Tracks a condition and builds each selected branch once.</summary>
    public static void Show(Func<bool> when, Action children, Action? fallback = null)
    {
        DevelopmentCheck.Require(when is not null, "Show requires a condition getter.");
        DevelopmentCheck.Require(children is not null, "Show requires branch content.");
        Scope.View(new ShowCondition(when, children, fallback), 0);
    }

    /// <summary>Builds content for a present value and supplies its latest value through a getter.</summary>
    /// <param name="keyed">Recreates content when the value changes according to its default equality comparer.</param>
    public static void Show<T>(Func<T?> when, Action<Func<T>> children, Action? fallback = null, bool keyed = false)
    {
        DevelopmentCheck.Require(when is not null, "Show requires a value getter.");
        DevelopmentCheck.Require(children is not null, "Show requires branch content.");
        Scope.View(new ShowValue<T>(when, children, fallback, keyed), 0);
    }

    /// <summary>Declares the first matching branch, tracking conditions in their declaration order.</summary>
    public static void Switch(params ReadOnlySpan<Match> cases)
    {
        CheckMatches(cases);
        Scope.View(new SwitchConditions(cases.ToArray()), 0);
    }

    /// <summary>Describes a condition and the content built when Switch selects it.</summary>
    public static Match Match(Func<bool> when, Action children)
    {
        DevelopmentCheck.Require(when is not null, "Match requires a condition getter.");
        DevelopmentCheck.Require(children is not null, "Match requires branch content.");
        return new Match(when, children);
    }

    /// <summary>Tracks a component getter and rebuilds content when the selected function changes.</summary>
    public static void Dynamic(Func<Action> component)
    {
        DevelopmentCheck.Require(component is not null, "Dynamic requires a component getter.");
        Scope.View(new ComponentFunction(component), 0);
    }

    [Conditional("DEBUG")]
    private static void CheckMatches(ReadOnlySpan<Match> cases)
    {
        foreach (Match match in cases)
        {
            DevelopmentCheck.Require(match.When is not null, "Switch requires a condition getter for each Match.");
            DevelopmentCheck.Require(match.Children is not null, "Switch requires content for each Match.");
        }
    }

    private static void DeclareBranch<T>(T value, Action? children, bool keyed = true)
    {
        if (children is null)
            return;

        ulong key = 0;
        var previous = Hierarchy<ElementTree>.GetChildren(Scope.World, Scope.Entity);
        if (previous.Count > 0)
        {
            Entity branch = previous[0];
            key = Scope.World.Read<ElementKey>(branch).Key;
            T previousValue = Scope.World.Read<BranchContent<T>>(branch).Value;
            bool changed = keyed
                ? !EqualityComparer<T>.Default.Equals(previousValue, value)
                : (previousValue is null) != (value is null);
            if (changed)
                key = unchecked(key + 1);
        }
        Scope.View(new BranchContent<T>(value, children), key);
    }

    private readonly record struct ShowCondition(Func<bool> When, Action Children, Action? Fallback) : IView, IComponent
    {
        public BuildTask Build()
        {
            bool value = When();
            DeclareBranch(value, value ? Children : Fallback);
            return default;
        }
    }

    private readonly record struct ShowValue<T>(Func<T?> When, Action<Func<T>> Children, Action? Fallback, bool Keyed) : IView, IComponent
    {
        public BuildTask Build()
        {
            T? value = When();
            bool present = value is not null;
            Entity condition = Scope.Entity;
            Action<Func<T>> children = Children;
            Action? content = present ? () =>
            {
                Func<Func<T?>> current = Memo(() => Scope.Read<ShowValue<T>>(condition).When);
                children(() => current()()!);
            } : Fallback;
            DeclareBranch(value, content, Keyed);
            return default;
        }
    }

    private readonly record struct BranchContent<T>(T Value, Action Children) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Children();
            return default;
        }
    }

    private readonly record struct SwitchConditions(Match[] Cases) : IView, IComponent
    {
        public BuildTask Build()
        {
            for (int index = 0; index < Cases.Length; index++)
            {
                Match match = Cases[index];
                if (!match.When())
                    continue;
                DeclareBranch(index, match.Children);
                break;
            }
            return default;
        }
    }

    private readonly record struct ComponentFunction(Func<Action> Component) : IView, IComponent
    {
        public BuildTask Build()
        {
            Action content = Component();
            DevelopmentCheck.Require(content is not null, "Dynamic requires a component function.");
            DeclareBranch(content, content);
            return default;
        }
    }
}
