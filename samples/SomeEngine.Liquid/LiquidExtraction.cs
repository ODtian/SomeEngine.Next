using System.Numerics;
using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
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

public sealed class LiquidExtraction : IRenderExtractionRangeSystem
{
    private readonly Dictionary<Entity, LiquidFrames> _frames = [];
    private readonly List<LiquidChange> _changes = [];
    private readonly List<Entity> _removed = [];
    private readonly LiquidCollector _direct;

    public LiquidExtraction() => _direct = new(this);

    public Entity RenderEntity { get; private set; }
    public double CopyMilliseconds { get; private set; }
    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<LiquidBody>();
        query.Write<LiquidRenderData>();
    }
    public void Reset()
    {
        _changes.Clear();
        _removed.Clear();
        _direct.Reset();
        CopyMilliseconds = 0;
    }

    public void Collect(QueryChunkView chunk)
    {
        _direct.Collect(ReadOnlyQueryPacketJobs.Borrow(chunk), 0, true);
        MergeValues(_direct);
        _direct.Reset();
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        if (!chunk.TryRead<LiquidBody>(out _) || !chunk.HasChangedSinceLastSystemVersion<LiquidBody>()) return false;
        _direct.Collect(ReadOnlyQueryPacketJobs.Borrow(chunk), 0, true);
        bool changed = MergeValues(_direct);
        _direct.Reset();
        return changed;
    }

    public IRenderExtractionCollector CreateRangeCollector() => new LiquidCollector(this);

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context) =>
        MergeValues((LiquidCollector)collector);

    private bool MergeValues(LiquidCollector values)
    {
        foreach (LiquidChange change in values.Changes)
        {
            _frames[change.Source] = change.Frames;
            _changes.Add(change);
        }
        CopyMilliseconds += values.CopyMilliseconds;
        return values.Changes.Count != 0;
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        if (fullCollection)
        {
            foreach (Entity source in _frames.Keys)
                if (!sourceWorld.IsAlive(source) || !sourceWorld.Has<LiquidBody>(source)) _removed.Add(source);
            foreach (Entity source in _removed) _frames.Remove(source);
        }
        return _removed.Count != 0;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach (LiquidChange change in _changes)
        {
            Entity entity = context.RetainMirror(change.Source);
            context.Upsert(entity, new LiquidRenderData(change.Frames.Collected));
            change.Frames.Exchange();
        }
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_frames.ContainsKey(mirror.Source)) context.RemoveIfExists<LiquidRenderData>(mirror.RenderEntity);
        RenderEntity = _frames.Count == 0 ? Entity.Null : context.RequireMirror(_frames.Keys.First());
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach (LiquidChange change in _changes)
        {
            context.UpdateExisting(context.RequireMirror(change.Source), new LiquidRenderData(change.Frames.Collected));
            change.Frames.Exchange();
        }
    }

    public void ValidateChanges(RenderExtractionContext context) { }
    public void Dispose()
    {
        Reset();
        _frames.Clear();
    }

    private sealed class LiquidFrames
    {
        internal LiquidFrame Collected = new(), Displayed = new();
        internal void Exchange() => (Displayed, Collected) = (Collected, Displayed);
    }

    private readonly record struct LiquidChange(Entity Source, LiquidFrames Frames);

    private sealed class LiquidCollector(LiquidExtraction extraction) : IRenderExtractionCollector
    {
        internal readonly List<LiquidChange> Changes = [];
        internal double CopyMilliseconds;

        public void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
        {
            if (!packet.TryRead<LiquidBody>(out ReadOnlySpan<LiquidBody> liquids)) return;
            long started = Stopwatch.GetTimestamp();
            for (int row = 0; row < packet.Count; row++)
            {
                if (!fullCollection && !packet.RowChangedSince<LiquidBody>(row, lastSystemVersion)) continue;
                Entity source = packet.Entities[row];
                LiquidFrames frames = extraction._frames.TryGetValue(source, out LiquidFrames? current) ? current : new();
                frames.Collected.CopyFrom(liquids[row].Flow, liquids[row].View);
                Changes.Add(new(source, frames));
            }
            CopyMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        public void Reset()
        {
            Changes.Clear();
            CopyMilliseconds = 0;
        }

        public void Dispose() => Reset();
    }
}
