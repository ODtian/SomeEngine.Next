using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;
using SomeEngine.Job;

namespace SomeEngine.Layout;

/// <summary>Layout reads, ordered children, and content-relative positions across canvas updates.</summary>
internal sealed class LayoutData(World world, ILayoutDataUpdate[] dataUpdates)
{
    internal World World { get; } = world;
    private readonly Dictionary<Entity, NodeLayout> _nodes = new();
    private readonly List<NodeLayout> _order = new();
    private readonly List<Entity> _roots = new();
    private readonly List<Entity> _children = new();
    private readonly List<Entity> _removed = new();
    private readonly List<(Entity Entity, Size Size)> _canvases = new();
    private readonly List<Subtree> _subtrees = new();
    private readonly List<(Entity, ushort)> _unusedInputKeys = new();
    private bool _parallelMeasurement;
    private ulong _frame;
    private uint _inputVersion;

    internal void BeginUpdate() => _frame++;

    internal void EndUpdate()
    {
        foreach (var pair in _nodes)
            if (pair.Value.TreeFrame != _frame)
                _removed.Add(pair.Key);
        foreach (Entity entity in _removed)
            _nodes.Remove(entity);
        _removed.Clear();
        _order.Clear();
        _roots.Clear();
        _children.Clear();
        _canvases.Clear();
        _subtrees.Clear();
    }

    internal void ReadCanvas(Entity canvas, Size size)
    {
        if (!World.Has<LayoutRect>(canvas)) World.Add(canvas, new LayoutRect());
        _canvases.Add((canvas, size));
        CollectChildren(canvas, _roots);
        foreach (Entity root in _roots)
        {
            ReadTree(root, canvas, canvas);
            _nodes[root].Position = default;
            AddSubtree(_nodes[root], Constraints.Fixed(size), canvas, true);
        }
        _roots.Clear();
    }

    internal void Update(bool parallel)
    {
        try
        {
            for (int index = _order.Count - 1; index >= 0; index--)
            {
                NodeLayout node = _order[index];
                foreach (Entity child in node.Children)
                {
                    if (!_nodes[child].SizeInputsChanged)
                        continue;
                    node.InputsChanged = true;
                    node.SizeInputsChanged |= !node.SizeIsFixed;
                }
                if (node.InputsChanged)
                    node.InputVersion = ++_inputVersion;
            }

            bool parallelContent = dataUpdates.All(update => update.SupportsParallel);
            MeasureSubtrees(parallel && parallelContent);
            while (true)
            {
                _subtrees.Clear();
                // Choose the highest dirty descendant under each cached boundary, making the jobs disjoint.
                foreach (NodeLayout node in _order)
                {
                    bool ancestorChanged = node.Parent != node.Canvas && _nodes[node.Parent].DirtySubtree;
                    bool changed = node.Cache.InputVersion != node.InputVersion;
                    node.DirtySubtree = ancestorChanged || changed;
                    if (changed && !ancestorChanged)
                        AddSubtree(node, node.Cache.Constraints, node.Parent, false);
                }
                if (_subtrees.Count == 0) break;
                MeasureSubtrees(parallel && parallelContent);
            }
            bool coordinatesChanged = false;
            foreach (NodeLayout node in _order)
            {
                coordinatesChanged |= node.ResultFrame == _frame;
                if (node.ResultFrame == _frame) ReleaseUnusedInputs(node);
                if (node.CacheWritten) WriteCache(node);
                Size measured = node.Cache.Size;
                if (World.Has<ScrollOffset>(node.Entity))
                {
                    UpdateScroll(node, measured);
                    Vector2 scroll = World.Read<ScrollOffset>(node.Entity).Value;
                    coordinatesChanged |= scroll != node.Scroll;
                    node.Scroll = scroll;
                }
                else
                {
                    coordinatesChanged |= node.Scroll != default;
                    node.Scroll = default;
                }
            }
            _subtrees.Clear();
            foreach (NodeLayout node in _order)
                if (node.Parent == node.Canvas) AddSubtree(node, default, node.Canvas, true);
            if (coordinatesChanged) ChunkJobs.Run(new RectangleJob(this), _subtrees.Count, parallel, chunkSize: 1);
            foreach (NodeLayout node in _order) WriteRectangle(node.Entity, node.Rectangle);
            foreach (var canvas in _canvases)
                WriteRectangle(canvas.Entity, new Rect(0, 0, canvas.Size.Width, canvas.Size.Height));
            foreach (ILayoutDataUpdate dataUpdate in dataUpdates)
                dataUpdate.Complete(World);
        }
        finally
        {
            _order.Clear();
            _roots.Clear();
            _children.Clear();
            _canvases.Clear();
            _subtrees.Clear();
        }
    }

