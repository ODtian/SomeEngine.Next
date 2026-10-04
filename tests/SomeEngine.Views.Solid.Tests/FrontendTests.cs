namespace SomeEngine.Views.Solid.Tests;

public sealed class FrontendTests
{
    [Fact]
    public void InsertSendsChangedStateValuesToTheSameComposeComponent() => FrontendChecks.CheckInsert();

    [Fact]
    public void ComposeRefreshesPreserveSolidStateAndItsResponsiveOutput() => FrontendChecks.CheckComponent();

    [Fact]
    public void ViewBuildRefreshesPreserveSolidStateAndItsResponsiveOutput() => FrontendChecks.CheckBuild();

    [Fact]
    public void InsertChangesBranchesAndPreservesFollowingNodesByTheirIdentity() => FrontendChecks.CheckInsertedBranches();

    [Fact]
    public void InsertPreservesChildrenOnFailureAndRecoversAfterDependencyChanges() => FrontendChecks.CheckInsertionRecovery();
}
