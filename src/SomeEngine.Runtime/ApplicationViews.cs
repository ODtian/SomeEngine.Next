using System.Numerics;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

/// <summary>Retained diagnostic camera outputs sharing the prepared Rendering world.</summary>
internal sealed class ApplicationViews(
    global::SomeEngine.RenderGraph.RenderGraph graph,
    GraphRegionId region,
    Func<ApplicationViewSettings, ClusterGraphRegions, ClusterPipelineSystem> createPipeline,
    Func<ClusterPipelineSystem, RenderFrameSystems> createSystems) : IDisposable
{
    private readonly List<View> _views = [];
    internal UiTexture[] Images { get; private set; } = [];

    internal bool Matches(IReadOnlyList<ApplicationView> views)
    {
        if (_views.Count != views.Count) return false;
        for (int index = 0; index < views.Count; index++)
            if (!ReferenceEquals(_views[index].Image, views[index]) || _views[index].Settings != views[index].Settings)
                return false;
        return true;
    }

    internal bool ChangesOutputs(IReadOnlyList<ApplicationView> views)
    {
        if (_views.Count != views.Count) return true;
        for (int index = 0; index < views.Count; index++)
            if (!SameDimensions(_views[index].Settings, views[index].Settings)) return true;
        return false;
    }

    internal UiTexture[] RetainedImages(IReadOnlyList<ApplicationView> views)
    {
        var retained = new List<UiTexture>();
        for (int index = 0; index < Math.Min(views.Count, Images.Length); index++)
            if (SameDimensions(_views[index].Settings, views[index].Settings)) retained.Add(Images[index]);
        return retained.ToArray();
    }

    internal void ReconcileSettings(IReadOnlyList<ApplicationView> views)
    {
        for (int index = _views.Count - 1; index >= views.Count; index--)
        {
            Remove(_views[index]);
            _views.RemoveAt(index);
        }
        for (int index = 0; index < views.Count; index++)
        {
            ApplicationView image = views[index];
            ApplicationViewSettings settings = image.Settings;
            if (index < _views.Count && SameDimensions(_views[index].Settings, settings))
            {
                View view = _views[index];
                view.Pipeline.SetDebugMode(settings.Mode);
                _views[index] = view with { Image = image, Settings = settings };
                continue;
            }
            if (index < _views.Count)
            {
                Remove(_views[index]);
                _views.RemoveAt(index);
            }
            _views.Insert(index, Create(image));
        }
        if (views.Count == 0) Images = [];
    }

    private View Create(ApplicationView image)
    {
        GraphRegionId parent;
        ClusterGraphRegions regions;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            parent = edit.AddRegion("Additional view", region);
            regions = new(edit.AddRegion("Geometry", parent), edit.AddRegion("Targets", parent),
                edit.AddRegion("Shading", parent), edit.AddRegion("Feedback", parent));
            edit.Commit();
        }
        ClusterPipelineSystem? pipeline = null;
        RenderFrameSystems? systems = null;
        try
        {
            pipeline = createPipeline(image.Settings, regions);
            systems = createSystems(pipeline);
            systems.Initialize();
            return new(image, image.Settings, parent, pipeline, systems);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            try { pipeline?.UninstallGraph(); } catch (Exception cleanup) { errors.Add(cleanup); }
            try { systems?.Dispose(); } catch (Exception cleanup) { errors.Add(cleanup); }
            try
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.Remove(parent);
                edit.Commit();
            }
            catch (Exception cleanup) { errors.Add(cleanup); }
            if (errors.Count != 1) throw new AggregateException("Additional view creation and cleanup failed.", errors);
            throw;
        }
    }

    private void Remove(View view)
    {
        // Consumers detach first; RenderGraph retains submitted resources until their GPU completion.
        view.Pipeline.UninstallGraph();
        view.Systems.Dispose();
        using RenderGraphEdit edit = graph.BeginEdit();
        edit.Remove(view.Region);
        edit.Commit();
    }

    private static bool SameDimensions(ApplicationViewSettings left, ApplicationViewSettings right)
        => left.Width == right.Width && left.Height == right.Height;

    internal void ReconcileGraph(ClusterShadingPath shadingPath)
    {
        if (Images.Length != _views.Count) Images = new UiTexture[_views.Count];
        for (int index = 0; index < _views.Count; index++)
        {
            View view = _views[index];
            view.Pipeline.SetShadingPath(shadingPath);
            Images[index] = new(view.Image.ImageId, view.Pipeline.ReconcileGraph().MaterialColor);
        }
    }

    internal void Record(RenderFrame renderFrame, RenderGraphFrame graphFrame, in RenderView mainView,
        in Matrix4x4 unjitteredProjection)
    {
        foreach (View view in _views)
        {
            float aspectScale = (float)mainView.ViewportWidth * view.Settings.Height /
                ((float)mainView.ViewportHeight * view.Settings.Width);
            Matrix4x4 projection = unjitteredProjection;
            projection.M11 *= aspectScale;
            Matrix4x4? geometryProjection = mainView.GeometryProjection;
            if (geometryProjection is { } geometry)
            {
                geometry.M11 *= aspectScale;
                geometryProjection = geometry;
            }
            RenderView camera = mainView with
            {
                Projection = projection,
                GeometryProjection = geometryProjection,
                ViewportWidth = checked((uint)view.Settings.Width),
                ViewportHeight = checked((uint)view.Settings.Height),
            };
            view.Pipeline.StageViews([camera]);
            view.Systems.Update(renderFrame, graphFrame);
        }
    }

    public void Dispose()
    {
        List<Exception>? errors = null;
        for (int index = _views.Count - 1; index >= 0; index--)
        {
            try
            {
                Remove(_views[index]);
                _views.RemoveAt(index);
            }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (_views.Count == 0) Images = [];
        if (errors is not null) throw new AggregateException("Additional view cleanup failed.", errors);
    }

    private sealed record View(ApplicationView Image, ApplicationViewSettings Settings, GraphRegionId Region,
        ClusterPipelineSystem Pipeline, RenderFrameSystems Systems);
}
