using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>Measures caller-declared children in fixed, content-sized, or proportional tracks.</summary>
public record struct Grid(GridTracks Rows, GridTracks Columns, float Gap = 0) : IView, IComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;
    public BuildTask Build() => default;

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        GridValidation.CheckGap(Gap);
        float[] columns = CreateFixedTrackSizes(Columns);
        float[] rows = CreateFixedTrackSizes(Rows);
        var items = new MeasuredCell[pass.Children.Count];
        for (int index = 0; index < items.Length; index++)
        {
            Entity child = pass.Children[index];
            GridCell cell = pass.Has<GridCell>(child) ? pass.Read<GridCell>(child) : default;
            GridValidation.CheckCell(in cell, Rows.Count, Columns.Count);
            Box box = pass.Has<Box>(child) ? pass.Read<Box>(child) : default;
            var available = new Constraints(default, new Size(
                GetCellMaximum(Columns, columns, cell.Column, cell.ColumnSpan, constraints.Max.Width),
                GetCellMaximum(Rows, rows, cell.Row, cell.RowSpan, constraints.Max.Height)));
            items[index] = new MeasuredCell(child, cell, box, pass.Measure(child, in available));
        }
        GrowAutoTracks(Columns, columns, items, Axis.Horizontal);
        DistributeFractionSpace(Columns, columns, items, Axis.Horizontal, constraints.Max.Width);
        for (int index = 0; index < items.Length; index++)
        {
            MeasuredCell item = items[index];
            float width = GetSpanLength(columns, item.Cell.Column, item.Cell.ColumnSpan);
            var available = new Constraints(new Size(
                float.IsNaN(item.Box.Fixed.Width) ? MathF.Min(width, item.Box.Max.Width) : 0, 0),
                new Size(width, GetCellMaximum(Rows, rows, item.Cell.Row, item.Cell.RowSpan, constraints.Max.Height)));
            items[index] = item with { Size = pass.Measure(item.Entity, in available) };
        }
        GrowAutoTracks(Rows, rows, items, Axis.Vertical);
        DistributeFractionSpace(Rows, rows, items, Axis.Vertical, constraints.Max.Height);
        foreach (MeasuredCell item in items)
        {
            GridCell cell = item.Cell;
            Box box = item.Box;
            var size = new Size(GetSpanLength(columns, cell.Column, cell.ColumnSpan), GetSpanLength(rows, cell.Row, cell.RowSpan));
            var available = new Constraints(new Size(
                float.IsNaN(box.Fixed.Width) ? MathF.Min(size.Width, box.Max.Width) : 0,
                float.IsNaN(box.Fixed.Height) ? MathF.Min(size.Height, box.Max.Height) : 0), size);
            pass.Measure(item.Entity, in available);
            pass.Place(item.Entity, new Vector2(GetTrackStart(columns, cell.Column), GetTrackStart(rows, cell.Row)));
        }
        return constraints.Constrain(new Size(GetSpanLength(columns, 0, columns.Length), GetSpanLength(rows, 0, rows.Length)));
    }

    private readonly void DistributeFractionSpace(GridTracks tracks, float[] sizes, MeasuredCell[] items, Axis axis, float available)
    {
        double weight = 0;
        foreach (GridTrack track in tracks)
            if (track.Kind == GridTrackKind.Fraction)
                weight += track.Value;
        if (weight == 0)
            return;
        double unit;
        if (float.IsFinite(available))
            unit = MathF.Max(0, available - GetSpanLength(sizes, 0, sizes.Length)) / weight;
        else
        {
            // Unbounded constraints use content to determine one shared fraction unit.
            unit = 0;
            foreach (MeasuredCell item in items)
            {
                int start = axis == Axis.Horizontal ? item.Cell.Column : item.Cell.Row;
                int count = axis == Axis.Horizontal ? item.Cell.ColumnSpan : item.Cell.RowSpan;
                double cellWeight = 0;
                for (int index = start; index < start + count; index++)
                    if (tracks[index].Kind == GridTrackKind.Fraction)
                        cellWeight += tracks[index].Value;
                if (cellWeight == 0)
                    continue;
                float content = axis == Axis.Horizontal ? item.Size.Width : item.Size.Height;
                unit = Math.Max(unit, (content - GetSpanLength(sizes, start, count)) / cellWeight);
            }
        }
        for (int index = 0; index < sizes.Length; index++)
            if (tracks[index].Kind == GridTrackKind.Fraction)
                sizes[index] = (float)(unit * tracks[index].Value);
    }

    private readonly void GrowAutoTracks(GridTracks tracks, float[] sizes, MeasuredCell[] items, Axis axis)
    {
        // Single cells establish content sizes before spanning cells distribute their extra extent.
        for (int span = 1; span <= tracks.Count; span++)
        {
            foreach (MeasuredCell item in items)
            {
                int start = axis == Axis.Horizontal ? item.Cell.Column : item.Cell.Row;
                int count = axis == Axis.Horizontal ? item.Cell.ColumnSpan : item.Cell.RowSpan;
                if (count != span)
                    continue;
                float content = axis == Axis.Horizontal ? item.Size.Width : item.Size.Height;
                float remaining = content - GetSpanLength(sizes, start, count);
                int autoCount = 0;
                for (int index = start; index < start + count; index++)
                    if (tracks[index].Kind == GridTrackKind.Auto)
                        autoCount++;
                if (remaining <= 0 || autoCount == 0)
                    continue;
                for (int index = start; index < start + count; index++)
                    if (tracks[index].Kind == GridTrackKind.Auto)
                        sizes[index] += remaining / autoCount;
            }
        }
    }

    private readonly float GetCellMaximum(GridTracks tracks, float[] sizes, int start, int count, float available)
    {
        for (int index = start; index < start + count; index++)
            if (tracks[index].Kind != GridTrackKind.Fixed)
                return available;
        return GetSpanLength(sizes, start, count);
    }

    private static float[] CreateFixedTrackSizes(GridTracks tracks)
    {
        var sizes = new float[tracks.Count];
        for (int index = 0; index < sizes.Length; index++)
            sizes[index] = tracks[index].Kind == GridTrackKind.Fixed ? tracks[index].Value : 0;
        return sizes;
    }

    private readonly float GetSpanLength(float[] sizes, int start, int count)
    {
        float length = Gap * Math.Max(0, count - 1);
        for (int index = start; index < start + count; index++)
            length += sizes[index];
        return length;
    }

    private readonly float GetTrackStart(float[] sizes, int index) => GetSpanLength(sizes, 0, index) + (index > 0 ? Gap : 0);

    private readonly record struct MeasuredCell(Entity Entity, GridCell Cell, Box Box, Size Size);
}
