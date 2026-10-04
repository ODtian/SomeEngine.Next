namespace SomeEngine.Elements.Consumer.Tests;

public sealed class ValueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToggleUsesPointerAndKeyboardActivation(bool rewritten) => ValueChecks.RunToggle(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueControlsStepWithFocusedArrowKeys(bool knob) => ValueChecks.RunKeyboard(knob);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueControlsQuantizeAccumulatedDragging(bool knob) => ValueChecks.RunDrag(knob);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueControlsReachLimitsWithUnevenSteps(bool knob) => ValueChecks.RunLimits(knob);

    [Fact]
    public void AudioVolumeFlowsThroughSliderKnobAndText() => ValueChecks.RunVolume();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueControlsApplyEachDragInInputOrder(bool knob) => ValueChecks.RunDragOrder(knob);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroStepKeepsContinuousDraggingAndDefaultKeyboardSteps(bool knob) => ValueChecks.RunContinuous(knob);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualLimitsRetainTheirOnlyValue(bool knob) => ValueChecks.RunEqualRange(knob);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HalfStepDragRoundsUp(bool knob) => ValueChecks.RunHalfStep(knob);
}
