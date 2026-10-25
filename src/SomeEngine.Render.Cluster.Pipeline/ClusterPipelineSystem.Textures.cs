using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using EngineTexture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialTexture(GraphTextureUse Use)
    {
        internal ImmutableArray<GraphParameterInput> Inputs { get; init; }
        // Pending connection work, not a copy of the graph's accepted resource views.
        internal bool ForceRebind { get; init; }
        internal TextureVersions Versions { get; init; }
    }
    private readonly HashSet<EngineTexture> _materialTextureInputsToPrepare = [];
    private readonly HashSet<int> _textureScalarChanges = [];
    private bool _missingTextureInputsToPrepare;

    private void UpdateMaterialTextures(
        ref RenderGraphEdit edit)
    {
        var textureInputs = new Dictionary<EngineTexture, List<GraphParameterInput>>();
        var missingInputs = new List<GraphParameterInput>();
        foreach (((var material, uint property, ResourceBindingType _), GraphParameterInput input) in GraphData.MaterialInputs)
        {
            if (input.Type is not (ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav)) continue;
            if (material.GetTexture(property) is { } asset)
            {
                if (!textureInputs.TryGetValue(asset, out var members)) textureInputs.Add(asset, members = []);
                members.Add(input);
            }
            else if (input.Type == ResourceBindingType.TextureUav)
                throw new InvalidDataException($"Material '{material.Name}' property {property} requires a Texture for writable access.");
            else missingInputs.Add(input);
        }
        var textures = _graphData.MaterialTextures;
        foreach ((EngineTexture asset, MaterialTexture previous) in _graphData.MaterialTextures)
            if (!textureInputs.ContainsKey(asset))
            {
                edit.ReleaseExternalTextureUse(previous.Use);
                textures = textures.Remove(asset);
            }
        foreach ((EngineTexture asset, List<GraphParameterInput> inputs) in textureInputs)
        {
            MaterialTexture value = textures.TryGetValue(asset, out var current)
                ? current : RegisterMaterialTexture(ref edit, _renderDevice.RetainTexture(asset)) with
                { Versions = _resources.World.ReadTextureVersions(asset) };
            if (!value.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs)))
                value = value with { Inputs = inputs.ToImmutableArray(), ForceRebind = true };
            textures = textures.SetItem(asset, value);
        }
        if (missingInputs.Count != 0)
        {
            MaterialTexture missing;
            if (GraphData.MissingMaterialTexture is { } existing) missing = existing;
            else missing = RegisterMaterialTexture(ref edit, _renderDevice.RetainMissingTexture());
            if (!missing.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(missingInputs)))
                missing = missing with { Inputs = missingInputs.ToImmutableArray(), ForceRebind = true };
            GraphData.MissingMaterialTexture = missing;
        }
        else if (GraphData.MissingMaterialTexture is { } missing)
        {
            edit.ReleaseExternalTextureUse(missing.Use);
            GraphData.MissingMaterialTexture = null;
        }
        GraphData.MaterialTextures = textures;
    }

    private static MaterialTexture RegisterMaterialTexture(
        ref RenderGraphEdit edit, RenderDevice.TextureReference reference)
    {
        try
        {
            return new(edit.AcquireExternalTextureUse(reference.Resource, [], reference)) { ForceRebind = true };
        }
        catch
        {
            // An unsuccessful acquire has not transferred the reference to the edit.
            reference.Dispose();
            throw;
        }
    }

    private void AcceptMaterialTextures(ImmutableDictionary<EngineTexture, MaterialTexture> textures,
        MaterialTexture? missing = null)
    {
        // The accepted edit still compares against the previously published graph data.
        // RenderWorld extraction owns texture versions; no second Changed subscription is needed.
        if (!ReferenceEquals(textures, _graphData.MaterialTextures))
        {
            foreach (EngineTexture texture in _graphData.MaterialTextures.Keys)
                if (!textures.ContainsKey(texture)) _materialTextureInputsToPrepare.Remove(texture);
            foreach ((EngineTexture texture, MaterialTexture next) in textures)
                if (!_graphData.MaterialTextures.TryGetValue(texture, out MaterialTexture previous) ||
                    next.Use != previous.Use || next.Versions != previous.Versions ||
                    !next.Inputs.Equals(previous.Inputs))
                    _materialTextureInputsToPrepare.Add(texture);
        }
        _missingTextureInputsToPrepare = missing is { Inputs.IsDefaultOrEmpty: false } value &&
            (_missingTextureInputsToPrepare || _graphData.MissingMaterialTexture is not { } beforeMissing ||
                !beforeMissing.Inputs.Equals(value.Inputs));
    }

    private void RefreshMaterialTextures()
    {
        var changed = new HashSet<EngineTexture>();
        foreach ((EngineTexture texture, MaterialTexture value) in _graphData.MaterialTextures)
            if (_resources.World.ReadTextureVersions(texture) != value.Versions ||
                !ReferenceEquals(_renderDevice.GetTexture(texture), _graph.GetExternalTexture(value.Use.Texture)))
                changed.Add(texture);
        if (changed.Count == 0) return;
        var textures = _graphData.MaterialTextures;
        EditGraph((ref RenderGraphEdit edit) =>
        {
            foreach (EngineTexture asset in changed)
            {
                MaterialTexture current = textures[asset];
                if (!ReferenceEquals(_renderDevice.GetTexture(asset), _graph.GetExternalTexture(current.Use.Texture)))
                {
                    edit.ReleaseExternalTextureUse(current.Use);
                    current = RegisterMaterialTexture(ref edit, _renderDevice.RetainTexture(asset)) with
                        { Inputs = current.Inputs };
                }
                textures = textures.SetItem(asset, current with { Versions = _resources.World.ReadTextureVersions(asset) });
            }
            GraphData.MaterialTextures = textures;
        }, () =>
        {
            if (_installedShadeStages is { } stages)
                foreach (int address in _materialAssignments.Current.ActiveOffsets)
                {
                    _materialAssignments.Current.TryGetEntry(address, out var material, out _);
                    if (stages.ScalarLayouts.TryGetValue(address, out var layout) && layout.TextureProperties.Any(property =>
                            material.GetTexture(property) is { } texture && changed.Contains(texture)))
                        _textureScalarChanges.Add(address);
                }
            // Same-backing content changes also invalidate scalar preparation and retained depth.
            foreach (EngineTexture asset in changed) _materialTextureInputsToPrepare.Add(asset);
            _rasterRevision = checked(_rasterRevision + 1);
            RecordTextureCasterChanges(changed);
        });
    }

    private void UpdateMaterialTextureInputs(ref RenderGraphFrame frame)
    {
        foreach (EngineTexture asset in _materialTextureInputsToPrepare.Count != 0 ? _materialTextureInputsToPrepare.ToArray() : [])
        {
            MaterialTexture material = GraphData.MaterialTextures[asset];
            if (material.Inputs.IsDefaultOrEmpty) { _materialTextureInputsToPrepare.Remove(asset); continue; }
            RenderDevice.TextureViews views = _renderDevice.PrepareTextureForInputs(ref frame, material.Inputs.AsSpan(), asset, out bool hasFrameUploads);
            bool sameBacking = ReferenceEquals(views.Texture, _graph.GetExternalTexture(material.Use.Texture));
            if (!sameBacking && _installedShadeStages is { } stages)
                foreach (int address in _materialAssignments.Current.ActiveOffsets)
                {
                    _materialAssignments.Current.TryGetEntry(address, out var value, out _);
                    if (stages.ScalarLayouts.TryGetValue(address, out var layout) &&
                        layout.TextureProperties.Any(property => ReferenceEquals(value.GetTexture(property), asset)))
                        _textureScalarChanges.Add(address);
                }
            bool registered = sameBacking || _graph.TryGetTextureId(views.Texture, out _);
            views.BindInputs(ref frame, material.Inputs.AsSpan(), registered, material.ForceRebind);
            if (registered && material.ForceRebind)
                GraphData.MaterialTextures = GraphData.MaterialTextures.SetItem(asset, material with { ForceRebind = false });
            if (registered && !hasFrameUploads) _materialTextureInputsToPrepare.Remove(asset);
        }
        if (_missingTextureInputsToPrepare && GraphData.MissingMaterialTexture is { Inputs.IsDefaultOrEmpty: false } missing)
        {
            TextureSrv srv = _renderDevice.PrepareMissingTextureForInputs(ref frame, missing.Inputs.AsSpan(), out bool hasFrameUploads);
            new RenderDevice.TextureViews(srv.Resource, srv, null).BindInputs(
                ref frame, missing.Inputs.AsSpan(), persistent: true, forceRebind: missing.ForceRebind);
            if (missing.ForceRebind)
                GraphData.MissingMaterialTexture = missing with { ForceRebind = false };
            _missingTextureInputsToPrepare = hasFrameUploads;
        }
    }
}