    private void ReadTree(Entity entity, Entity parent, Entity canvas)
    {
        if (!World.Has<LayoutRect>(entity)) World.Add(entity, new LayoutRect());
        if (!_nodes.TryGetValue(entity, out NodeLayout? node))
        {
            node = new NodeLayout(entity, dataUpdates.Length);
            _nodes.Add(entity, node);
        }
        ushort viewType = World.Read<ViewType>(entity).Id;
        bool missingMeasurement = !node.HasMeasurement || !World.Has<LayoutCache>(entity) ||
            World.Read<LayoutCache>(entity).InputVersion != node.InputVersion;
        bool readsChanged = HasReadChanges(node);
        foreach (ILayoutDataUpdate dataUpdate in dataUpdates)
            readsChanged |= dataUpdate.HasChanges(World, entity);
        bool parentChanged = node.Parent != parent;
        bool typeChanged = node.ViewType != viewType;
        _children.Clear();
        CollectChildren(entity, _children);
        bool childrenChanged = !node.Children.SequenceEqual(_children);
        if (childrenChanged)
        {
            node.Children.Clear();
            node.Children.AddRange(_children);
        }
        node.InputsChanged = missingMeasurement || readsChanged || parentChanged || typeChanged || childrenChanged;
        node.SizeInputsChanged = missingMeasurement || parentChanged || typeChanged ||
            (readsChanged && !node.ConstraintsAreTight) || (childrenChanged && !node.SizeIsFixed);
        node.Parent = parent;
        node.ViewType = viewType;
        node.TreeFrame = _frame;
        node.Canvas = canvas;
        node.Measurable = ViewTypes.HasMeasure(World, entity);
        node.CacheWritten = false;
        if (World.Has<LayoutCache>(entity)) node.Cache = World.Read<LayoutCache>(entity);
        else node.HasMeasurement = false;
        if (node.InputsChanged)
            for (int index = 0; index < dataUpdates.Length; index++)
                if (dataUpdates[index].SupportsParallel)
                    node.Content[index] = dataUpdates[index].ReadContent(World, entity);
        if (node.InputsChanged)
            foreach (LayoutInput input in node.Inputs.Values) input.Capture(World);
        _order.Add(node);
        foreach (Entity child in node.Children)
            ReadTree(child, entity, canvas);
    }

    private bool HasReadChanges(NodeLayout node)
    {
        foreach (Dependency read in node.Reads)
            if (DependencyChanges.HasChanges(World, node.Entity, read))
                return true;
        return false;
    }

