using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Layout;

public enum Axis : byte
{
    Horizontal,
    Vertical,
}

public enum Align : byte
{
    Auto,
    Start,
    Center,
    End,
    Stretch,
    SpaceBetween,
}

/// <summary>Measures and places caller-declared children along a horizontal or vertical axis.</summary>
public record struct Flex(Axis Direction, float Gap = 0, Align MainAlign = Align.Start,
    Align CrossAlign = Align.Stretch, bool Wrap = false) : IView, IComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;
    public BuildTask Build() => default;

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        FlexValidation.Check(in this);
        var items = new MeasuredItem[pass.Children.Count];
        bool grows = false;
        for (int index = 0; index < items.Length; index++)
        {
            Entity child = pass.Children[index];
            FlexItem item = pass.Has<FlexItem>(child) ? pass.Read<FlexItem>(child) : default;
            FlexValidation.CheckItem(in item);
            Box box = pass.Has<Box>(child) ? pass.Read<Box>(child) : default;
            var available = new Constraints(default, ToSize(float.PositiveInfinity, CrossSize(constraints.Max)));
            if (!float.IsNaN(item.Basis))
                available = new Constraints(ToSize(item.Basis, 0), ToSize(item.Basis, CrossSize(constraints.Max)));
            Size size = pass.Measure(child, in available);
            float basis = float.IsNaN(item.Basis) ? MainSize(size) : Math.Clamp(item.Basis, MainSize(box.Min), MainSize(box.Max));
            items[index] = new MeasuredItem(child, item, box, size, basis);
            grows |= item.Grow > 0;
        }

        List<FlexLine> lines = SplitLines(items, MainSize(constraints.Max));
        float main = 0;
        foreach (FlexLine line in lines)
            main = MathF.Max(main, line.Main);
        if (grows && float.IsFinite(MainSize(constraints.Max)))
            main = MainSize(constraints.Max);
        main = Math.Clamp(main, MainSize(constraints.Min), MainSize(constraints.Max));

        float cross = Gap * Math.Max(0, lines.Count - 1);
        for (int index = 0; index < lines.Count; index++)
        {
            FlexLine line = lines[index];
            DistributeMainSpace(items, in line, main);
            line.Main = Gap * Math.Max(0, line.Count - 1);
            line.Cross = 0;
            for (int childIndex = line.Start; childIndex < line.Start + line.Count; childIndex++)
            {
                MeasuredItem item = items[childIndex];
                if (item.Main != MainSize(item.Size))
                {
                    var available = new Constraints(ToSize(item.Main, 0), ToSize(item.Main, CrossSize(constraints.Max)));
                    item = item with { Size = pass.Measure(item.Entity, in available) };
                    items[childIndex] = item;
                }
                line.Main += MainSize(item.Size);
                line.Cross = MathF.Max(line.Cross, CrossSize(item.Size));
            }
            cross += line.Cross;
            lines[index] = line;
        }

        Size extent = constraints.Constrain(ToSize(main, cross));
        float crossPosition = 0;
        foreach (FlexLine measuredLine in lines)
        {
            FlexLine line = measuredLine;
            if (!Wrap)
                line.Cross = CrossSize(extent);
            PlaceLine(ref pass, items, in line, main, crossPosition);
            crossPosition += line.Cross + Gap;
        }
        return extent;
    }

    private readonly List<FlexLine> SplitLines(MeasuredItem[] items, float available)
    {
        var lines = new List<FlexLine>();
        var line = new FlexLine();
        for (int index = 0; index < items.Length; index++)
        {
            float gap = line.Count > 0 ? Gap : 0;
            if (Wrap && line.Count > 0 && line.Main + gap + items[index].Main > available)
            {
                lines.Add(line);
                line = new FlexLine { Start = index };
                gap = 0;
            }
            line.Count++;
            line.Main += gap + items[index].Main;
        }
        if (line.Count > 0)
            lines.Add(line);
        return lines;
    }

    private readonly void DistributeMainSpace(MeasuredItem[] items, in FlexLine line, float available)
    {
        float remaining = available - line.Main;
        bool growing = remaining > 0;
        bool clamped;
        do
        {
            double weight = 0;
            for (int index = line.Start; index < line.Start + line.Count; index++)
                weight += AvailableWeight(in items[index], growing);
            if (weight == 0 || remaining == 0)
                break;
            clamped = false;
            float free = remaining;
            for (int index = line.Start; index < line.Start + line.Count; index++)
            {
                MeasuredItem item = items[index];
                float desired = item.Main + (float)((double)free * AvailableWeight(in item, growing) / weight);
                float allocated = Math.Clamp(desired, MainSize(item.Box.Min), MainSize(item.Box.Max));
                clamped |= allocated != desired;
                remaining -= allocated - item.Main;
                items[index] = item with { Main = allocated };
            }
        } while (clamped && (growing ? remaining > 0 : remaining < 0));
    }

    private readonly float AvailableWeight(in MeasuredItem item, bool growing)
    {
        if (growing)
            return item.Main < MainSize(item.Box.Max) ? item.Data.Grow : 0;
        return item.Main > MainSize(item.Box.Min) ? item.Data.Shrink : 0;
    }

    private readonly void PlaceLine(ref LayoutPass pass, MeasuredItem[] items, in FlexLine line, float main, float crossPosition)
    {
        float free = main - line.Main;
        float position = MainAlign switch
        {
            Align.Center => free / 2,
            Align.End => free,
            _ => 0,
        };
        float spacing = Gap + (MainAlign == Align.SpaceBetween && line.Count > 1 ? MathF.Max(0, free) / (line.Count - 1) : 0);
        for (int index = line.Start; index < line.Start + line.Count; index++)
        {
            MeasuredItem item = items[index];
            Size size = item.Size;
            float allocated = item.Main;
            float crossAllocated = CrossSize(size);
            Align alignment = item.Data.Self == Align.Auto ? CrossAlign : item.Data.Self;
            if (alignment == Align.Stretch && float.IsNaN(CrossSize(item.Box.Fixed)))
                crossAllocated = Math.Clamp(line.Cross, CrossSize(item.Box.Min), CrossSize(item.Box.Max));
            if (allocated != MainSize(size) || crossAllocated != CrossSize(size))
            {
                var available = new Constraints(ToSize(allocated, crossAllocated), ToSize(allocated, crossAllocated));
                size = pass.Measure(item.Entity, in available);
            }
            float crossOffset = alignment switch
            {
                Align.Center => (line.Cross - CrossSize(size)) / 2,
                Align.End => line.Cross - CrossSize(size),
                _ => 0,
            };
            pass.Place(item.Entity, ToPosition(position, crossPosition + crossOffset));
            position += MainSize(size) + spacing;
        }
    }

    private readonly float MainSize(Size size) => Direction == Axis.Horizontal ? size.Width : size.Height;
    private readonly float CrossSize(Size size) => Direction == Axis.Horizontal ? size.Height : size.Width;
    private readonly Size ToSize(float main, float cross) =>
        Direction == Axis.Horizontal ? new Size(main, cross) : new Size(cross, main);
    private readonly Vector2 ToPosition(float main, float cross) =>
        Direction == Axis.Horizontal ? new Vector2(main, cross) : new Vector2(cross, main);

    private readonly record struct MeasuredItem(Entity Entity, FlexItem Data, Box Box, Size Size, float Main);

    private struct FlexLine
    {
        internal int Start;
        internal int Count;
        internal float Main;
        internal float Cross;
    }
}
