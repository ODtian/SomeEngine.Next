using SomeEngine.Diagnostics;

namespace SomeEngine.Runtime;

internal static class ApplicationTrace
{
    internal static readonly TraceSite DeviceCreation = new("startup", "DeviceCreation");
    internal static readonly TraceSite ContentWait = new("startup", "ContentWait");
    internal static readonly TraceSite Prepare = new("app", "Prepare");
    internal static readonly TraceSite ExtractionApply = new("app", "ExtractionApply");
    internal static readonly TraceSite Extraction = new("someui", "GameWorld.Extract");
    internal static readonly TraceSite PrepareMeshes = new("app", "PrepareMeshes");
    internal static readonly TraceSite StreamingFeedback = new("app", "StreamingFeedback");
    internal static readonly TraceSite PrepareSystems = new("app", "PrepareSystems");
    internal static readonly TraceSite PrepareCommit = new("app", "PrepareCommit");
    internal static readonly TraceSite Reconcile = new("app", "ReconcileGraph");
    internal static readonly TraceSite FrameResources = new("wait", "FrameResources");
    internal static readonly TraceSite CollectCompleted = new("app", "CollectCompleted");
    internal static readonly TraceSite AcquireImage = new("app", "AcquireImage");
    internal static readonly TraceSite Present = new("app", "Present");
    internal static readonly TraceSite Execute = new("app", "ExecuteFrame");
    internal static readonly TraceSite RenderSystems = new("app", "RenderSystems");
    internal static readonly TraceSite TemporalRecording = new("app", "TemporalRecording");
    internal static readonly TraceSite FrameCompletion = new("app", "FrameCompletion");
    internal static readonly TraceSite GpuCompletion = new("wait", "GpuFrameCompletion");
    internal static readonly TraceSite TaskCompletion = new("wait", "TaskCompletion");
    internal static readonly TraceSite GameUpdate = new("app", "GameUpdate");
}