    internal Size Measure(Entity entity, in Constraints constraints, Entity parent, bool parentMeasurement = true)
    {
        NodeLayout node = _nodes[entity];
        DevelopmentCheck.Require(node.Parent == parent, "Measure requires this layout's child.");
        Box box = Has<Box>(entity, entity) ? Read<Box>(entity, entity) : default;
        LayoutValidation.CheckBox(in box);
        Constraints outerConstraints = ConstrainBox(in box, in constraints);
        bool firstMeasurement = node.MeasurementFrame != _frame;
        if (parentMeasurement)
        {
            if (firstMeasurement)
            {
                node.ConstraintsAreTight = constraints.Min == constraints.Max;
                node.SizeIsFixed = outerConstraints.Min == outerConstraints.Max;
            }
            else
            {
                // The parent's loose basis still contributes when its final allocation is fixed.
                node.ConstraintsAreTight &= constraints.Min == constraints.Max;
                node.SizeIsFixed &= outerConstraints.Min == outerConstraints.Max;
            }
        }
        node.MeasurementFrame = _frame;
        if (node.HasMeasurement)
        {
            LayoutCache cache = node.Cache;
            if (cache.InputVersion == node.InputVersion && cache.Constraints == constraints)
                return cache.Size;
        }

        node.HasMeasurement = false;
        if (firstMeasurement)
            node.Reads.Clear();
        Has<Box>(entity, entity);
        if (Has<Box>(entity, entity))
            Read<Box>(entity, entity);
        bool scrolling = Has<ScrollOffset>(entity, entity);
        Size padding = new(box.Padding.Left + box.Padding.Right, box.Padding.Top + box.Padding.Bottom);
        var contentConstraints = new Constraints(
            new Size(MathF.Max(0, outerConstraints.Min.Width - padding.Width), MathF.Max(0, outerConstraints.Min.Height - padding.Height)),
            new Size(MathF.Max(0, outerConstraints.Max.Width - padding.Width), MathF.Max(0, outerConstraints.Max.Height - padding.Height)));
        var pass = new LayoutPass(this, entity, new LayoutChildren(node.Children), new Vector2(box.Padding.Left, box.Padding.Top));
        Constraints measuredConstraints = scrolling ? new Constraints(default, Size.Unbounded) : contentConstraints;
        for (int index = 0; index < dataUpdates.Length; index++)
            if (dataUpdates[index].SupportsParallel) node.Content[index]?.Update(in measuredConstraints);
            else dataUpdates[index].Update(World, entity, in measuredConstraints);
        Size size = node.Measurable
            ? ViewTypes.Measure(node.ViewType, World, entity, ref pass, in measuredConstraints)
            : MeasureScrollContent(ref pass, in measuredConstraints);
        CheckMeasuredChildren(node);
        LayoutValidation.CheckSize(size);
        node.ContentSize = size;
        size = contentConstraints.Constrain(size);
        size = outerConstraints.Constrain(new Size(size.Width + padding.Width, size.Height + padding.Height));
        var measured = new LayoutCache { Constraints = constraints, Size = size, InputVersion = node.InputVersion };
        node.Cache = measured;
        node.ResultFrame = _frame;
        node.CacheWritten = true;
        if (!_parallelMeasurement) WriteCache(node);
        node.HasMeasurement = true;
        return size;
    }

    private void UpdateScroll(NodeLayout node, Size size)
    {
        ScrollOffset previous = World.Read<ScrollOffset>(node.Entity);
        ScrollOffset offset = previous;
        Box box = World.Has<Box>(node.Entity) ? World.Read<Box>(node.Entity) : default;
        Vector2 viewport = new(MathF.Max(0, size.Width - box.Padding.Left - box.Padding.Right),
            MathF.Max(0, size.Height - box.Padding.Top - box.Padding.Bottom));
        offset.Max = Vector2.Max(Vector2.Zero, new Vector2(node.ContentSize.Width, node.ContentSize.Height) - viewport);
        bool hasSeen = World.Has<ScrollSeen>(node.Entity);
        ScrollSeen seen = hasSeen ? World.Read<ScrollSeen>(node.Entity) : default;
        bool inputChanged = false;
        if (World.Has<ScrollInput>(node.Entity))
        {
            ScrollInput input = World.Read<ScrollInput>(node.Entity);
            if (input.Count != seen.Count)
            {
                offset.Value += input.Delta;
                seen.Count = input.Count;
                inputChanged = true;
            }
        }
        offset.Value = Vector2.Clamp(offset.Value, Vector2.Zero, offset.Max);
        if (World.Has<ScrollTarget>(node.Entity))
        {
            ScrollTarget target = World.Read<ScrollTarget>(node.Entity);
            if (target.Count != seen.TargetCount)
            {
                Rect region = target.Region;
                offset.Value = Vector2.Clamp(new Vector2(
                    ScrollToRegion(offset.Value.X, viewport.X, region.X, region.Width),
                    ScrollToRegion(offset.Value.Y, viewport.Y, region.Y, region.Height)), Vector2.Zero, offset.Max);
                seen.TargetCount = target.Count;
                inputChanged = true;
            }
        }
        if (inputChanged)
        {
            if (hasSeen)
                World.Replace(node.Entity, seen);
            else
                World.Add(node.Entity, seen);
        }
        if (offset.Value != previous.Value || offset.Max != previous.Max)
            World.Replace(node.Entity, offset);
    }

