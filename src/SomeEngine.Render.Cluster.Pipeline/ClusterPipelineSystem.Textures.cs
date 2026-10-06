using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialTexture(GraphTextureId Id, RhiTexture Texture, int References, bool RegisteredHere);
    private int _materialTexturesChanged;

    private ImmutableDictionary<EngineTexture, MaterialTexture> UpdateMaterialTextures(
        ref RenderGraphEdit edit, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        var changes = new Dictionary<EngineTexture, int>();
        var resources = new HashSet<EngineTexture>();
        foreach (uint bin in rasterStages.ChangedBins)
        {
            if (_installedRasterStages is not null && _installedRasterStages.TryGetBin(bin, out var before))
                Count(before.CommandBindings, -1);
            if (rasterStages.TryGetBin(bin, out var after)) Count(after.CommandBindings, 1);
        }
        foreach (uint bin in shadeStages.ChangedBins)
        {
            if (_installedShadeStages is not null && _installedShadeStages.TryGetBin(bin, out var before))
            {
                Count(before.CommandBindings, -1);
                if (before.Receiver is { } receiver) Count(receiver.CommandBindings, -1);
            }
            if (shadeStages.TryGetBin(bin, out var after))
            {
                Count(after.CommandBindings, 1);
                if (after.Receiver is { } receiver) Count(receiver.CommandBindings, 1);
            }
        }
        void Count(ClusterCommandBindingKey bindings, int change)
        {
            resources.Clear();
            bindings.CollectTextures(resources);
            foreach (EngineTexture texture in resources)
                changes[texture] = changes.GetValueOrDefault(texture) + change;
        }
        var textures = _graphData.MaterialTextures;
        foreach ((EngineTexture texture, int change) in changes)
        {
            if (change == 0) continue;
            if (textures.TryGetValue(texture, out MaterialTexture current))
            {
                int references = checked(current.References + change);
                if (references != 0) textures = textures.SetItem(texture, current with { References = references });
                else
                {
                    if (current.RegisteredHere) edit.Remove(current.Id);
                    textures = textures.Remove(texture);
                }
            }
            else textures = textures.Add(texture, RegisterMaterialTexture(ref edit, texture, change));
        }
        return textures;
    }

    private MaterialTexture RegisterMaterialTexture(ref RenderGraphEdit edit, EngineTexture texture, int references)
    {
        RhiTexture resource = _renderDevice.GetTexture(texture);
        bool registeredHere = !_graph.TryGetTextureId(resource, out GraphTextureId id);
        if (registeredHere) id = edit.RegisterExternalTexture(resource, []);
        return new(id, resource, references, registeredHere);
    }

    private void AcceptMaterialTextures(ImmutableDictionary<EngineTexture, MaterialTexture> textures)
    {
        foreach (EngineTexture texture in _graphData.MaterialTextures.Keys)
            if (!textures.ContainsKey(texture)) texture.Changed -= MaterialTextureChanged;
        foreach (EngineTexture texture in textures.Keys)
            if (!_graphData.MaterialTextures.ContainsKey(texture)) texture.Changed += MaterialTextureChanged;
        _graphData.MaterialTextures = textures;
    }

    private void MaterialTextureChanged(EngineTexture texture) => Volatile.Write(ref _materialTexturesChanged, 1);

    private void RefreshMaterialTextures()
    {
        if (Interlocked.Exchange(ref _materialTexturesChanged, 0) == 0) return;
        var textures = _graphData.MaterialTextures;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            foreach ((EngineTexture asset, MaterialTexture current) in _graphData.MaterialTextures)
            {
                if (ReferenceEquals(_renderDevice.GetTexture(asset), current.Texture)) continue;
                if (current.RegisteredHere) edit.Remove(current.Id);
                textures = textures.SetItem(asset, RegisterMaterialTexture(ref edit, asset, current.References));
            }
            edit.Commit();
            _graphData.MaterialTextures = textures;
            // A texture notification can change alpha/deformation with an unchanged
            // material binding (including in-place content). Do not retain old depth.
            _rasterRevision = checked(_rasterRevision + 1);
        }
        catch { Volatile.Write(ref _materialTexturesChanged, 1); throw; }
        finally { edit.Dispose(); }
    }
}
