using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Shadows;

public sealed partial class VirtualShadowMap
{
    private readonly Dictionary<VirtualShadowLocalLight, DistantLightHistory> _distantLights = [];
    private readonly Dictionary<VirtualShadowLocalLight, DistantLightHistory> _frameDistantLights = [];
    private readonly HashSet<VirtualShadowLocalLight> _distantUpdates = [];
    private readonly VirtualShadowDistantLightUpdates _distantSchedule = new();
    private readonly record struct DistantLightHistory(int FirstView, int ViewCount);
    public float GlobalLocalResolutionLodBias { get; private set; }

    /// <summary>Accepts the completed global page-pressure result used for local shadow requests.</summary>
    public void UpdateGlobalLocalResolutionLodBias(float bias) => GlobalLocalResolutionLodBias = bias;

    private void ConfigureLocalRepresentation(ref VirtualShadowView view, in RenderView receiver, float projectionScale)
    {
        uint minimumMip = Settings.CalculateConservativeLocalMip(receiver, view.LightPosition, view.LightRange,
            projectionScale, view.ResolutionLodBias, GlobalLocalResolutionLodBias);
        view.MinMipLevel = minimumMip;
        bool distant = Settings.DistantLightMode == VirtualShadowDistantLightMode.All ||
            (Settings.DistantLightMode == VirtualShadowDistantLightMode.Conservative && minimumMip == Settings.MipLevelCount - 1u);
        if (distant)
        {
            view.MinMipLevel = Settings.MipLevelCount - 1u;
            view.Flags |= VirtualShadowViewFlags.SinglePage | VirtualShadowViewFlags.DistantLight;
        }
    }

    private void SelectDistantUpdates(RenderLightSet lights, int viewCount, ref uint generation)
    {
        _frameDistantLights.Clear();
        _distantUpdates.Clear();
        var eligible = new List<VirtualShadowLocalLight>();
        for (int index = 0; index < viewCount; index++)
        {
            VirtualShadowView view = _views[index];
            if (view.LightKind == VirtualShadowLightKind.Directional || view.FaceIndex != 0u ||
                (view.Flags & VirtualShadowViewFlags.DistantLight) == 0u) continue;
            int row = checked((int)view.LightIndex);
            Entity entity = view.LightKind == VirtualShadowLightKind.Point
                ? lights.GetPointEntity(row - lights.Directional.Count)
                : lights.GetSpotEntity(row - lights.Directional.Count - lights.Points.Count);
            var key = new VirtualShadowLocalLight(view.LightKind, entity, entity == Entity.Null ? view.LightIndex : 0u);
            int faces = view.LightKind == VirtualShadowLightKind.Point ? 6 : 1;
            if (!_pendingCacheReset && _distantLights.TryGetValue(key, out DistantLightHistory previous) &&
                previous.ViewCount == faces && previous.FirstView + faces <= _committedViews.Length &&
                view.CacheGeneration == _committedViews[previous.FirstView].CacheGeneration &&
                HasSameDistantProjection(view, _committedViews[previous.FirstView]))
            {
                eligible.Add(key);
            }
            _frameDistantLights.Add(key, new DistantLightHistory(index, faces));
        }
        foreach (VirtualShadowLocalLight light in _distantSchedule.Select(eligible.ToArray(), Settings.MaximumDistantUpdatesPerFrame))
            _distantUpdates.Add(light);
        foreach ((VirtualShadowLocalLight light, DistantLightHistory history) in _frameDistantLights.ToArray())
        {
            bool cached = eligible.Contains(light);
            bool update = _distantUpdates.Contains(light);
            for (int face = 0; face < history.ViewCount; face++)
            {
                ref VirtualShadowView view = ref _views[history.FirstView + face];
                if (cached && !update) view.Flags |= VirtualShadowViewFlags.CachedDistantLight;
                else
                {
                    view.Flags &= ~VirtualShadowViewFlags.CachedDistantLight;
                    if (cached && update)
                    {
                        generation = NextCacheGeneration(generation);
                        view.CacheGeneration = generation;
                    }
                }
            }
        }
    }

    private static bool HasSameDistantProjection(VirtualShadowView current, VirtualShadowView previous) =>
        current.LightKind == previous.LightKind && current.LightPosition == previous.LightPosition &&
        current.LightRange == previous.LightRange && current.LightViewProjection == previous.LightViewProjection &&
        current.VirtualResolution == previous.VirtualResolution && current.PageSize == previous.PageSize &&
        (previous.Flags & VirtualShadowViewFlags.DistantLight) != 0u;

    private void CommitDistantLights()
    {
        _distantSchedule.CompleteFrame(_frameDistantLights.Keys.ToArray(), _distantUpdates.ToArray());
        _distantLights.Clear();
        foreach ((VirtualShadowLocalLight light, DistantLightHistory history) in _frameDistantLights)
            _distantLights.Add(light, history);
        _frameDistantLights.Clear();
    }
}
