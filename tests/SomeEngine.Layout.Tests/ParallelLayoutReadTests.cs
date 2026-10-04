using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Views;

namespace SomeEngine.Layout.Tests;

public sealed partial class ParallelLayoutReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutsReadTheirRectangleAndNewExternalDataThroughThePublicPass(bool parallel)
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem { Parallel = parallel });
        Entity source = world.CreateEntity(new Dimensions(new Size(20, 30)));
        world.Add(source, new SizeChoice(false));
        Entity[] leaves = new Entity[4];
        Entity[] roots = new Entity[4];
        for (int index = 0; index < leaves.Length; index++)
        {
            Entity canvas = world.CreateEntity(new Canvas { Size = new Size(320, 240) });
            roots[index] = Views.Views.Spawn(world, canvas, new Panel(source));
        }
        systems.Update();
        Assert.All(roots, root => Assert.True(!world.HasSparse<BuildError>(root),
            world.HasSparse<BuildError>(root) ? world.ReadSparse<BuildError>(root).Exception.ToString() : ""));
        for (int index = 0; index < leaves.Length; index++)
        {
            Entity container = Hierarchy<ElementTree>.GetChildren(world, roots[index])[0];
            leaves[index] = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        }
        Assert.All(leaves, node => Assert.Equal(new Rect(0, 0, 20, 30), world.Read<LayoutRect>(node).Value));

        world.AddSparse(source, new SparseDimensions(new Size(40, 50)));
        world.Replace(source, new SizeChoice(true));
        systems.Update();
        Assert.All(leaves, node => Assert.Equal(new Rect(0, 0, 40, 50), world.Read<LayoutRect>(node).Value));

        world.ReplaceSparse(source, new SparseDimensions(new Size(60, 70)));
        systems.Update();
        Assert.All(leaves, node => Assert.Equal(new Rect(0, 0, 60, 70), world.Read<LayoutRect>(node).Value));
    }

    private readonly record struct SizeChoice(bool Sparse) : IComponent;
    private readonly record struct Dimensions(Size Value) : IComponent;
    private readonly record struct SparseDimensions(Size Value) : ISparseComponent;

    private readonly record struct Panel(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content()) Scope.View(new ContentSize(Source), 2);
            return default;
        }
    }

    private readonly record struct ContentSize(Entity Source) : IView, IComponent, ILayout
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            if (!pass.Has<LayoutRect>(pass.Node)) return constraints.Constrain(new Size(99, 99));
            return constraints.Constrain(pass.Read<SizeChoice>(Source).Sparse
                ? pass.Read<SparseDimensions>(Source).Value : pass.Read<Dimensions>(Source).Value);
        }
    }
}
