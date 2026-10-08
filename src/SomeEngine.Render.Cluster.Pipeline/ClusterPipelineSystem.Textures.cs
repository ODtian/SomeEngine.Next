using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialTexture(GraphTextureId Id, RhiTexture Texture, int References, bool RegisteredHere)
    {
        internal ImmutableArray<GraphParameterInput> Inputs { get; init; }
        internal TextureSrv? Srv { get; init; }
        internal TextureUav? Uav { get; init; }
    }
    private readonly object _materialTextureChangeGate = new();
    private readonly HashSet<EngineTexture> _changedMaterialTextures = [];

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
                if (before.SurfaceShader is { } receiver) Count(receiver.CommandBindings, -1);
            }
            if (shadeStages.TryGetBin(bin, out var after))
            {
                Count(after.CommandBindings, 1);
                if (after.SurfaceShader is { } receiver) Count(receiver.CommandBindings, 1);
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
        var textureInputs = new Dictionary<EngineTexture, List<GraphParameterInput>>();
        var missingInputs = new List<GraphParameterInput>();
        foreach (((var material, uint property), MaterialInput input) in GraphData.MaterialInputs)
        {
            if (input.Input.Type is not (ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav)) continue;
            if (material.GetTexture(property) is { } asset)
            {
                if (!textureInputs.TryGetValue(asset, out var members)) textureInputs.Add(asset, members = []);
                members.Add(input.Input);
            }
            else if (input.Input.Type == ResourceBindingType.TextureUav)
                throw new InvalidDataException($"Material '{material.Name}' slot '{input.Name}' requires a Texture for writable access.");
            else missingInputs.Add(input.Input);
        }
        foreach ((EngineTexture asset, List<GraphParameterInput> inputs) in textureInputs)
            textures = textures.SetItem(asset, textures[asset] with { Inputs = inputs.ToImmutableArray(), Srv = null, Uav = null });
        if (missingInputs.Count != 0)
        {
            MaterialTexture missing = GraphData.MissingMaterialTexture ??
                new MaterialTexture(edit.RegisterExternalTexture(_renderDevice.MissingTexture, []), _renderDevice.MissingTexture, 1, true);
            GraphData.MissingMaterialTexture = missing with { Inputs = missingInputs.ToImmutableArray(), Srv = null };
        }
        else GraphData.MissingMaterialTexture = GraphData.MissingMaterialTexture is { } missing
            ? missing with { Inputs = [] } : null;
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

    private void MaterialTextureChanged(EngineTexture texture)
    {
        lock (_materialTextureChangeGate) _changedMaterialTextures.Add(texture);
    }

    private void RefreshMaterialTextures()
    {
        HashSet<EngineTexture> changed;
        lock (_materialTextureChangeGate)
        {
            if (_changedMaterialTextures.Count == 0) return;
            changed = [.. _changedMaterialTextures];
            _changedMaterialTextures.Clear();
        }
        var textures = _graphData.MaterialTextures;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            foreach ((EngineTexture asset, MaterialTexture current) in _graphData.MaterialTextures)
            {
                if (ReferenceEquals(_renderDevice.GetTexture(asset), current.Texture)) continue;
                if (current.RegisteredHere) edit.Remove(current.Id);
                textures = textures.SetItem(asset, RegisterMaterialTexture(ref edit, asset, current.References) with { Inputs = current.Inputs });
            }
            edit.Commit();
            _graphData.MaterialTextures = textures;
            // A texture notification can change alpha/deformation with an unchanged
            // material binding (including in-place content). Do not retain old depth.
            _rasterRevision = checked(_rasterRevision + 1);
            RecordTextureCasterChanges(changed);
        }
        catch
        {
            lock (_materialTextureChangeGate) _changedMaterialTextures.UnionWith(changed);
            throw;
        }
        finally { edit.Dispose(); }
    }

    private void UpdateMaterialTextureInputs(ref RenderGraphFrame frame)
    {
        var textures = GraphData.MaterialTextures;
        foreach ((EngineTexture asset, MaterialTexture material) in GraphData.MaterialTextures)
        {
            if (material.Inputs.IsDefaultOrEmpty) continue;
            RenderDevice.TextureViews views = _renderDevice.PrepareTextureForInputs(ref frame, material.Inputs.AsSpan(), asset);
            TextureSrv? srv = views.Srv;
            TextureUav? uav = views.Uav;
            bool registered = _graph.TryGetTextureId(views.Texture, out _);
            if (registered && ReferenceEquals(srv, material.Srv) && ReferenceEquals(uav, material.Uav)) continue;
            foreach (GraphParameterInput input in material.Inputs)
            {
                ResourceBinding value = input.Type == ResourceBindingType.TextureSrv
                    ? ResourceBinding.SampledTexture(srv!) : ResourceBinding.StorageTexture(uav!);
                if (registered) frame.SetPersistentParameterInput(input, value);
                else frame.SetParameterInput(input, value);
            }
            if (registered) textures = textures.SetItem(asset, material with { Srv = srv, Uav = uav });
        }
        GraphData.MaterialTextures = textures;
        if (GraphData.MissingMaterialTexture is { Inputs.IsDefaultOrEmpty: false } missing)
        {
            TextureSrv srv = _renderDevice.PrepareMissingTextureForInputs(ref frame, missing.Inputs.AsSpan());
            if (!ReferenceEquals(srv, missing.Srv))
            {
                foreach (GraphParameterInput input in missing.Inputs)
                    frame.SetPersistentParameterInput(input, ResourceBinding.SampledTexture(srv));
                GraphData.MissingMaterialTexture = missing with { Srv = srv };
            }
        }
    }
}
