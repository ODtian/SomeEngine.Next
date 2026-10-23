namespace SomeEngine.Resources;

public sealed partial class Terrain
{
    private readonly List<TerrainDataRequest> _requests = [];
    private TerrainDataRequest[]? _requestData;
    private long _requestSequence;

    internal long NextRequestSequence() => ++_requestSequence;

    internal void RetainRequest(TerrainDataRequest request)
    {
        if (request.IsSubmitted || _requests.Contains(request)) return;
        _requests.Add(request);
        _requestData = null;
    }

    internal TerrainDataRequest[] CaptureRequests()
    {
        lock (Gate)
        {
            if (_requests.RemoveAll(static request => request.IsSubmitted) != 0) _requestData = null;
            return _requestData ??= _requests.ToArray();
        }
    }

    internal void CancelRequests()
    {
        TerrainDataRequest[] requests = CaptureRequests();
        foreach (TerrainDataRequest request in requests) request.Cancel();
    }
}
