using SomeEngine.Graphics;

namespace SomeEngine.Resources;

internal interface ITextureDeviceData
{
    SomeEngine.Graphics.Texture Texture { get; }
    uint FirstAvailableMip { get; }
    bool IsValid { get; }
    bool TryAttach(Texture texture, Texture? replacement);
    void ClearUpdateReferences();
    void Release();
    void ConfirmUpdate();
    void CancelUpdate();
}

internal sealed class TextureDevices
{
    internal readonly object Gate = new();
    internal readonly Dictionary<Device, ITextureDeviceData> Data = [];

    ~TextureDevices()
    {
        foreach (ITextureDeviceData data in Data.Values)
            data.Release();
    }
}

public sealed partial class Texture
{
    private readonly TextureDevices _devices = new();
    private object _deviceGate => _devices.Gate;
    private ulong _allocationRevision;

    public bool IsUpdating { get { lock (_deviceGate) return _updateCompletion is not null; } }
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
        Task completion;
        bool commit;
        lock (_deviceGate)
        {
            if (_updateCompletion is not null)
                throw new InvalidOperationException("A texture content update is already in progress.");
            replacement.Revision = checked(Revision + 1);
            _replacement = replacement;
            _updateDevices = [];
            _updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _updateCompletion.Task;
            foreach ((Device device, ITextureDeviceData data) in _devices.Data.ToArray())
            {
                if (data.TryAttach(this, replacement)) _updateDevices.Add(device);
                else
                {
                    _devices.Data.Remove(device);
                    _allocationRevision++;
                }
            }
            commit = _updateDevices.Count == 0;
        }
        if (commit) CommitUpdate();
        return completion;
    }

    internal void CompleteDeviceUpdate(Device device, Texture content)
    {
        bool commit;
        lock (_deviceGate)
        {
            if (!ReferenceEquals(_replacement, content) || _committingUpdate) return;
            _updateDevices!.Remove(device);
            commit = _updateDevices.Count == 0;
        }
        if (commit) CommitUpdate();
    }

    internal void FailDeviceUpdate(Texture content, Exception error)
    {
        ITextureDeviceData[] devices;
        TaskCompletionSource completion;
        lock (_deviceGate)
        {
            if (!ReferenceEquals(_replacement, content) || _committingUpdate) return;
            _replacement = null;
            _updateDevices = null;
            completion = _updateCompletion!;
            devices = [.. _devices.Data.Values];
            _committingUpdate = true;
        }
        List<Exception>? cleanupErrors = null;
        foreach (ITextureDeviceData data in devices)
            try { data.CancelUpdate(); }
            catch (Exception cleanup) { (cleanupErrors ??= []).Add(cleanup); }
        lock (_deviceGate)
        {
            _committingUpdate = false;
            _updateCompletion = null;
        }
        if (cleanupErrors is not null)
            completion.TrySetException(new AggregateException([error, .. cleanupErrors]));
        else if (error is OperationCanceledException canceled) completion.TrySetCanceled(canceled.CancellationToken);
        else completion.TrySetException(error);
    }

    private void CommitUpdate()
    {
        TaskCompletionSource completion;
        ITextureDeviceData[] devices;
        lock (_deviceGate)
        {
            if (_replacement is null || _committingUpdate || _updateDevices!.Count != 0) return;
            completion = _updateCompletion!;
            _committingUpdate = true;
            ApplyReplacement(this, _replacement);
            devices = [.. _devices.Data.Values];
            _replacement = null;
            _updateDevices = null;
        }
        List<Exception>? errors = null;
        foreach (ITextureDeviceData data in devices)
            try { data.ConfirmUpdate(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        lock (_deviceGate)
        {
            _committingUpdate = false;
            _updateCompletion = null;
        }
        if (errors is null) completion.TrySetResult();
        else completion.TrySetException(new AggregateException(errors));
    }

    /// <summary>Returns this texture's allocation on a particular device, when one exists.</summary>
    public bool TryGetTexture(Device device, out SomeEngine.Graphics.Texture? texture)
    {
        bool found = TryGetDeviceData(device, out ITextureDeviceData? data);
        texture = data?.Texture;
        return found;
    }

    public uint GetFirstAvailableMip(Device device) =>
        TryGetDeviceData(device, out ITextureDeviceData? data) ? data.FirstAvailableMip : MipLevelCount;

    internal bool TryGetDeviceData<TData>(Device device, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TData? data)
        where TData : class, ITextureDeviceData
    {
        bool commit = false;
        lock (_deviceGate)
        {
            if (_devices.Data.TryGetValue(device, out ITextureDeviceData? value) && value.IsValid)
            {
                data = (TData)value;
                return true;
            }
            data = null;
            if (_devices.Data.Remove(device))
            {
                _allocationRevision++;
                commit = _updateDevices?.Remove(device) == true && _updateDevices.Count == 0;
            }
        }
        if (commit) CommitUpdate();
        return false;
    }

    internal void SetDeviceData(Device device, ITextureDeviceData data)
    {
        lock (_deviceGate)
        {
            if (!data.TryAttach(this, _replacement))
                throw new ObjectDisposedException(nameof(Device));
            if (_devices.Data.TryGetValue(device, out ITextureDeviceData? previous))
                previous.ClearUpdateReferences();
            else _updateDevices?.Add(device);
            _devices.Data[device] = data;
            _allocationRevision++;
        }
    }

    internal ITextureDeviceData? RemoveDeviceData(Device device, ITextureDeviceData? expected = null)
    {
        ITextureDeviceData? data;
        bool commit;
        lock (_deviceGate)
        {
            if (!_devices.Data.TryGetValue(device, out data) || (expected is not null && !ReferenceEquals(data, expected)))
                return null;
            _devices.Data.Remove(device);
            _allocationRevision++;
            commit = _updateDevices?.Remove(device) == true && _updateDevices.Count == 0;
        }
        data.ClearUpdateReferences();
        if (commit) CommitUpdate();
        return data;
    }
}
