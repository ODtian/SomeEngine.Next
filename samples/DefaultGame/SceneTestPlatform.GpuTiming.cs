using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Runtime;

namespace DefaultGame;

public sealed partial class SceneTestPlatform
{
    private List<ApplicationFrameTimes>? _gpuPresentedFrames;
    private List<ApplicationFrameTiming>? _gpuMeasuredFrames;
    private List<string>? _gpuTimingFiles;
    private bool _gpuTimingCallbacksInstalled;

    private void StartGpuPassTimingCapture()
    {
        if (!CaptureGpuPassTimes) return;
        _gpuPresentedFrames = [];
        _gpuMeasuredFrames = [];
        _gpuTimingFiles = [];
        // Capture before existing consumers, so a throwing FrameCompleted
        // consumer still leaves a witness for the already-presented frame.
        // Keep ordinary multicast semantics for later +=, -= or replacement.
        Application.FrameCompleted = (Action<ApplicationFrame>)RecordGpuPresentedFrame + Application.FrameCompleted;
        Application.FrameMeasured = (Action<ApplicationFrameTiming>)RecordGpuMeasuredFrame + Application.FrameMeasured;
        _gpuTimingCallbacksInstalled = true;
    }

    private void BeginGpuPassTimingScene()
    {
        _gpuPresentedFrames?.Clear();
        _gpuMeasuredFrames?.Clear();
    }

    private void RecordGpuPresentedFrame(ApplicationFrame frame)
    {
        // F10 can be requested by the interactive frame before its reload. That
        // frame belongs to the old renderer, not to the measurement scene.
        if (IsMeasuring && _sceneStarted != 0) _gpuPresentedFrames?.Add(frame.Timing);
    }

    private void RecordGpuMeasuredFrame(ApplicationFrameTiming frame)
    {
        if (IsMeasuring && _sceneStarted != 0) _gpuMeasuredFrames?.Add(frame);
    }

    // Also called by Main's run-level finally: content acquisition can fail
    // before Runtime has started a scene and can invoke SceneCompleted.
    internal void FinishGpuPassTimingCapture() => RestoreGpuPassTimingCallbacks();

    private void RestoreGpuPassTimingCallbacks()
    {
        if (!_gpuTimingCallbacksInstalled) return;
        Application.FrameCompleted -= RecordGpuPresentedFrame;
        Application.FrameMeasured -= RecordGpuMeasuredFrame;
        _gpuTimingCallbacksInstalled = false;
        _gpuPresentedFrames = null;
        _gpuMeasuredFrames = null;
    }

