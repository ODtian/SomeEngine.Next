using System.Numerics;
using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Systems;

namespace SomeEngine.Liquid;

public readonly record struct LiquidBody(LiquidFlow Flow, LiquidView View, int Revision) : IComponent;
public readonly record struct LiquidRenderData(LiquidFrame Data) : IComponent;

public sealed class LiquidView
{
    public int Display;
    public float Yaw = .55f, Pitch = .38f, Distance = 6.8f;
    public Vector3 Target = new(0, .95f, 0);
}

/// <summary>RenderWorld's packed arrays. Collection exchanges two frames after one copy from game data.</summary>
public sealed class LiquidFrame
{
    public readonly Vector4[] PositionVolume = new Vector4[LiquidFlow.Capacity];
    public readonly Vector4[] Contact = new Vector4[LiquidFlow.Capacity];
    public readonly Vector4[] SurfaceSize = new Vector4[LiquidFlow.Capacity];
    public readonly Vector4[] Footprint = new Vector4[LiquidFlow.Capacity];
    public readonly Vector4[] CurveStart = new Vector4[LiquidFlow.Capacity], CurveEnd = new Vector4[LiquidFlow.Capacity];
    public readonly Vector4[] CurveFirstTangent = new Vector4[LiquidFlow.Capacity], CurveLastTangent = new Vector4[LiquidFlow.Capacity];
    public readonly Vector4[] CurveRadius = new Vector4[LiquidFlow.Capacity];
    public readonly LiquidObstacle[] Obstacles = new LiquidObstacle[2];
    public int Count, Display, PointLimit;
    public float Seconds, Extent;
    public Vector3 Nozzle;
    public string Scene = "pour";
    public LiquidSurface Surface;
    public LiquidMaterial Material;
    public Vector3 Camera, Target;

    internal void CopyFrom(LiquidFlow flow, LiquidView view)
    {
        Count = flow.Count; Seconds = (float)flow.Seconds; Display = view.Display; Nozzle = flow.Nozzle;
        ReadOnlySpan<LiquidPoint> points = flow.Points;
        Span<LiquidCurve> curves = stackalloc LiquidCurve[LiquidFlow.Capacity];
        LiquidGeometry.BuildCurves(points, flow.Inlet, curves);
        for (int index = 0; index < Count; index++)
        {
            ref readonly LiquidPoint point = ref points[index];
            PositionVolume[index] = new(point.Position, (float)point.Volume);
            Contact[index] = new(point.Normal, (int)point.Surface);
            SurfaceSize[index] = new(point.Radius, point.Width, 2 * point.Height, (int)point.Motion);
            Footprint[index] = new(point.Axis, point.Number);
            ref readonly LiquidCurve curve = ref curves[index];
            CurveStart[index] = new(curve.Start, curve.Length); CurveEnd[index] = new(curve.End, 0);
            CurveFirstTangent[index] = new(curve.FirstTangent, 0); CurveLastTangent[index] = new(curve.LastTangent, 0);
            CurveRadius[index] = new(curve.SquaredRadius, curve.Neck);
        }
        PointLimit = flow.PointLimit; Scene = flow.Scene; Extent = flow.Extent;
        Surface = flow.Surface; Material = flow.Material;
        flow.Obstacles.CopyTo(Obstacles, 0);
        Target = view.Target;
        Camera = new Vector3(MathF.Sin(view.Yaw) * MathF.Cos(view.Pitch),
            MathF.Sin(view.Pitch), MathF.Cos(view.Yaw) * MathF.Cos(view.Pitch)) * view.Distance
            + Target;
    }
}

public sealed class LiquidExtraction : IRenderExtractionSystem
{
    private LiquidFrame _collected = new(), _displayed = new();
    private Entity _source;
    private bool _hasLiquid;
    public Entity RenderEntity { get; private set; }
    public double CopyMilliseconds { get; private set; }
    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<LiquidBody>();
        query.Write<LiquidRenderData>();
    }
    public void Reset() => _hasLiquid = false;
    public void Collect(QueryChunkView chunk)
    {
        if (!chunk.TryRead<LiquidBody>(out ReadOnlySpan<LiquidBody> liquids)) return;
        _source = chunk.Entities[0];
        long started = Stopwatch.GetTimestamp();
        _collected.CopyFrom(liquids[0].Flow, liquids[0].View);
        CopyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _hasLiquid = true;
    }
    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.TryRead<LiquidBody>(out _) || !chunk.HasChangedSinceLastSystemVersion<LiquidBody>()) return false;
        Collect(chunk);
        return true;
    }
    public void Apply(RenderExtractionContext context)
    {
        if (!_hasLiquid) return;
        RenderEntity = context.RetainMirror(_source);
        context.Upsert(RenderEntity, new LiquidRenderData(_collected));
        (_displayed, _collected) = (_collected, _displayed);
    }
    public void ApplyChanges(RenderExtractionContext context)
    {
        context.UpdateExisting(RenderEntity, new LiquidRenderData(_collected));
        (_displayed, _collected) = (_collected, _displayed);
    }
    public void ValidateChanges(RenderExtractionContext context) { }
    public void Dispose() { }
}