    private static float ScrollToRegion(float offset, float viewport, float start, float size)
    {
        if (start < offset)
            return start;
        if (start + size > offset + viewport)
            return MathF.Min(start, start + size - viewport);
        return offset;
    }

    private static Size MeasureScrollContent(ref LayoutPass pass, in Constraints constraints)
    {
        Size extent = default;
        foreach (Entity child in pass.Children)
        {
            Size size = pass.Measure(child, in constraints);
            pass.Place(child, Vector2.Zero);
            extent = new Size(MathF.Max(extent.Width, size.Width), MathF.Max(extent.Height, size.Height));
        }
        return extent;
    }

    private static Constraints ConstrainBox(in Box box, in Constraints constraints)
    {
        Size fixedSize = box.Fixed;
        Size minimum = new(
            Math.Clamp(box.Min.Width, constraints.Min.Width, constraints.Max.Width),
            Math.Clamp(box.Min.Height, constraints.Min.Height, constraints.Max.Height));
        Size maximum = new(
            Math.Clamp(box.Max.Width, minimum.Width, constraints.Max.Width),
            Math.Clamp(box.Max.Height, minimum.Height, constraints.Max.Height));
        if (!float.IsNaN(fixedSize.Width))
        {
            minimum = minimum with { Width = Math.Clamp(fixedSize.Width, minimum.Width, maximum.Width) };
            maximum = maximum with { Width = minimum.Width };
        }
        if (!float.IsNaN(fixedSize.Height))
        {
            minimum = minimum with { Height = Math.Clamp(fixedSize.Height, minimum.Height, maximum.Height) };
            maximum = maximum with { Height = minimum.Height };
        }
        return new Constraints(minimum, maximum);
    }

    internal void Place(Entity child, Vector2 position, Entity parent)
    {
        DevelopmentCheck.Require(_nodes.ContainsKey(child) && _nodes[child].HasMeasurement &&
            _nodes[child].MeasurementFrame == _frame,
            "Place requires a measured child.");
        NodeLayout node = _nodes[child];
        DevelopmentCheck.Require(node.Parent == parent, "Place requires this layout's child.");
        node.Position = position;
    }

    [Conditional("DEBUG")]
    private void CheckMeasuredChildren(NodeLayout node)
    {
        foreach (Entity child in node.Children)
            DevelopmentCheck.Require(_nodes[child].HasMeasurement && _nodes[child].MeasurementFrame == _frame,
                "Each layout child requires a measured size before its parent measurement returns.");
    }

    internal ref readonly T Read<T>(Entity node, Entity entity) where T : struct
    {
        LayoutInput<T> input = ReadInput<T>(node, entity);
        DevelopmentCheck.Require(input.Exists, $"Layout input {typeof(T).Name} requires component data.");
        RecordRead(node, new Dependency
        {
            Kind = DependencyKind.Component, Entity = entity, Component = ComponentId<T>.Value,
            Version = input.Version,
        });
        return ref input.Value;
    }

