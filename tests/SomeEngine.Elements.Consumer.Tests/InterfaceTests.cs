namespace SomeEngine.Elements.Consumer.Tests;

public sealed class InterfaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructCounterDisplaysPointerKeyboardAndGamepadInput(bool jobs) =>
        InterfaceChecks.CheckStruct(jobs);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposeCounterDisplaysPointerKeyboardAndGamepadInput(bool jobs) =>
        InterfaceChecks.CheckCompose(jobs);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwiftUICounterDisplaysPointerKeyboardAndGamepadInput(bool jobs) =>
        InterfaceChecks.CheckSwiftUI(jobs);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SolidCounterDisplaysPointerKeyboardAndGamepadInput(bool jobs) =>
        InterfaceChecks.CheckSolid(jobs);
}
