namespace SomeEngine.RenderGraph;

internal static class GraphTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RowsSetAccessRow = new("rg", nameof(RowsSetAccessRow), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Dependencies = new("rg", nameof(Dependencies), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Diagnostics = new("rg", nameof(Diagnostics), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Execute = new("rg", nameof(Execute), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Rows = new("rg", nameof(Rows), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Materialize = new("rg", nameof(Materialize), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RecordSubmit = new("rg", nameof(RecordSubmit), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite BatchConstruction = new("rg", nameof(BatchConstruction), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite CommandLists = new("rg", nameof(CommandLists), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite PassDispatch = new("rg", nameof(PassDispatch), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Callbacks = new("pass", nameof(Callbacks), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite BindingResolution = new("rg", nameof(BindingResolution), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite LateReleases = new("rg", nameof(LateReleases), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite SubmitAssembly = new("rg", nameof(SubmitAssembly), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RowsSetBufferRow = new("rg", nameof(RowsSetBufferRow), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RowsSetTextureRow = new("rg", nameof(RowsSetTextureRow), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RowsSetViewRow = new("rg", nameof(RowsSetViewRow), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Schedule = new("rg", nameof(Schedule), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Frontiers = new("rg", nameof(Frontiers), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite StateCommit = new("rg", nameof(StateCommit), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite StateUpdateConstruction = new("rg", nameof(StateUpdateConstruction), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite Synchronization = new("rg", nameof(Synchronization), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite WaitCollection = new("rg", nameof(WaitCollection), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite BoundaryObservation = new("rg", nameof(BoundaryObservation), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RowsSetPassRow = new("rg", nameof(RowsSetPassRow), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite EditCommit = new("rg", nameof(EditCommit), detail: false);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite ParameterUpdates = new("rg", nameof(ParameterUpdates), detail: false);
}
