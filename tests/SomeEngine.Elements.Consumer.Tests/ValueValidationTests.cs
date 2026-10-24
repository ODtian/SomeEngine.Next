using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class ValueValidationTests
{
#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ValueControlsUseTheSharedDevelopmentScopeCheck(int control)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (control)
            {
                case 0: new Toggle(false).Build(); break;
                case 1: new Slider(0.5f).Build(); break;
                case 2: new Rewrites.Knob(0.5f).Build(); break;
            }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ValueDeclarationReportsInvalidCallerDataThroughDevelopmentCheck(int input)
    {
        var range = new ValueRange(0.5f);
        Vector2 direction = Vector2.UnitX;
        float distance = 148;
        switch (input)
        {
            case 0: range.Value = float.NaN; break;
            case 1: range.Maximum = -1; break;
            case 2: range.Step = -0.1f; break;
            case 3: direction = Vector2.Zero; break;
            case 4: distance = 0; break;
            case 5: range.Value = 2; break;
        }
        Assert.Throws<InvalidOperationException>(() =>
            ValueFunctions.ValueDraggable(Entity.Null, in range, direction, distance));
    }
#endif
}
