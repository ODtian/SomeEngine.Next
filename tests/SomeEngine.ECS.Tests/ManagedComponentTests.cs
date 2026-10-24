namespace SomeEngine.ECS.Tests;

public sealed class ManagedComponentTests
{
    [Fact]
    public void TableReferencesRemainReadableAfterCollection() =>
        ManagedComponentChecks.TableReferencesRemainReadableAfterCollection();

    [Fact]
    public void SparseReferencesRemainReadableAfterCollection() =>
        ManagedComponentChecks.SparseReferencesRemainReadableAfterCollection();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceFieldsCanBeWrittenThroughTraversal(bool sparse) =>
        ManagedComponentChecks.ReferenceFieldsCanBeWrittenThroughTraversal(sparse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingReferencesReleasesPreviousValues(bool sparse) =>
        ManagedComponentChecks.ReplacingReferencesReleasesPreviousValues(sparse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntityCopiesRetainReferencesAndCanChangeIndependently(bool sparse) =>
        ManagedComponentChecks.EntityCopiesRetainReferencesAndCanChangeIndependently(sparse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingReferencesReleasesValuesAfterRemovalFactsAreCleared(bool sparse) =>
        ManagedComponentChecks.RemovingReferencesReleasesValuesAfterRemovalFactsAreCleared(sparse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferencesSurviveGrowthAndTableChanges(bool sparse) =>
        ManagedComponentChecks.ReferencesSurviveGrowthAndTableChanges(sparse);
}
