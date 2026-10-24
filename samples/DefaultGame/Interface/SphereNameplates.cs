using System.Globalization;
using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Render.Components;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace DefaultGame;

/// <summary>World canvases with numbered Surface plates following selected moving spheres.</summary>
internal sealed partial class SphereNameplates : IDisposable
{
    private readonly World _world;
    private readonly List<(Entity Sphere, Entity Canvas)> _nameplates = [];

    internal SphereNameplates(World world)
    {
        _world = world;
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<MeshMotion>().Read<WorldTransform>());
        var spheres = new List<Entity>();
        try
        {
            world.ExecuteQuery(query, rows =>
            {
                int index = 0;
                foreach (QueryChunkView chunk in rows.Chunks)
                    foreach (Entity sphere in chunk.Entities)
                    {
                        const int columns = 32;
                        if (index / columns == columns - 1 && index % 4 == 0) spheres.Add(sphere);
                        index++;
                    }
            });
        }
        finally { world.ReleaseQuery(query); }
        int number = 1;
        foreach (Entity sphere in spheres)
        {
            Entity canvas = world.CreateEntity(new WorldTransform { Qvvs = PlateTransform(sphere) });
            world.Add(canvas, new Canvas
            {
                Target = CanvasTarget.WorldSpace(canvas), Size = new Size(160, 44), DepthTest = true,
            });
            Views.Views.Spawn(world, canvas, new NumberPlate(number++));
            _nameplates.Add((sphere, canvas));
        }
    }

    internal void Update()
    {
        foreach ((Entity sphere, Entity canvas) in _nameplates)
            _world.Replace(canvas, new WorldTransform { Qvvs = PlateTransform(sphere) });
    }

    private TransformQvvs PlateTransform(Entity sphere) => TransformQvvs.Combine(
        _world.Read<WorldTransform>(sphere).Qvvs,
        new TransformQvvs(new Vector3(2.4f, 2.5f, -0.2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), 0.03f));

    public void Dispose()
    {
        foreach ((Entity _, Entity canvas) in _nameplates)
            Hierarchy<ElementTree>.DestroySubtree(_world, canvas);
    }

    private readonly record struct NumberPlate(int Number) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Absolute().Size(160, 44).Background(new Color(0.02f, 0.08f, 0.14f, 0.92f)).Corner(7)
                .Border(new Color(0.12f, 0.85f, 1), 1).Content())
            {
                Row().Size(6, 24).Position(10, 10).Background(new Color(0.12f, 0.85f, 1)).Corner(2);
                string digits = Number.ToString("D3", CultureInfo.InvariantCulture);
                ReadOnlySpan<byte> masks = [0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f];
                ReadOnlySpan<Rect> segments =
                [
                    new(0, 0, 14, 3), new(12, 1, 3, 12), new(12, 12, 3, 12), new(0, 23, 14, 3),
                    new(0, 12, 3, 12), new(0, 1, 3, 12), new(0, 11, 14, 3),
                ];
                for (int digit = 0; digit < digits.Length; digit++)
                    for (int segment = 0; segment < segments.Length; segment++)
                        if ((masks[digits[digit] - '0'] & (1 << segment)) != 0)
                        {
                            Rect rectangle = segments[segment];
                            Row().Size(rectangle.Width, rectangle.Height)
                                .Position(43 + digit * 24 + rectangle.X, 9 + rectangle.Y)
                                .Background(new Color(0.85f, 0.95f, 1)).Corner(1);
                        }
            }
            return default;
        }
    }
}
