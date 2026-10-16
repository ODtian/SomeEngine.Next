namespace SomeEngine.Views.Tests;

public sealed class TypedAnimationTests
{
    [Fact]
    public void GeneratedFieldTargetsAnimateThroughTypedSpecs() => TypedAnimationChecks.GeneratedFieldTargetsAnimateThroughTypedSpecs();

    [Fact]
    public void EqualTargetsPreserveTheirElapsedProgress() => TypedAnimationChecks.EqualTargetsPreserveTheirElapsedProgress();

    [Fact]
    public void ReferencedStateReceivesOnlyThePresentedValues() => TypedAnimationChecks.ReferencedStateReceivesOnlyThePresentedValues();

    [Fact]
    public void AReversedReferenceTargetContinuesFromItsPresentedValue() => TypedAnimationChecks.AReversedReferenceTargetContinuesFromItsPresentedValue();

    [Fact]
    public void AParameterUpdateUsesTheExistingElapsedTime() => TypedAnimationChecks.AParameterUpdateUsesTheExistingElapsedTime();

    [Fact]
    public void AConsumerMotionUsesItsManagedHistoryThroughAnimate() => TypedAnimationChecks.AConsumerMotionUsesItsManagedHistoryThroughAnimate();

    [Fact]
    public void AnAwaitedReferenceTargetContinuesFromItsPresentation() => TypedAnimationChecks.AnAwaitedReferenceTargetContinuesFromItsPresentation();

    [Fact]
    public void CancelingAnAwaitedReferenceTargetKeepsTheReplacementTarget() => TypedAnimationChecks.CancelingAnAwaitedReferenceTargetKeepsTheReplacementTarget();

    [Fact]
    public void ReferencePresentationRefreshesRootAndLeafReaders() => TypedAnimationChecks.ReferencePresentationRefreshesRootAndLeafReaders();
}
