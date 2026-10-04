using System.Diagnostics;
using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

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
    }

    internal void Update(Entity canvas, Size size)
    {
        try
        {
            if (!World.Has<LayoutRect>(canvas))
                World.Add(canvas, new LayoutRect());
            CollectChildren(canvas, _roots);
            foreach (Entity root in _roots)
                ReadTree(root, canvas);

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

            Constraints constraints = Constraints.Fixed(size);
            foreach (Entity root in _roots)
            {
                _nodes[root].Position = default;
                Measure(root, in constraints, canvas);
            }
            // A cached parent keeps its fixed-size children in place while their contents update.
            foreach (NodeLayout node in _order)
            {
                LayoutCache cache = World.Read<LayoutCache>(node.Entity);
                if (cache.InputVersion != node.InputVersion)
                    Measure(node.Entity, in cache.Constraints, node.Parent);
            }

            foreach (NodeLayout node in _order)
            {
                node.CanvasPosition = node.Position;
                if (node.Parent != canvas && _nodes.TryGetValue(node.Parent, out NodeLayout? parent))
                    node.CanvasPosition += parent.CanvasPosition;
                Size measured = World.Read<LayoutCache>(node.Entity).Size;
                WriteRectangle(node.Entity, new Rect(node.CanvasPosition.X, node.CanvasPosition.Y,
                    measured.Width, measured.Height));
            }
            WriteRectangle(canvas, new Rect(0, 0, size.Width, size.Height));
            foreach (ILayoutDataUpdate dataUpdate in dataUpdates)
                dataUpdate.Complete(World);
        }
        finally
        {
            _order.Clear();
            _roots.Clear();
            _children.Clear();
        }
    }

    private void ReadTree(Entity entity, Entity parent)
    {
        if (!_nodes.TryGetValue(entity, out NodeLayout? node))
        {
            node = new NodeLayout(entity);
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
        _order.Add(node);
        foreach (Entity child in node.Children)
            ReadTree(child, entity);
    }

    private bool HasReadChanges(NodeLayout node)
    {
        foreach (Dependency read in node.Reads)
            if (DependencyChanges.HasChanges(World, node.Entity, read))
                return true;
        return false;
    }

    internal Size Measure(Entity entity, in Constraints constraints, Entity parent)
    {
        NodeLayout node = _nodes[entity];
        DevelopmentCheck.Require(node.Parent == parent, "Measure requires this layout's child.");
        if (!World.Has<LayoutRect>(entity))
            World.Add(entity, new LayoutRect());
        Box box = World.Has<Box>(entity) ? World.Read<Box>(entity) : default;
        LayoutValidation.CheckBox(in box);
        Constraints outerConstraints = ConstrainBox(in box, in constraints);
        bool firstMeasurement = node.MeasurementFrame != _frame;
        if (firstMeasurement)
        {
            node.ConstraintsAreTight = constraints.Min == constraints.Max;
            node.SizeIsFixed = outerConstraints.Min == outerConstraints.Max;
            node.MeasurementFrame = _frame;
        }
        else
        {
            // Flex's loose basis still contributes to the parent even when its final allocation is fixed.
            node.ConstraintsAreTight &= constraints.Min == constraints.Max;
            node.SizeIsFixed &= outerConstraints.Min == outerConstraints.Max;
        }
        if (node.HasMeasurement && World.Has<LayoutCache>(entity))
        {
            LayoutCache cache = World.Read<LayoutCache>(entity);
            if (cache.InputVersion == node.InputVersion && cache.Constraints == constraints)
                return cache.Size;
        }

        node.HasMeasurement = false;
        if (firstMeasurement)
            node.Reads.Clear();
        Has<Box>(entity, entity);
        if (World.Has<Box>(entity))
            Read<Box>(entity, entity);
        Size padding = new(box.Padding.Left + box.Padding.Right, box.Padding.Top + box.Padding.Bottom);
        var contentConstraints = new Constraints(
            new Size(MathF.Max(0, outerConstraints.Min.Width - padding.Width), MathF.Max(0, outerConstraints.Min.Height - padding.Height)),
            new Size(MathF.Max(0, outerConstraints.Max.Width - padding.Width), MathF.Max(0, outerConstraints.Max.Height - padding.Height)));
        var pass = new LayoutPass(this, entity, new LayoutChildren(node.Children), new Vector2(box.Padding.Left, box.Padding.Top));
        foreach (ILayoutDataUpdate dataUpdate in dataUpdates)
            dataUpdate.Update(World, entity, in contentConstraints);
        Size size = ViewTypes.Measure(World, entity, ref pass, in contentConstraints);
        CheckMeasuredChildren(node);
        LayoutValidation.CheckSize(size);
        size = contentConstraints.Constrain(size);
        size = outerConstraints.Constrain(new Size(size.Width + padding.Width, size.Height + padding.Height));
        var measured = new LayoutCache { Constraints = constraints, Size = size, InputVersion = node.InputVersion };
        if (World.Has<LayoutCache>(entity))
            World.Replace(entity, in measured);
        else
            World.Add(entity, in measured);
        node.HasMeasurement = true;
        return size;
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
        RecordRead(node, new Dependency
        {
            Kind = DependencyKind.Component, Entity = entity, Component = ComponentId<T>.Value,
            Version = World.ComponentDataWriteVersion<T>(entity),
        });
        return ref World.ReadComponentReference<T>(entity);
    }

    internal bool Has<T>(Entity node, Entity entity) where T : struct
    {
        bool exists = World.HasComponentData<T>(entity);
        RecordRead(node, new Dependency
        {
            Kind = DependencyKind.Presence, Entity = entity, Component = ComponentId<T>.Value,
            Version = exists ? 1u : 0u,
        });
        return exists;
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
            if (ViewTypes.HasMeasure(World, child))
                children.Add(child);
            else
                CollectChildren(child, children);
        }
    }

    private sealed class NodeLayout(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal readonly List<Entity> Children = new();
        internal readonly List<Dependency> Reads = new();
        internal Entity Parent;
        internal ushort ViewType;
        internal Vector2 Position;
        internal Vector2 CanvasPosition;
        internal uint InputVersion;
        internal ulong TreeFrame;
        internal ulong MeasurementFrame;
        internal bool HasMeasurement;
        internal bool ConstraintsAreTight;
        internal bool SizeIsFixed;
        internal bool InputsChanged;
        internal bool SizeInputsChanged;
    }
}
