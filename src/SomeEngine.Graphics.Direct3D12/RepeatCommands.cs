using SomeEngine.Diagnostics;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public bool TryRepeatCommands(CommandContext context, out RecordedCommands commands) =>
        RequireCommandContext(context, nameof(context)).TryRepeatCommands(out commands);

    private sealed partial class D3D12CommandContext
    {
        private D3D12CommandSlot? _repeatCommands;

        internal bool TryRepeatCommands(out RecordedCommands commands)
        {
            ThrowIfDisposed();
            _device.ThrowIfUnavailable();
            if (_recording is not null)
                throw new InvalidOperationException("Finish the current recording before repeating commands.");
            commands = default;
            D3D12CommandSlot? recording = _repeatCommands;
            if (recording is null || !recording.TryClaim())
            {
                return false;
            }
            bool completed = false;
            try
            {
                using var trace = EngineTrace.Begin(RhiTrace.ReuseNativeCommands);
                if (!recording.TryCaptureRepeatedCommands())
                {
                    return false;
                }
                ulong sequence = AllocateSequence();
                commands = new RecordedCommands(recording.ActivateCommands(sequence), sequence);
                completed = true;
                return true;
            }
            finally
            {
                if (!completed) recording.CompleteUse();
            }
        }
    }

    private sealed partial class D3D12CommandSlot
    {
        private readonly Dictionary<D3D12PersistentParameterBindings, D3D12PersistentParameterData> _repeatParameters = [];
        internal void TrackRepeatedParameters(D3D12PersistentParameterBindings source, D3D12PersistentParameterData data) =>
            _repeatParameters[source] = data;
        private int _repeatRestrictions;
        internal bool TryCaptureRepeatedCommands()
        {
            CachedNativeCommands commands = _nativeRecording;
            if (!commands.Complete || !commands.Data.CanRepeat) return false;
            foreach (var parameter in _repeatParameters)
                if (!ReferenceEquals(parameter.Key.CurrentData, parameter.Value)) return false;
            try
            {
                foreach (object reference in commands.References)
                {
                    switch (reference)
                    {
                        case NativeLease resource:
                            Capture(resource);
                            break;
                        case DescriptorLease descriptor:
                            Capture(descriptor);
                            break;
                        case D3D12PersistentParameterData parameters:
                            if (!TryCapturePersistentParameterData(parameters)) return false;
                            break;
                        case DescriptorGeneration descriptors:
                            // Bindless tables can change independently of graph resources.
                            if (!ReferenceEquals(Descriptors, descriptors)) return false;
                            break;
                        case D3D12OrdinaryDataChunk:
                            // These buffers belong to this command context. No new Begin has
                            // occurred, so their contents and descriptor-table storage remain intact.
                            break;
                        default:
                            return false;
                    }
                }
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
