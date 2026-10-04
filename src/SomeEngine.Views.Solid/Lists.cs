using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Solid;

public static partial class Solid
{
    /// <summary>Builds each equal item once, moving its row and updating its index getter.</summary>
    public static void For<T>(Func<IReadOnlyList<T>> each, Action<T, Func<int>> row) where T : IEquatable<T>
    {
        DevelopmentCheck.Require(each is not null, "For requires an items getter.");
        DevelopmentCheck.Require(row is not null, "For requires row content.");
        Scope.View(new ItemList<T>(each, row), 0);
    }

    /// <summary>Builds entity-keyed rows from an Each query getter.</summary>
    public static void For(Func<QueryRows> each, Action<Entity, Func<int>> row)
    {
        DevelopmentCheck.Require(each is not null, "For requires an items getter.");
        For<Entity>(() => ReadEntities(each), row);
    }

    /// <summary>Builds each position once and supplies its current item through a getter.</summary>
    public static void Index<T>(Func<IReadOnlyList<T>> each, Action<Func<T>, int> row)
    {
        DevelopmentCheck.Require(each is not null, "Index requires an items getter.");
        DevelopmentCheck.Require(row is not null, "Index requires row content.");
        Scope.View(new PositionList<T>(each, row), 0);
    }

    /// <summary>Builds position-keyed rows from an Each query getter.</summary>
    public static void Index(Func<QueryRows> each, Action<Func<Entity>, int> row)
    {
        DevelopmentCheck.Require(each is not null, "Index requires an items getter.");
        Index<Entity>(() => ReadEntities(each), row);
    }

    private static IReadOnlyList<Entity> ReadEntities(Func<QueryRows> each)
    {
        var entities = new List<Entity>();
        foreach (Entity entity in each())
            entities.Add(entity);
        return entities;
    }

    private readonly record struct ItemList<T>(Func<IReadOnlyList<T>> Items, Action<T, Func<int>> Row) : IView, IComponent
        where T : IEquatable<T>
    {
        public BuildTask Build()
        {
            IReadOnlyList<T> items = Items();
            DevelopmentCheck.Require(items is not null, "For requires an items list.");
            var previous = Hierarchy<ElementTree>.GetChildren(Scope.World, Scope.Entity);
            var matched = new bool[previous.Count];
            ulong nextKey = 0;
            foreach (Entity child in previous)
                nextKey = Math.Max(nextKey, unchecked(Scope.World.Read<ElementKey>(child).Key + 1));

            for (int index = 0; index < items.Count; index++)
            {
                T item = items[index];
                ulong key = nextKey;
                bool found = false;
                for (int previousIndex = 0; previousIndex < previous.Count; previousIndex++)
                {
                    if (matched[previousIndex])
                        continue;
                    Entity child = previous[previousIndex];
                    T previousItem = Scope.World.Read<ItemRow<T>>(child).Item;
                    if (!EqualityComparer<T>.Default.Equals(previousItem, item))
                        continue;
                    matched[previousIndex] = true;
                    key = Scope.World.Read<ElementKey>(child).Key;
                    found = true;
                    break;
                }
                if (!found)
                    nextKey = unchecked(nextKey + 1);
                Scope.View(new ItemRow<T>(item, index, Row), key);
            }
            return default;
        }
    }

    private readonly record struct ItemRow<T>(T Item, int Index, Action<T, Func<int>> Row) : IView, IComponent
        where T : IEquatable<T>
    {
        public static BuildMode Mode => BuildMode.Once;

        public bool Equals(ItemRow<T> other) =>
            EqualityComparer<T>.Default.Equals(Item, other.Item) && Index == other.Index;

        public override int GetHashCode() => HashCode.Combine(Item, Index);

        public BuildTask Build()
        {
            Entity entity = Scope.Entity;
            Func<int> index = Memo(() => Scope.Read<ItemRow<T>>(entity).Index);
            Row(Item, index);
            return default;
        }
    }

    private readonly record struct PositionList<T>(Func<IReadOnlyList<T>> Items, Action<Func<T>, int> Row) : IView, IComponent
    {
        public BuildTask Build()
        {
            IReadOnlyList<T> items = Items();
            DevelopmentCheck.Require(items is not null, "Index requires an items list.");
            for (int index = 0; index < items.Count; index++)
                Scope.View(new PositionRow<T>(items[index], index, Row), (ulong)index);
            return default;
        }
    }

    private readonly record struct PositionRow<T>(T Item, int Index, Action<Func<T>, int> Row) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public bool Equals(PositionRow<T> other) =>
            EqualityComparer<T>.Default.Equals(Item, other.Item) && Index == other.Index;

        public override int GetHashCode() => HashCode.Combine(Item, Index);

        public BuildTask Build()
        {
            Entity entity = Scope.Entity;
            Func<T> item = Memo(() => Scope.Read<PositionRow<T>>(entity).Item);
            Row(item, Index);
            return default;
        }
    }
}
