using System.Diagnostics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Layout;

internal static class GridValidation
{
    [Conditional("DEBUG")]
    internal static void CheckGap(float gap) =>
        DevelopmentCheck.Require(float.IsFinite(gap) && gap >= 0, "Grid.Gap requires a finite nonnegative value.");

    [Conditional("DEBUG")]
    internal static void CheckTracks(ReadOnlySpan<GridTrack> tracks)
    {
        foreach (GridTrack track in tracks)
        {
            DevelopmentCheck.Require(track.Kind is GridTrackKind.Fixed or GridTrackKind.Auto or GridTrackKind.Fraction,
                "GridTrack requires Fixed, Auto, or Fraction.");
            if (track.Kind == GridTrackKind.Fixed)
                DevelopmentCheck.Require(float.IsFinite(track.Value) && track.Value >= 0,
                    "GridTrack.Fixed requires a finite nonnegative length.");
            if (track.Kind == GridTrackKind.Fraction)
                DevelopmentCheck.Require(float.IsFinite(track.Value) && track.Value > 0,
                    "GridTrack.Fraction requires a finite positive weight.");
        }
    }

    [Conditional("DEBUG")]
    internal static void CheckCell(in GridCell cell, int rows, int columns)
    {
        DevelopmentCheck.Require(cell.Row >= 0 && cell.Row < rows && cell.RowSpan > 0 && cell.RowSpan <= rows - cell.Row,
            "GridCell requires a row and positive row span within Grid.Rows.");
        DevelopmentCheck.Require(cell.Column >= 0 && cell.Column < columns && cell.ColumnSpan > 0 && cell.ColumnSpan <= columns - cell.Column,
            "GridCell requires a column and positive column span within Grid.Columns.");
    }
}
