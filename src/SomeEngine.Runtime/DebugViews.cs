using System.Numerics;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

/// <summary>Independent camera outputs sharing only prepared scene data with the main view.</summary>
internal sealed class DebugViews(
    global::SomeEngine.RenderGraph.RenderGraph graph,
    GraphRegionId region,
    Func<DebugViewSettings, ClusterGraphRegions, ClusterPipelineSystem> createPipeline,
    Func<ClusterPipelineSystem, RenderFrameSystems> createSystems) : IDisposable
{
    private readonly List<Preview> _previews = [];
    private DebugViewSettings[] _settings = [];
    internal UiTexture[] Images { get; private set; } = [];
    internal IReadOnlyList<DebugViewSettings> Settings => _settings;

    internal bool Matches(ReadOnlySpan<DebugViewSettings> settings) => _settings.AsSpan().SequenceEqual(settings);

    internal bool ChangesOutputs(ReadOnlySpan<DebugViewSettings> settings)
    {
        if (settings.Length != _previews.Count) return true;
        for (int index = 0; index < settings.Length; index++)
            if (!SameDimensions(_previews[index].Settings, settings[index])) return true;
        return false;
    }

    // Keep unaffected references in the UI while outputs being replaced are detached.
    internal UiTexture[] RetainedImages(ReadOnlySpan<DebugViewSettings> settings)
    {
        var retained = new List<UiTexture>();
        for (int index = 0; index < Math.Min(settings.Length, Images.Length); index++)
            if (SameDimensions(_previews[index].Settings, settings[index])) retained.Add(Images[index]);
        return retained.ToArray();
    }

    internal void ReconcileSettings(ReadOnlySpan<DebugViewSettings> settings)
    {
        for (int index = _previews.Count - 1; index >= settings.Length; index--)
        {
            Remove(_previews[index]);
            _previews.RemoveAt(index);
        }
        for (int index = 0; index < settings.Length; index++)
        {
            DebugViewSettings item = settings[index];
            if (index < _previews.Count && SameDimensions(_previews[index].Settings, item))
            {
                Preview preview = _previews[index];
                if (preview.Settings != item)
                {
                    preview.Pipeline.SetDebugMode(item.Mode);
                    _previews[index] = preview with { Settings = item };
                }
                continue;
            }
            if (index < _previews.Count)
            {
                Remove(_previews[index]);
                _previews.RemoveAt(index);
            }
            _previews.Insert(index, Create(item));
        }
        _settings = settings.ToArray();
        if (settings.IsEmpty) Images = [];
    }

    private Preview Create(DebugViewSettings settings)
    {
        GraphRegionId parent;
        ClusterGraphRegions regions;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            parent = edit.AddRegion("Debug view", region);
            regions = new(edit.AddRegion("Geometry", parent), edit.AddRegion("Targets", parent),
                edit.AddRegion("Shading", parent), edit.AddRegion("Feedback", parent));
            edit.Commit();
        }
        ClusterPipelineSystem pipeline = createPipeline(settings, regions);
        RenderFrameSystems systems = createSystems(pipeline);
        systems.Initialize();
        return new(settings, parent, pipeline, systems);
    }

    private void Remove(Preview preview)
    {
        preview.Pipeline.UninstallGraph();
        preview.Systems.Dispose();
        using RenderGraphEdit edit = graph.BeginEdit();
        edit.Remove(preview.Region);
        edit.Commit();
    }

    private static bool SameDimensions(DebugViewSettings left, DebugViewSettings right)
        => left.Width == right.Width && left.Height == right.Height;

    internal void ReconcileGraph(ClusterShadingPath shadingPath)
    {
        if (_previews.Count == 0) return;
        var images = new UiTexture[_previews.Count];
        for (int index = 0; index < _previews.Count; index++)
        {
            _previews[index].Pipeline.SetShadingPath(shadingPath);
            images[index] = new(index + 2, _previews[index].Pipeline.ReconcileGraph().MaterialColor);
        }
        Images = images;
    }

    internal void Record(RenderReadScope readScope, RenderGraphFrame frame, in RenderView mainView,
        in Matrix4x4 unjitteredProjection)
    {
        foreach (Preview preview in _previews)
        {
            Matrix4x4 projection = unjitteredProjection;
            projection.M11 *= (float)mainView.ViewportWidth * preview.Settings.Height /
                ((float)mainView.ViewportHeight * preview.Settings.Width);
            RenderView view = mainView with
            {
                Projection = projection,
                GeometryProjection = null,
                ViewportWidth = checked((uint)preview.Settings.Width),
                ViewportHeight = checked((uint)preview.Settings.Height),
            };
            preview.Pipeline.StageViews([view]);
            preview.Systems.Update(readScope, frame);
        }
    }

    public void Dispose()
    {
        foreach (Preview preview in _previews) Remove(preview);
        _previews.Clear();
        _settings = [];
        Images = [];
    }

    private sealed record Preview(DebugViewSettings Settings, GraphRegionId Region,
        ClusterPipelineSystem Pipeline, RenderFrameSystems Systems);
}
