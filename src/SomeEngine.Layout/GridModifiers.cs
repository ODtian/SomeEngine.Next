using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Layout;

public static class GridModifiers
{
    public static Entity Cell(this Entity entity, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Scope.Write<GridCell>(entity) = new GridCell
        {
            Row = row, Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan,
        };
        return entity;
    }
}
