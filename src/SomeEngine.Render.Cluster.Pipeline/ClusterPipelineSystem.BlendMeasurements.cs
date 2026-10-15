namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private int _measurementSimulationFrame;

    /// <summary>Completed blend GPU measurements, identified by the caller's simulation frame.</summary>
    public event Action<ClusterBlendMetrics>? BlendFrameCompleted;

    /// <summary>Associates the next graph submission with the application's simulation frame.</summary>
    public void SetMeasurementFrame(int simulationFrame) => _measurementSimulationFrame = simulationFrame;

    private void ReceiveBlendFrame(ClusterBlendMetrics frame) => BlendFrameCompleted?.Invoke(frame);
}
