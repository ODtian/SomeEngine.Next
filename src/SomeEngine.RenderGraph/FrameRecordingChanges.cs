namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly HashSet<GraphIdentity> _recordingFirstPasses = [];
    private readonly HashSet<int> _recordingChangeSubmissions = [];

    private bool SeparateChangedCommands()
    {
        _recordingChangeSubmissions.Clear();
        foreach (int pass in _commandChanges)
            if ((uint)pass < (uint)_submissionByPass.Length && _live[pass] && _submissionByPass[pass] >= 0)
                _recordingChangeSubmissions.Add(_submissionByPass[pass]);
        bool separated = false;
        foreach (int submission in _recordingChangeSubmissions)
        {
            bool changed = false;
            foreach (RecordingBatch batch in _submissionRecordingUnits[submission])
            {
                if (batch.Count < 2 || !CanRepeatCommands(batch.Passes[batch.Start])) continue;
                int end = batch.Start + batch.Count;
                int modified = 0;
                for (int item = batch.Start; item < end; item++)
                    if (_commandChanges.Contains(batch.Passes[item])) modified++;
                if (modified == 0 || modified == batch.Count) continue;
                bool previous = _commandChanges.Contains(batch.Passes[batch.Start]);
                for (int item = batch.Start + 1; item < end; item++)
                {
                    int pass = batch.Passes[item];
                    bool current = _commandChanges.Contains(pass);
                    if (current != previous) changed |= _recordingFirstPasses.Add(_passes[pass].Identity);
                    previous = current;
                }
            }
            if (!changed) continue;
            _recordingSubmissionChanges.Add(submission);
            separated = true;
        }
        return separated;
    }
}
