using System.Runtime.ExceptionServices;

namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private RecordingBatch[][] _aheadBatches = [];
    private JobHandle[] _aheadJobs = [];

    private void RecordAheadAndSubmit(int waveCount, Span<QueueCompletion> destination)
    {
        EnsureCapacity(ref _aheadBatches, waveCount);
        EnsureCapacity(ref _aheadJobs, waveCount);
        Exception? failure = null;
        try
        {
            // All graph data, barriers and parameter connections are ready here. Recording
            // reads them; submission changes only completion and boundary-state results.
            // A recording context belongs to one batch, and its job is joined before Submit.
            using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordAheadScheduleProbe))
            {
                for (int wave = 0; wave < waveCount; wave++)
                {
                    if (!_recordingParallel[wave]) continue;
                    RecordingBatch[] source = _workerRecordingUnits[wave];
                    RecordingBatch[] work = _aheadBatches[wave] ??= [];
                    EnsureCapacity(ref work, source.Length);
                    _aheadBatches[wave] = work;
                    int count = 0;
                    foreach (RecordingBatch batch in source)
                        if (!TryRepeatBatch(batch)) work[count++] = batch;
                    if (count != 0)
                        _aheadJobs[wave] = JobSystem.ScheduleParallel(
                            new RecordBatchJob(this, work, 0), count, 1);
                }
            }

            using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordAheadSubmitProbe))
            {
                SubmitLateReleases(destination);
                for (int wave = 0; wave < waveCount; wave++)
                {
                    foreach (RecordingBatch batch in _callingRecordingUnits[wave]) RecordBatch(batch);
                    if (_recordingParallel[wave]) Join(wave);
                    else
                        foreach (RecordingBatch batch in _workerRecordingUnits[wave]) RecordBatch(batch);
                    SubmitWave(wave, destination);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Do not return frame storage, dispose unsubmitted commands or report failure
            // while a later batch can still write a recording result into that storage.
            using var cleanupProbe =
                global::SomeEngine.Diagnostics.EngineTrace.Begin(RecordAheadCleanupProbe);
            for (int wave = 0; wave < waveCount; wave++)
            {
                try { Join(wave); }
                catch (Exception exception) { failure ??= exception; }
                if (_aheadBatches[wave] is { } work) Array.Clear(work);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        void Join(int wave)
        {
            JobHandle job = _aheadJobs[wave];
            _aheadJobs[wave] = default;
            job.Complete();
        }
    }
}
