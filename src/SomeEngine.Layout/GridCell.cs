using SomeEngine.ECS;

namespace SomeEngine.Layout;

/// <summary>Caller-written zero-based cell coordinates and positive row and column spans.</summary>
public struct GridCell : IComponent
{
    public int Row;
    public int Column;
    private int? _rowSpan;
    private int? _columnSpan;
    public int RowSpan { readonly get => _rowSpan ?? 1; set => _rowSpan = value; }
    public int ColumnSpan { readonly get => _columnSpan ?? 1; set => _columnSpan = value; }
}
