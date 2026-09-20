using SomeEngine.Graphics;

namespace SomeEngine.Resources;

internal interface ITextureDeviceData
{
    SomeEngine.Graphics.Texture Texture { get; }
    uint FirstAvailableMip { get; }
    void Release();
    void ConfirmUpdate();
    void CancelUpdate();
}

public sealed partial class Texture
{
    private readonly object _deviceGate = new();
    private readonly Dictionary<Device, ITextureDeviceData> _devices = [];

    public bool IsUpdating { get { lock (_deviceGate) return _updateCompletion is not null; } }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private Texture? _replacement;
    private HashSet<Device>? _updateDevices;
    private TaskCompletionSource? _updateCompletion;
    private bool _committingUpdate;

    internal Texture GetUploadContent()
    {
        lock (_deviceGate) return _replacement ?? this;
    }

    private Task QueueUpdate(Texture replacement)
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_updateCompletion is not null)
                throw new InvalidOperationException("A texture content update is already in progress.");
            replacement.Revision = checked(Revision + 1);
            _replacement = replacement;
            _updateDevices = [.. _devices.Keys];
            _updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task completed = _updateCompletion.Task;
            if (_updateDevices.Count == 0) _ = CommitUpdateAsync();
            return completed;
        }
    }

    internal void CompleteDeviceUpdate(Device device, Texture content)
    {
        lock (_deviceGate)
        {
            if (!ReferenceEquals(_replacement, content) || _committingUpdate) return;
            _updateDevices!.Remove(device);
            if (_updateDevices.Count == 0) _ = CommitUpdateAsync();
        }
    }

    internal void FailDeviceUpdate(Texture content, Exception error)
    {
        lock (_deviceGate)
        {
            if (!ReferenceEquals(_replacement, content) || _committingUpdate) return;
            List<Exception>? cleanupErrors = null;
            foreach (ITextureDeviceData data in _devices.Values.ToArray())
                try { data.CancelUpdate(); }
                catch (Exception cleanup) { (cleanupErrors ??= []).Add(cleanup); }
            _replacement = null;
            _updateDevices = null;
            if (cleanupErrors is not null)
                _updateCompletion!.TrySetException(new AggregateException([error, .. cleanupErrors]));
            else if (error is OperationCanceledException canceled) _updateCompletion!.TrySetCanceled(canceled.CancellationToken);
            else _updateCompletion!.TrySetException(error);
            _updateCompletion = null;
        }
    }

    private async Task CommitUpdateAsync()
    {
        TaskCompletionSource completion;
        Task cleanup;
        var errors = new List<Exception>();
        lock (_deviceGate)
        {
            if (_replacement is null || _committingUpdate) return;
            Texture replacement = _replacement;
            completion = _updateCompletion!;
            _committingUpdate = true;
            // ApplyReloadAsync switches fields and detaches the replacement's byte sources
            // before its first asynchronous cleanup. Later cancellation cannot undo commit.
            cleanup = ApplyReloadAsync(this, replacement, CancellationToken.None).AsTask();
            foreach (ITextureDeviceData data in _devices.Values)
                try { data.ConfirmUpdate(); }
                catch (Exception error) { errors.Add(error); }
            _replacement = null;
            _updateDevices = null;
        }
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        lock (_deviceGate)
        {
            _committingUpdate = false;
            _updateCompletion = null;
        }
        if (errors.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Texture content was updated, but old-content cleanup failed.", errors));
    }

    /// <summary>Returns this texture's allocation on a particular device, when one exists.</summary>
    public bool TryGetTexture(Device device, out SomeEngine.Graphics.Texture? texture)
    {
        lock (_deviceGate)
        {
            if (_devices.TryGetValue(device, out ITextureDeviceData? data))
            {
                texture = data.Texture;
                return true;
            }
            texture = null;
            return false;
        }
    }

    public uint GetFirstAvailableMip(Device device)
    {
        lock (_deviceGate)
            return _devices.TryGetValue(device, out ITextureDeviceData? data)
                ? data.FirstAvailableMip : MipLevelCount;
    }

    internal bool TryGetDeviceData<TData>(Device device, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TData? data) where TData : class, ITextureDeviceData
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            data = _devices.TryGetValue(device, out ITextureDeviceData? value) ? (TData)value : null;
            return data is not null;
        }
    }

    internal void SetDeviceData(Device device, ITextureDeviceData data)
    {
        lock (_deviceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _devices[device] = data;
        }
    }

    internal ITextureDeviceData? RemoveDeviceData(Device device)
    {
        lock (_deviceGate)
        {
            if (!_devices.Remove(device, out ITextureDeviceData? data)) return null;
            if (_updateDevices?.Remove(device) == true && _updateDevices.Count == 0)
                _ = CommitUpdateAsync();
            return data;
        }
    }

    private void ReleaseDevices()
    {
        ITextureDeviceData[] devices;
        lock (_deviceGate)
        {
            if (_replacement is not null)
                FailDeviceUpdate(_replacement, new ObjectDisposedException(nameof(Texture)));
            devices = [.. _devices.Values];
            _devices.Clear();
        }
        var errors = new List<Exception>();
        foreach (ITextureDeviceData device in devices)
            try { device.Release(); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Some device textures could not be released.", errors);
    }
}
