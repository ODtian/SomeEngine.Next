namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class FocusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GridNavigationUpdatesFocusAndConfirmation(bool rewritten) => FocusChecks.RunGrid(rewritten);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReleasedFocusReturnsToTheNearestFocusableAncestorOrClears(bool direct, bool focusableRoot) =>
        FocusChecks.RunRelease(direct, focusableRoot);

    [Fact]
    public void ScheduledUserNavigationChoosesItsOwnFocusAndHandlesConfirmation() => FocusChecks.RunCustomNavigation();

    [Fact]
    public void TransparentCompositionsUseTheirLayoutContentForFocus() => FocusChecks.RunCompositions();

    [Fact]
    public void RemovingFocusabilityReturnsFocusToTheAncestor() => FocusChecks.RunFocusabilityRemoval();

    [Fact]
    public void InputOrderDeterminesConfirmationTargetsAndCapture() => FocusChecks.RunInputOrder();

    [Fact]
    public void NavigationUsesTheNearestCenterAndPreviousFrameLayout() => FocusChecks.RunMovingLayout();

    [Fact]
    public void DirectionalNavigationStaysInTheFocusedCanvas() => FocusChecks.RunCanvasNavigation();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasingTheWholeFocusedBranchReturnsToASurvivingAncestor(bool focusableRoot) =>
        FocusChecks.RunBranchRelease(focusableRoot);

    [Fact]
    public void FocusReturnsAlongTheCurrentParentChainAfterReparenting() => FocusChecks.RunReparentedRelease();

    [Fact]
    public void WorldsNavigateTheirOwnFocusIndependently() => FocusChecks.RunWorlds();
}
