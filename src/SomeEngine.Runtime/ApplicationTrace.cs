using SomeEngine.Diagnostics;

namespace SomeEngine.Runtime;

internal static class ApplicationTrace
{
    internal static readonly TraceSite Scene = new("app", "Scene.Update");
    internal static readonly TraceSite Playback = new("app", "Scene.Playback");
    internal static readonly TraceSite Prepare = new("app", "Prepare");
    internal static readonly TraceSite Reconcile = new("app", "ReconcileGraph");
    internal static readonly TraceSite Execute = new("app", "ExecuteFrame");
    internal static readonly TraceSite GpuCompletion = new("wait", "GpuFrameCompletion");
    internal static readonly TraceSite TaskCompletion = new("wait", "TaskCompletion");
    internal static readonly TraceSite GameUpdate = new("app", "GameUpdate");
}
