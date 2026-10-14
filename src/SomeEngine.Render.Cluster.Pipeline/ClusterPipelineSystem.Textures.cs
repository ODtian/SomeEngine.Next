using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialTexture(GraphTextureId Id, RhiTexture Texture, int References,
        bool RegisteredHere, TextureVersions Versions);
    private ImmutableDictionary<EngineTexture, MaterialTexture> _materialTextures =
        ImmutableDictionary<EngineTexture, MaterialTexture>.Empty;

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
                Count(before.CommandBindings, -1);
            if (shadeStages.TryGetBin(bin, out var after)) Count(after.CommandBindings, 1);
        }
        void Count(ClusterCommandBindingKey bindings, int change)
        {
            resources.Clear();
            bindings.CollectTextures(resources);
            foreach (EngineTexture texture in resources)
                changes[texture] = changes.GetValueOrDefault(texture) + change;
        }
        var textures = _materialTextures;
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
        return RefreshMaterialTextures(ref edit, textures);
    }

    private MaterialTexture RegisterMaterialTexture(ref RenderGraphEdit edit, EngineTexture texture, int references)
    {
        RhiTexture resource = _renderDevice.GetTexture(texture);
        bool registeredHere = !_graph.TryGetTextureId(resource, out GraphTextureId id);
        if (registeredHere) id = edit.RegisterExternalTexture(resource, []);
        return new(id, resource, references, registeredHere, _renderWorld.ReadTextureVersions(texture));
    }

    private void AcceptMaterialTextures(ImmutableDictionary<EngineTexture, MaterialTexture> textures)
    {
        _materialTextures = textures;
    }

    private void RefreshMaterialTextures()
    {
        bool changed = _materialTextures.Any(pair =>
        {
            TextureVersions version = _renderWorld.ReadTextureVersions(pair.Key);
            return version != pair.Value.Versions || version.Content != version.Replacement;
        });
        if (!changed) return;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            var textures = RefreshMaterialTextures(ref edit, _materialTextures);
            edit.Commit();
            AcceptMaterialTextures(textures);
        }
        finally { edit.Dispose(); }
    }

    private ImmutableDictionary<EngineTexture, MaterialTexture> RefreshMaterialTextures(
        ref RenderGraphEdit edit, ImmutableDictionary<EngineTexture, MaterialTexture> textures)
    {
        foreach ((EngineTexture asset, MaterialTexture current) in textures)
        {
            TextureVersions version = _renderWorld.ReadTextureVersions(asset);
            if (version == current.Versions && version.Content == version.Replacement) continue;
            if (ReferenceEquals(_renderDevice.GetTexture(asset), current.Texture))
                textures = textures.SetItem(asset, current with { Versions = version });
            else
            {
                if (current.RegisteredHere) edit.Remove(current.Id);
                textures = textures.SetItem(asset, RegisterMaterialTexture(ref edit, asset, current.References));
            }
        }
        return textures;
    }
}
