using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Commands
{
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly Queue<CommandBuffer> _ready = new();
    private CommandBuffer? _recording;
    private bool _recordingCanReenterHook;
    private CommandBuffer? _candidateOverlay;
    private CommandBuffer? _hookRecording;
    private long _hookRecordingEpoch;
    private CommandBuffer? _reservedPlayback;
    private bool _capturingCandidate;
    private int _candidateOwnerThreadId;

    internal object Gate => _gate;

    internal object FlushGate => _flushGate;

    internal void Dispose()
    {
        lock (_gate)
        {
            var buffers = new List<CommandBuffer>();
            AddDistinct(buffers, _recording);
            AddDistinct(buffers, _candidateOverlay);
            AddDistinct(buffers, _hookRecording);
            AddDistinct(buffers, _reservedPlayback);
            while (_ready.Count != 0)
                AddDistinct(buffers, _ready.Dequeue());

            _recording = null;
            _candidateOverlay = null;
            _hookRecording = null;
            _reservedPlayback = null;
            _recordingCanReenterHook = false;
            _hookRecordingEpoch = 0;
            _capturingCandidate = false;
            _candidateOwnerThreadId = 0;

            for (int i = 0; i < buffers.Count; i++)
                buffers[i].DisposeOwnedUnderGate();
        }
    }

    private static void AddDistinct(
        List<CommandBuffer> buffers,
        CommandBuffer? candidate)
    {
        if (candidate is null)
            return;
        for (int i = 0; i < buffers.Count; i++)
        {
            if (ReferenceEquals(buffers[i], candidate))
                return;
        }
        buffers.Add(candidate);
    }

    internal CommandBuffer Get(World world)
    {
        lock (_gate)
            return GetUnderGate(world);
    }

    internal DeferredCommandWriter GetHookWriter(World world, HookCommandToken token)
    {
        lock (_gate)
        {
            // Reject an expired/leaked token before even allocating or selecting a wave.
            world.HookStore.ValidateCommandToken(token);
            CommandBuffer buffer;
            if (_capturingCandidate &&
                _candidateOwnerThreadId == Environment.CurrentManagedThreadId)
            {
                buffer = _candidateOverlay ??= new CommandBuffer(world, worldOwned: true);
            }
            else
            {
                buffer = GetOrCreateHookRecordingUnderGate(world, token);
            }
            world.ValidateHookCommandBufferRecordAccessUnderGate(buffer, token);
            return new DeferredCommandWriter(buffer, token);
        }
    }

    private CommandBuffer GetOrCreateHookRecordingUnderGate(
        World world,
        HookCommandToken token)
    {
        if (_hookRecording is not null)
        {
            if (_hookRecordingEpoch != token.Epoch)
            {
                throw new InvalidOperationException(
                    "A different immediate hook still owns the deferred command recording wave.");
            }
            return _hookRecording;
        }

        // Consecutive immediate callbacks with no intervening Flush share one next-wave image.
        // Re-pin the unsealed hook-origin recording instead of charging one wave per callback.
        if (_recordingCanReenterHook && _recording is not null)
        {
            _ready.EnsureCapacity(checked(_ready.Count + 1));
            _hookRecording = _recording;
            _hookRecordingEpoch = token.Epoch;
            _recording = null;
            _recordingCanReenterHook = false;
            return _hookRecording;
        }

        // Prepare both the optional pre-hook wave rotation and the hook wave's exit publication.
        // ExitExecution can then publish without allocating and without masking a hook exception.
        int additions = _recording is null ? 1 : 2;
        _ready.EnsureCapacity(checked(_ready.Count + additions));
        var hookRecording = new CommandBuffer(world, worldOwned: true);
        if (_recording is not null)
        {
            _recording.SealOwnedForPlaybackUnderGate();
            _ready.Enqueue(_recording);
            _recording = null;
            _recordingCanReenterHook = false;
        }

        _hookRecording = hookRecording;
        _hookRecordingEpoch = token.Epoch;
        return hookRecording;
    }

    internal void EndHookWriter(HookCommandToken token)
    {
        lock (_gate)
        {
            if (_hookRecording is null)
                return;
            if (_hookRecordingEpoch != token.Epoch)
            {
                throw new InvalidOperationException(
                    "The immediate hook command recording epoch changed before hook exit.");
            }

            CommandBuffer recording = _hookRecording;
            _hookRecording = null;
            _hookRecordingEpoch = 0;
            if (!recording.HasRecordedCommandsUnderGate)
            {
                recording.DisposeOwnedUnderGate();
                return;
            }

            if (_recording is null)
            {
                _recording = recording;
                _recordingCanReenterHook = true;
            }
            else
            {
                recording.SealOwnedForPlaybackUnderGate();
                _ready.Enqueue(recording);
            }
        }
    }

    private CommandBuffer GetUnderGate(World world)
    {
        if (_capturingCandidate &&
            _candidateOwnerThreadId == Environment.CurrentManagedThreadId)
        {
            return _candidateOverlay ??= new CommandBuffer(world, worldOwned: true);
        }

        if (_recording is null)
        {
            _recording = new CommandBuffer(world, worldOwned: true);
            _recordingCanReenterHook = false;
        }
        return _recording;
    }

    internal void RequireCurrentHookBufferUnderGate(
        CommandBuffer buffer,
        HookCommandToken token)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        bool current;
        if (_capturingCandidate)
        {
            current = _candidateOwnerThreadId == Environment.CurrentManagedThreadId &&
                      ReferenceEquals(_candidateOverlay, buffer);
        }
        else
        {
            current = _hookRecordingEpoch == token.Epoch &&
                      ReferenceEquals(_hookRecording, buffer);
        }

        if (!current)
        {
            throw new InvalidOperationException(
                "The deferred command writer no longer targets the current hook-owned command wave.");
        }
    }

    internal void BeginCandidate()
    {
        lock (_gate)
        {
            if (_capturingCandidate)
                throw new InvalidOperationException("Nested command overlays are not supported.");

            _candidateOverlay = null;
            _candidateOwnerThreadId = Environment.CurrentManagedThreadId;
            _capturingCandidate = true;
        }
    }

    internal void EndCandidate(bool published)
    {
        lock (_gate)
        {
            if (!_capturingCandidate)
                throw new InvalidOperationException("No command overlay is active.");
            RequireCandidateOwner();

            CommandBuffer? overlay = _candidateOverlay;
            _candidateOverlay = null;
            _capturingCandidate = false;
            _candidateOwnerThreadId = 0;

            if (overlay is null)
                return;

            if (!published)
            {
                overlay.DisposeOwnedUnderGate();
                return;
            }

            // Hook commands are always a later wave. Freeze any direct recording wave ahead of
            // the overlay so publication never appends into a buffer that playback may enumerate.
            if (_recording is not null)
            {
                _recording.SealOwnedForPlaybackUnderGate();
                _ready.Enqueue(_recording);
                _recording = null;
                _recordingCanReenterHook = false;
            }
            overlay.SealOwnedForPlaybackUnderGate();
            _ready.Enqueue(overlay);
        }
    }

    internal void PrepareCandidatePublication()
    {
        lock (_gate)
        {
            if (!_capturingCandidate)
                throw new InvalidOperationException("No command overlay is active.");
            RequireCandidateOwner();
            if (_candidateOverlay is null)
                return;

            // A non-owner thread may mint a direct recording wave after this preparation and
            // before candidate publication. Reserve both possible FIFO enqueues now.
            const int additions = 2;
            _ready.EnsureCapacity(checked(_ready.Count + additions));
        }
    }

    private void RequireCandidateOwner()
    {
        if (_candidateOwnerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "Only the structural transaction owner may record or publish its hook command overlay.");
        }
    }

    internal bool TryReserveNextPlayback(out CommandBuffer? playback)
    {
        lock (_gate)
        {
            if (_reservedPlayback is not null)
            {
                throw new InvalidOperationException(
                    "Another command playback wave is already reserved for this World.");
            }

            while (_ready.Count != 0 && !_ready.Peek().HasRecordedCommandsUnderGate)
            {
                CommandBuffer empty = _ready.Dequeue();
                empty.DisposeOwnedUnderGate();
            }

            if (_ready.Count != 0)
            {
                playback = _ready.Peek();
            }
            else if (_recording is not null && _recording.HasRecordedCommandsUnderGate)
            {
                playback = _recording;
                // Freeze the selected recording identity at the ready frontier before waiting for
                // topology admission. New recorders therefore mint a distinct later wave, while a
                // cancelled reservation leaves the frozen wave at the same FIFO frontier.
                _ready.EnsureCapacity(checked(_ready.Count + 1));
                playback.SealOwnedForPlaybackUnderGate();
                _ready.Enqueue(playback);
                _recording = null;
                _recordingCanReenterHook = false;
            }
            else
            {
                if (_recording is not null)
                {
                    _recording.DisposeOwnedUnderGate();
                    _recording = null;
                    _recordingCanReenterHook = false;
                }
                playback = null;
                return false;
            }

            playback.ReserveOwnedPlaybackUnderGate();
            _reservedPlayback = playback;
            return true;
        }
    }

    internal void PlaybackReservedUnderExistingTopologyAdmission(CommandBuffer playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        lock (_gate)
        {
            if (!ReferenceEquals(_reservedPlayback, playback))
                throw new InvalidOperationException("The reserved command wave identity changed before playback.");

            if (_ready.Count != 0 && ReferenceEquals(_ready.Peek(), playback))
            {
                _ready.Dequeue();
            }
            else if (ReferenceEquals(_recording, playback))
            {
                _recording = null;
            }
            else
            {
                throw new InvalidOperationException(
                    "The reserved command wave is no longer at the World playback frontier.");
            }

            _reservedPlayback = null;
            try
            {
                playback.PlaybackReservedUnderExistingTopologyAdmissionUnderGate();
                playback.ClearOwnedUnderGate();
            }
            finally
            {
                playback.DisposeOwnedUnderGate();
            }
        }
    }

    internal void CancelPlaybackReservation(CommandBuffer playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        lock (_gate)
        {
            if (!ReferenceEquals(_reservedPlayback, playback))
                return;

            playback.CancelOwnedPlaybackReservationUnderGate();
            _reservedPlayback = null;
        }
    }
}