    internal bool Has<T>(Entity node, Entity entity) where T : struct
    {
        bool exists = ReadInput<T>(node, entity).Exists;
        RecordRead(node, new Dependency
        {
            Kind = DependencyKind.Presence, Entity = entity, Component = ComponentId<T>.Value,
            Version = exists ? 1u : 0u,
        });
        return exists;
    }

    private LayoutInput<T> ReadInput<T>(Entity node, Entity entity) where T : struct
    {
        Dictionary<(Entity, ushort), LayoutInput> inputs = _nodes[node].Inputs;
        var key = (entity, ComponentId<T>.Value);
        if (!inputs.TryGetValue(key, out LayoutInput? input))
        {
            if (_parallelMeasurement) throw new LayoutInputRequired();
            input = new LayoutInput<T>(entity);
            inputs.Add(key, input);
        }
        if (!_parallelMeasurement) input.Capture(World);
        return (LayoutInput<T>)input;
    }

    private void WriteCache(NodeLayout node)
    {
        if (World.Has<LayoutCache>(node.Entity)) World.Replace(node.Entity, node.Cache);
        else World.Add(node.Entity, node.Cache);
        node.CacheWritten = false;
    }

    private void ReleaseUnusedInputs(NodeLayout node)
    {
        foreach (var key in node.Inputs.Keys)
        {
            bool used = false;
            foreach (Dependency read in node.Reads)
                if (read.Entity == key.Item1 && read.Component == key.Item2) { used = true; break; }
            if (!used) _unusedInputKeys.Add(key);
        }
        foreach (var key in _unusedInputKeys) node.Inputs.Remove(key);
        _unusedInputKeys.Clear();
    }

    private void MeasureSubtrees(bool parallel)
    {
        for (int index = _subtrees.Count - 1; index >= 0; index--)
        {
            Subtree subtree = _subtrees[index];
            NodeLayout node = _nodes[subtree.Entity];
            if (!node.HasMeasurement || node.Cache.InputVersion != node.InputVersion || node.Cache.Constraints != subtree.Constraints)
                continue;
            Measure(subtree.Entity, in subtree.Constraints, subtree.Parent, subtree.ParentMeasurement);
            _subtrees.RemoveAt(index);
        }
        _parallelMeasurement = parallel;
        try { ChunkJobs.Run(new MeasureJob(this), _subtrees.Count, parallel, chunkSize: 1); }
        finally { _parallelMeasurement = false; }
        foreach (Subtree subtree in _subtrees)
        {
            if (subtree.Error is not null) ExceptionDispatchInfo.Capture(subtree.Error).Throw();
            if (!subtree.ReadsRequired) continue;
            RestartMeasurement(subtree.Entity);
            Measure(subtree.Entity, in subtree.Constraints, subtree.Parent, subtree.ParentMeasurement);
        }
    }

    private void AddSubtree(NodeLayout node, Constraints constraints, Entity parent, bool parentMeasurement)
    {
        Subtree subtree = node.Subtree;
        subtree.Constraints = constraints;
        subtree.Parent = parent;
        subtree.ParentMeasurement = parentMeasurement;
        subtree.ReadsRequired = false;
        subtree.Error = null;
        _subtrees.Add(subtree);
    }

    private void RestartMeasurement(Entity entity)
    {
        NodeLayout node = _nodes[entity];
        node.MeasurementFrame = 0;
        node.HasMeasurement = false;
        foreach (Entity child in node.Children) RestartMeasurement(child);
    }

    private void CalculateRectangles(Entity entity, Vector2 origin)
    {
        NodeLayout node = _nodes[entity];
        node.CanvasPosition = origin + node.Position;
        node.Rectangle = new Rect(node.CanvasPosition.X, node.CanvasPosition.Y, node.Cache.Size.Width, node.Cache.Size.Height);
        foreach (Entity child in node.Children) CalculateRectangles(child, node.CanvasPosition - node.Scroll);
    }

