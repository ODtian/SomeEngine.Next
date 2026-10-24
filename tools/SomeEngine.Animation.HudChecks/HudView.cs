using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Animation.HudChecks;

internal readonly record struct HudView(State<int> Frame, State<float>[] Values) : IView, IComponent
{
    public BuildTask Build()
    {
        using var content = Scope.View(new Absolute(), 0).Content();
        for (int index = 0; index < Values.Length; index++)
        {
            int item = index;
            Entity node = Scope.View(new Absolute(), (ulong)index + 1).Size(104, 60)
                .Position(24 + index % 8 * 152, 24 + index / 8 * 80)
                .Background(new Color(0.1f, 0.2f, 0.5f));
            Scope.Ensure<HudValue>(node);
            Scope.Write<HudValue>(node).Value = Values[index];
            node.Animate(SurfaceChannels.Fill, Spec.Tween<Color, ColorOps, Linear>(0.15f));
            node.Animate(SurfaceChannels.CornerRadius, Spec.Tween<float, FloatOps, CubicOut>(0.15f));
            node.Animate(HudValueChannels.ValueValue, Spec.Tween<float, FloatOps, Linear>(0.15f));
            Flip.Enable(node, Spec.Tween(0.15f));
            State<int> frame = Frame;
            Scope.Compute(() =>
            {
                int number = frame.Value;
                float target = (number + item) % 128 / 127f;
                node.Background(new Color(target, 0.2f, 1 - target));
                Scope.Set(node, SurfaceChannels.CornerRadius, 4 + target * 12);
                Scope.Set(node, HudValueChannels.ValueValue, target);
                int position = (number / 16 & 1) == 0 ? item : 63 - item;
                node.Position(24 + position % 8 * 152, 24 + position / 8 * 80);
            });
        }
        return default;
    }
}

[GenerateFields]
[ComponentField("Value.Value", Name = "ValueValue")]
internal struct HudValue : IComponent
{
    public State<float> Value;
}
