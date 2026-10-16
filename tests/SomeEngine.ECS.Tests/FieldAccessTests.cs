namespace SomeEngine.ECS.Tests;

public sealed class FieldAccessTests
{
    [Fact]
    public void TableFieldsUseTypedAccessThroughValuesReferencesAndProperties() => FieldAccessChecks.TableFieldsUseTypedAccessThroughValuesReferencesAndProperties();

    [Fact]
    public void SparseFieldsUseTypedAccessThroughValuesReferencesAndProperties() => FieldAccessChecks.SparseFieldsUseTypedAccessThroughValuesReferencesAndProperties();

    [Fact]
    public void ReferenceLeafWritesKeepTheCurrentObjectAndOtherFields() => FieldAccessChecks.ReferenceLeafWritesKeepTheCurrentObjectAndOtherFields();

    [Fact]
    public void ReferenceLeafWritesAdvanceTheRootWriteVersion() => FieldAccessChecks.ReferenceLeafWritesAdvanceTheRootWriteVersion();

    [Fact]
    public void SparseReferenceLeafWritesAdvanceTheRootWriteVersion() => FieldAccessChecks.SparseReferenceLeafWritesAdvanceTheRootWriteVersion();

    [Fact]
    public void QueryFieldWritesAppearInChangedQueries() => FieldAccessChecks.QueryFieldWritesAppearInChangedQueries();

    [Fact]
    public void QueryFunctionWritesTypedComponentsAndCallerState() => FieldAccessChecks.QueryFunctionWritesTypedComponentsAndCallerState();

    [Fact]
    public void QueryFunctionAndDelegatePublishTheSameVersionRules() => FieldAccessChecks.QueryFunctionAndDelegatePublishTheSameVersionRules();

    [Fact]
    public void ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval() => FieldAccessChecks.ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval();

#if DEBUG
    [Fact]
    public void QueryFieldWritesReportTheRequiredWritePermission() => FieldAccessChecks.QueryFieldWritesReportTheRequiredWritePermission();

    [Fact]
    public void QueryFieldReadsReportTheCurrentChunkRowRange() => FieldAccessChecks.QueryFieldReadsReportTheCurrentChunkRowRange();

#endif
}