    private readonly struct MeasureJob(LayoutData data) : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            for (int index = chunk.Start; index < chunk.Start + chunk.Length; index++)
            {
                Subtree subtree = data._subtrees[index];
                try { data.Measure(subtree.Entity, in subtree.Constraints, subtree.Parent, subtree.ParentMeasurement); }
                catch (LayoutInputRequired) { subtree.ReadsRequired = true; }
                catch (Exception error) { subtree.Error = error; }
            }
        }
    }

    private readonly struct RectangleJob(LayoutData data) : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            for (int index = chunk.Start; index < chunk.Start + chunk.Length; index++)
                data.CalculateRectangles(data._subtrees[index].Entity, Vector2.Zero);
        }
    }

    private sealed class Subtree(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal Constraints Constraints;
        internal Entity Parent;
        internal bool ParentMeasurement;
        internal bool ReadsRequired;
        internal Exception? Error;
    }

    private sealed class LayoutInputRequired : Exception;

    private abstract class LayoutInput
    {
        internal bool Exists;
        internal uint Version;
        internal abstract void Capture(World world);
    }

    private sealed class LayoutInput<T>(Entity entity) : LayoutInput where T : struct
    {
        internal T Value;
        internal override void Capture(World world)
        {
            Exists = world.HasComponentData<T>(entity);
            Value = Exists ? world.ReadComponentData<T>(entity) : default;
            Version = Exists ? world.ComponentDataWriteVersion<T>(entity) : 0;
        }
    }

    private void RecordRead(Entity entity, Dependency read)
    {
        List<Dependency> reads = _nodes[entity].Reads;
        for (int index = 0; index < reads.Count; index++)
        {
            Dependency previous = reads[index];
            if (previous.Entity != read.Entity || previous.Component != read.Component || previous.Kind != read.Kind)
                continue;
            reads[index] = read;
            return;
        }
        reads.Add(read);
    }

    private void WriteRectangle(Entity entity, Rect value)
    {
        if (!World.Has<LayoutRect>(entity))
            World.Add(entity, new LayoutRect { Value = value });
        else if (World.Read<LayoutRect>(entity).Value != value)
            World.Replace(entity, new LayoutRect { Value = value });
    }

    private void CollectChildren(Entity node, List<Entity> children)
    {
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(World, node))
        {
            if (World.Has<Canvas>(child))
                continue;
            if (ViewTypes.HasMeasure(World, child) || World.Has<ScrollOffset>(child))
                children.Add(child);
            else
                CollectChildren(child, children);
        }
    }

    private sealed class NodeLayout(Entity entity, int contentCount)
    {
        internal readonly Entity Entity = entity;
        internal readonly List<Entity> Children = new();
        internal readonly List<Dependency> Reads = new();
        internal readonly Dictionary<(Entity, ushort), LayoutInput> Inputs = new();
        internal readonly ILayoutContent?[] Content = new ILayoutContent?[contentCount];
        internal readonly Subtree Subtree = new(entity);
        internal Entity Parent;
        internal Entity Canvas;
        internal ushort ViewType;
        internal Vector2 Position;
        internal Vector2 CanvasPosition;
        internal Size ContentSize;
        internal LayoutCache Cache;
        internal Rect Rectangle;
        internal Vector2 Scroll;
        internal bool CacheWritten;
        internal bool Measurable;
        internal bool DirtySubtree;
        internal uint InputVersion;
        internal ulong TreeFrame;
        internal ulong MeasurementFrame;
        internal ulong ResultFrame;
        internal bool HasMeasurement;
        internal bool ConstraintsAreTight;
        internal bool SizeIsFixed;
        internal bool InputsChanged;
        internal bool SizeInputsChanged;
    }
}
