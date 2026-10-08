using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

public enum ApplicationFailure : byte
{
    None,
    Startup,
    ContentLoad,
    Graphics,
    DeviceLost,
    Unsupported,
    FrameTimeout,
    SceneAction,
    ImageCallback,
    Rendering,
}

public sealed record ApplicationResult(ApplicationFailure Failure, int CompletedFrames, Exception? Error = null)
{
    public bool Succeeded => Failure == ApplicationFailure.None;
}

internal sealed class ApplicationRunException(ApplicationFailure failure, int completedFrames, Exception inner)
    : Exception(inner.Message, inner)
{
    internal ApplicationFailure Failure { get; } = failure;
    internal int CompletedFrames { get; } = completedFrames;

    internal static ApplicationFailure Classify(Exception error, ApplicationFailure fallback)
    {
        if (error is ClusterWorkOverflowException) return ApplicationFailure.Rendering;
        if (error is GraphicsException graphics)
            return graphics.Error == GraphicsError.DeviceLost ? ApplicationFailure.DeviceLost : ApplicationFailure.Graphics;
        if (error is NotSupportedException or DllNotFoundException) return ApplicationFailure.Unsupported;
        if (error is TimeoutException) return ApplicationFailure.FrameTimeout;
        if (error is ApplicationRunException run) return run.Failure;
        if (error is AggregateException aggregate) return Classify(aggregate.InnerExceptions[0], fallback);
        return fallback;
    }
}
