using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Layout;

public static partial class FlexFunctions
{
    public static Entity Row(float gap = 0) => Scope.View(new Flex(Axis.Horizontal, gap));
    public static Entity Column(float gap = 0) => Scope.View(new Flex(Axis.Vertical, gap));
}
