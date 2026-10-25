using SomeEngine.Diagnostics;

namespace SomeEngine.Runtime;

internal static class ApplicationTrace
{
    internal static readonly TraceSite DeviceCreation = new("startup", "DeviceCreation");
    internal static readonly TraceSite ContentWait = new("startup", "ContentWait");
    internal static readonly TraceSite Scene = new("app", "Scene.Update");
    internal static readonly TraceSite Playback = new("app", "Scene.Playback");
    internal static readonly TraceSite Prepare = new("app", "Prepare");
    internal static readonly TraceSite ExtractionApply = new("app", "ExtractionApply");
    internal static readonly TraceSite Extraction = new("someui", "GameWorld.Extract");
    internal static readonly TraceSite PrepareSystems = new("app", "PrepareSystems");
    internal static readonly TraceSite PrepareCommit = new("app", "PrepareCommit");
    internal static readonly TraceSite Reconcile = new("app", "ReconcileGraph");
    internal static readonly TraceSite Execute = new("app", "ExecuteFrame");
    internal static readonly TraceSite RenderSystems = new("app", "RenderSystems");
    internal static readonly TraceSite TemporalRecording = new("app", "TemporalRecording");
    internal static readonly TraceSite FrameCompletion = new("app", "FrameCompletion");
    internal static readonly TraceSite GpuCompletion = new("wait", "GpuFrameCompletion");
    internal static readonly TraceSite TaskCompletion = new("wait", "TaskCompletion");
    internal static readonly TraceSite GameUpdate = new("app", "GameUpdate");
    internal static readonly TraceSite FrameResources = new("app", "FrameResources");
    internal static readonly TraceSite InstanceRetirement = new("wait", "InstanceRetirement");
    internal static readonly TraceSite UiRetirement = new("wait", "UiRetirement");
    internal static readonly TraceSite GraphFrameResources = new("app", "GraphFrameResources");
    internal static readonly TraceSite WindowInput = new("app", "WindowInput");
    internal static readonly TraceSite Present = new("app", "Present");
    internal static readonly TraceSite ImageAcquire = new("app", "ImageAcquire");
}