    private void SaveGpuPassTimingScene()
    {
        if (!_gpuTimingCallbacksInstalled) return;
        try
        {
            // GPU callbacks are drained by Runtime's existing scene shutdown
            // before SceneCompleted/EndScene. Keep pre-window frames even when
            // the official measurement remains Incomplete.
            ApplicationFrameTimes[] presented = _gpuPresentedFrames!.ToArray();
            ApplicationFrameTiming[] measured = _gpuMeasuredFrames!.OrderBy(frame => frame.Frame).ToArray();
            int[] expected = presented.Select(frame => frame.SimulationFrame).Order().ToArray();
            int[] received = measured.Select(frame => frame.Frame).ToArray();
            List<string> issues = [];
            if (presented.Length == 0) issues.Add("No presented frames were captured for GPU diagnosis.");
            if (expected.Distinct().Count() != expected.Length)
                issues.Add("Presented simulation-frame identities are not unique.");
            if (received.Distinct().Count() != received.Length)
                issues.Add("GPU-measured simulation-frame identities are not unique.");
            if (!expected.SequenceEqual(received))
                issues.Add("GPU-measured frames do not match the presented-frame identities exactly.");
            if (measured.Select(frame => frame.Gpu.FrameIdentity).Distinct().Count() != measured.Length)
                issues.Add("GPU graph-frame identities are not unique within this scene.");
            foreach (ApplicationFrameTimes frame in presented)
                if (!ValidTime(frame.ElapsedMs))
                    issues.Add($"Presented frame {frame.SimulationFrame} has an invalid elapsed time.");
            foreach (ApplicationFrameTiming frame in measured)
            {
                if (!ValidTime(frame.CpuMs) || !ValidTime(frame.GameMs) || !ValidTime(frame.AnimationMs) ||
                    !ValidTime(frame.ExtractionMs) || !ValidTime(frame.PrepareMs) || !ValidTime(frame.ReconcileMs) ||
                    !ValidTime(frame.ExecuteMs) || !ValidTime(frame.Gpu.ElapsedMs) || frame.Gpu.Passes.Count == 0 ||
                    frame.Gpu.Passes.Any(pass => !ValidTime(pass.StartMs) || !ValidTime(pass.EndMs) ||
                        !ValidTime(pass.CommandsMs) || pass.EndMs < pass.StartMs))
                    issues.Add($"GPU-measured frame {frame.Frame} has missing passes or invalid timing values.");
            }
            bool contiguous = expected.Zip(expected.Skip(1), static (left, right) => (long)right == (long)left + 1).All(static value => value);
            string filename = $"{Path.GetFileNameWithoutExtension(_resultFileName)}-scene-{SelectedScene}-gpu-pass-times.json";
            using (var stream = new FileStream(Path.Combine(_resultDirectory!, filename), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("SchemaVersion", 1);
                writer.WriteBoolean("DiagnosticOnly", true);
                writer.WriteBoolean("DiagnosticComplete", issues.Count == 0);
                writer.WriteString("Scene", Current.Name);
                writer.WriteString("TimingScopes", "Diagnostic instrumentation: four GPU timestamps per executed pass and same-queue resolves. GpuElapsedMs is a calibrated cross-queue envelope, not a sum of pass durations. StartMs/EndMs include pass boundary barriers; CommandsMs excludes those boundary barriers. CPU and GPU callbacks are matched by simulation-frame identity. This is not a passing steady-state performance comparison.");
                writer.WriteNumber("PresentedFrameCount", presented.Length);
                writer.WriteNumber("GpuMeasuredFrameCount", measured.Length);
                writer.WriteBoolean("PresentedFramesContiguous", contiguous);
                writer.WriteString("CoverageRule", "Exact unique presented-frame identity match; no zero-based start or contiguity requirement. Canceled, unpresented iterations are absent from both callbacks.");
                writer.WriteStartArray("Issues");
                foreach (string issue in issues) writer.WriteStringValue(issue);
                writer.WriteEndArray();
                writer.WriteStartArray("PresentedFrames");
                foreach (ApplicationFrameTimes frame in presented)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("SimulationFrame", frame.SimulationFrame);
                    WriteTime(writer, "ElapsedMs", frame.ElapsedMs);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("Frames");
                foreach (ApplicationFrameTiming frame in measured)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("SimulationFrame", frame.Frame);
                    writer.WriteNumber("GraphFrameIdentity", frame.Gpu.FrameIdentity);
                    WriteTime(writer, "CpuMs", frame.CpuMs);
                    WriteTime(writer, "GameMs", frame.GameMs);
                    WriteTime(writer, "AnimationMs", frame.AnimationMs);
                    WriteTime(writer, "ExtractionMs", frame.ExtractionMs);
                    WriteTime(writer, "PrepareMs", frame.PrepareMs);
                    WriteTime(writer, "ReconcileMs", frame.ReconcileMs);
                    WriteTime(writer, "ExecuteMs", frame.ExecuteMs);
                    WriteTime(writer, "GpuElapsedMs", frame.Gpu.ElapsedMs);
                    writer.WriteStartArray("Passes");
                    for (int index = 0; index < frame.Gpu.Passes.Count; index++)
                    {
                        var pass = frame.Gpu.Passes[index];
                        writer.WriteStartObject();
                        // Ordinal is scoped to this frame. Labels are not assumed
                        // unique, and opaque GraphPassId internals are not read.
                        writer.WriteNumber("Ordinal", index);
                        writer.WriteString("Label", pass.Label);
                        writer.WriteString("Group", pass.Group);
                        writer.WriteString("QueueType", pass.QueueType.ToString());
                        writer.WriteNumber("QueueIndex", pass.QueueIndex);
                        WriteTime(writer, "StartMs", pass.StartMs);
                        WriteTime(writer, "EndMs", pass.EndMs);
                        WriteTime(writer, "CommandsMs", pass.CommandsMs);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            _gpuTimingFiles!.Add(filename);
            Console.WriteLine($"GPU pass diagnostic: {Path.Combine(_resultDirectory!, filename)}; frames={measured.Length}/{presented.Length}; complete={issues.Count == 0}.");
        }
        catch
        {
            RestoreGpuPassTimingCallbacks();
            throw;
        }
    }

    private void AddGpuPassTimingReport(JsonObject report)
    {
        if (_gpuTimingFiles is null) return;
        report["GpuPassTimingDiagnostics"] = new JsonObject
        {
            ["DiagnosticOnly"] = true,
            ["Files"] = new JsonArray(_gpuTimingFiles.Select(static file => (JsonNode?)JsonValue.Create(file)).ToArray()),
        };
    }

    private static bool ValidTime(double value) => double.IsFinite(value) && value >= 0;

    private static void WriteTime(Utf8JsonWriter writer, string name, double value)
    {
        // Invalid values are explicitly reported in Issues; retain a valid JSON
        // diagnostic rather than losing the entire failure packet to NaN/Infinity.
        if (double.IsFinite(value)) writer.WriteNumber(name, value);
        else writer.WriteNull(name);
    }
}
