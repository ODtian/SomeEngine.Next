using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Features.Shadows;

internal sealed class VirtualShadowFrame(VirtualShadowViews views)
{
    internal VirtualShadowViews Views = views;
    internal ReadOnlyMemory<VirtualShadowLightKey> LightKeys;
    internal Memory<PageTableRange> Ranges;
    internal uint RangeCount;
    internal bool ReadWorkTime;
    internal VirtualShadowQuality.QualityRequest? QualityRequest;
    internal Memory<RenderView> _shadowViews;
    internal Memory<VirtualShadowView> _previousShadowViews;
    internal float[] _frameBaseShadowBias = [];
    internal Memory<Matrix4x4> _previousShadowViewProjections;
    internal bool _frameHasPreviousHistory;
    internal int _viewCount;
    internal int _previousViewCount;
    internal RenderInstanceBatch? _frameInstances;
    internal ulong _frameCasterRevision;
    internal readonly List<uint> _streamingChangedInstances = [];
    internal readonly List<uint> _rasterChangedInstances = [];
    internal ulong _frameInstanceRevision;
    internal readonly List<uint> _changedInstances = [];
    internal readonly List<uint> _removedCasterRows = [];
    internal ScreenBinningExpansionResources CasterExpansion;
    internal uint _frameIndex;
    internal byte[] _frameCasterCacheAges = [];
    internal Entity[] _frameCasterEntities = [];
    internal uint _frameCasterInputVersion;
    internal readonly List<uint> _rebuiltCasterChanges = [];
    internal uint[] _frameCasterStaticBits = [];
    internal readonly List<Vector4> _lodCasterBounds = [];
    internal readonly List<uint> _returnedStaticCasters = [];
    internal VirtualShadowQuality.ShadowWorkView[] _frameWorkViews = [];
    internal float _frameWorkTimeFraction;
    internal int _frameWorkTimeFramesSinceChange;
    internal bool _frameWorkTimeClearsHistory;
}
