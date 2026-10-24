using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;

namespace SomeEngine.Elements;

/// <summary>Runs after layout and retargets each entity's presentation back to identity.</summary>
public struct FlipSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _nodes;
    private List<FlipTarget> _targets;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _targets = new();
        _nodes = context.World.Query(context.World.QueryDefinition()
            .Read<FlipSettings>().ReadWrite<FlipFrame>().ReadWrite<PresentationOffset>().Optional<LayoutRect>(QueryAccess.Read)
            .Optional<ElementTransform>(QueryAccess.Read));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _targets.Clear();
        List<FlipTarget> targets = _targets;
        world.ExecuteQuery(_nodes, context.LastSystemVersion, ref targets, static (rows, ref targets) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                FlipFrame previous = row.Read<FlipFrame>();
                if (!row.Has<LayoutRect>())
                {
                    if (previous.HasFrame) row.ReadWrite<FlipFrame>().HasFrame = false;
                    continue;
                }
                Rect rectangle = row.Read<LayoutRect>().Value;
                ElementTransform transform = row.Has<ElementTransform>()
                    ? row.Read<ElementTransform>() : ElementTransform.Identity;
                if (previous.HasFrame && previous.Rectangle == rectangle &&
                    previous.Transform == transform.Matrix && previous.Alpha == transform.Alpha)
                    continue;

                if (previous.HasFrame)
                {
                    PresentationOffset offset = row.Read<PresentationOffset>();
                    bool matrixChanged = previous.Rectangle != rectangle || previous.Transform != transform.Matrix;
                    bool alphaChanged = previous.Alpha != transform.Alpha;
                    Spec spec = row.Read<FlipSettings>().Spec;
                    if (matrixChanged) offset.Matrix = Invert(previous, rectangle, transform.Matrix) * offset.Matrix;
                    if (alphaChanged) offset.Alpha = transform.Alpha != 0 ? previous.Alpha * offset.Alpha / transform.Alpha : 1;
                    row.ReadWrite<PresentationOffset>() = offset;
                    targets.Add(new FlipTarget(row.Entity, spec, matrixChanged, alphaChanged));
                }

                previous.Rectangle = rectangle;
                previous.Transform = transform.Matrix;
                previous.Alpha = transform.Alpha;
                previous.HasFrame = true;
                row.ReadWrite<FlipFrame>() = previous;
            }
        });
        foreach (FlipTarget target in _targets)
        {
            if (target.MatrixChanged)
                PropertyAnimation.To(world, target.Entity, PresentationChannels.Matrix, Matrix3x2.Identity, target.Spec);
            if (target.AlphaChanged)
                PropertyAnimation.To(world, target.Entity, PresentationChannels.Alpha, 1f, target.Spec);
        }
    }

    private static Matrix3x2 Invert(FlipFrame previous, Rect rectangle, Matrix3x2 transform)
    {
        Matrix3x2 translation = Matrix3x2.CreateTranslation(previous.Rectangle.X - rectangle.X,
            previous.Rectangle.Y - rectangle.Y);
        if (previous.Transform == transform && previous.Rectangle.Width == rectangle.Width &&
            previous.Rectangle.Height == rectangle.Height)
            return translation;
        if (!Matrix3x2.Invert(transform, out Matrix3x2 inverse))
            return translation;
        return Matrix3x2.CreateTranslation(-rectangle.X, -rectangle.Y) * inverse
            * Matrix3x2.CreateScale(rectangle.Width > 0 ? previous.Rectangle.Width / rectangle.Width : 1,
                rectangle.Height > 0 ? previous.Rectangle.Height / rectangle.Height : 1)
            * previous.Transform * Matrix3x2.CreateTranslation(previous.Rectangle.X, previous.Rectangle.Y);
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_nodes);

    private readonly record struct FlipTarget(Entity Entity, Spec Spec,
        bool MatrixChanged, bool AlphaChanged);
}
