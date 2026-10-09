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
        internal RenderDevice.TextureReference Reference { get; init; } = null!;
    }
    private readonly object _materialTextureChangeGate = new();
    private readonly HashSet<EngineTexture> _changedMaterialTextures = [];
    private readonly HashSet<EngineTexture> _materialTextureInputsToPrepare = [];
    private readonly HashSet<int> _textureScalarChanges = [];
    private readonly List<RenderDevice.TextureReference> _newTextureReferences = [];
    private bool _missingTextureInputsToPrepare;

    private ImmutableDictionary<EngineTexture, MaterialTexture> UpdateMaterialTextures(
        ref RenderGraphEdit edit)
    {
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
        var textures = _graphData.MaterialTextures;
        foreach ((EngineTexture asset, MaterialTexture previous) in _graphData.MaterialTextures)
            if (!textureInputs.ContainsKey(asset))
            {
                if (previous.RegisteredHere) edit.Remove(previous.Id);
                textures = textures.Remove(asset);
            }
        foreach ((EngineTexture asset, List<GraphParameterInput> inputs) in textureInputs)
        {
            MaterialTexture value = textures.TryGetValue(asset, out var current)
                ? current : RegisterMaterialTexture(ref edit, asset, inputs.Count);
            if (!value.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs)))
                value = value with { Inputs = inputs.ToImmutableArray(), Srv = null, Uav = null };
            textures = textures.SetItem(asset, value with { References = inputs.Count });
        }
        if (missingInputs.Count != 0)
        {
            MaterialTexture missing;
            if (GraphData.MissingMaterialTexture is { } existing) missing = existing;
            else
            {
                RenderDevice.TextureReference reference = _renderDevice.RetainMissingTexture();
                _newTextureReferences.Add(reference);
                missing = new MaterialTexture(edit.RegisterExternalTexture(reference.Resource, []), reference.Resource, 1, true) { Reference = reference };
            }
            if (!missing.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(missingInputs)))
                missing = missing with { Inputs = missingInputs.ToImmutableArray(), Srv = null };
            GraphData.MissingMaterialTexture = missing;
        }
        else GraphData.MissingMaterialTexture = GraphData.MissingMaterialTexture is { } missing
            ? missing with { Inputs = [] } : null;
        return textures;
    }

    private MaterialTexture RegisterMaterialTexture(ref RenderGraphEdit edit, EngineTexture texture, int references)
    {
        RenderDevice.TextureReference reference = _renderDevice.RetainTexture(texture);
        _newTextureReferences.Add(reference);
        RhiTexture resource = reference.Resource;
        bool registeredHere = !_graph.TryGetTextureId(resource, out GraphTextureId id);
        if (registeredHere) id = edit.RegisterExternalTexture(resource, []);
        return new(id, resource, references, registeredHere) { Reference = reference };
    }

    private void AcceptMaterialTextures(ImmutableDictionary<EngineTexture, MaterialTexture> textures,
        MaterialTexture? missing = null, bool shutdown = false)
    {
        foreach ((EngineTexture texture, MaterialTexture previous) in _graphData.MaterialTextures)
        {
            if (!textures.TryGetValue(texture, out MaterialTexture next))
            {
                texture.Changed -= MaterialTextureChanged;
                _materialTextureInputsToPrepare.Remove(texture);
            }
            if (next.Reference != previous.Reference)
                Release(previous.Reference);
        }
        foreach ((EngineTexture texture, MaterialTexture next) in textures)
        {
            if (!_graphData.MaterialTextures.TryGetValue(texture, out MaterialTexture previous))
                texture.Changed += MaterialTextureChanged;
            if (next.Reference != previous.Reference || !next.Inputs.Equals(previous.Inputs))
                _materialTextureInputsToPrepare.Add(texture);
        }
        if (_graphData.MissingMaterialTexture is { } before && before.Reference != missing?.Reference)
            Release(before.Reference);
        _missingTextureInputsToPrepare = missing is { Inputs.IsDefaultOrEmpty: false } value &&
            (_missingTextureInputsToPrepare || _graphData.MissingMaterialTexture is not { } beforeMissing ||
                !beforeMissing.Inputs.Equals(value.Inputs));
        _graphData.MaterialTextures = textures;
        _graphData.MissingMaterialTexture = missing;
        return;

        void Release(RenderDevice.TextureReference reference)
        {
            if (shutdown) reference.Dispose();
            else _graph.RetireAfterSubmittedFrames(reference);
        }
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
            foreach (EngineTexture asset in changed)
            {
                if (!_graphData.MaterialTextures.TryGetValue(asset, out MaterialTexture current)) continue;
                if (ReferenceEquals(_renderDevice.GetTexture(asset), current.Texture)) continue;
                if (current.RegisteredHere) edit.Remove(current.Id);
                textures = textures.SetItem(asset, RegisterMaterialTexture(ref edit, asset, current.References) with { Inputs = current.Inputs });
            }
            if (_installedShadeStages is { } stages)
                foreach (int address in _materialAssignments.Current.ActiveOffsets)
                {
                    _materialAssignments.Current.TryGetEntry(address, out var material, out _);
                    if (stages.ScalarLayouts.TryGetValue(address, out var layout) && layout.TextureProperties.Any(property =>
                            material.GetTexture(property) is { IsUpdating: false } texture && changed.Contains(texture)))
                        _textureScalarChanges.Add(address);
                }
            edit.Commit();
            AcceptMaterialTextures(textures, _graphData.MissingMaterialTexture);
            foreach (EngineTexture asset in changed)
                if (textures.ContainsKey(asset)) _materialTextureInputsToPrepare.Add(asset);
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
        finally
        {
            foreach (RenderDevice.TextureReference reference in _newTextureReferences)
                if (!_graphData.MaterialTextures.Values.Any(value => ReferenceEquals(value.Reference, reference)))
                    reference.Dispose();
            _newTextureReferences.Clear();
            edit.Dispose();
        }
    }

    private void UpdateMaterialTextureInputs(ref RenderGraphFrame frame)
    {
        var textures = GraphData.MaterialTextures;
        foreach (EngineTexture asset in _materialTextureInputsToPrepare.Count != 0 ? _materialTextureInputsToPrepare.ToArray() : [])
        {
            MaterialTexture material = GraphData.MaterialTextures[asset];
            if (material.Inputs.IsDefaultOrEmpty) { _materialTextureInputsToPrepare.Remove(asset); continue; }
            RenderDevice.TextureViews views = _renderDevice.PrepareTextureForInputs(ref frame, material.Inputs.AsSpan(), asset, out bool hasFrameUploads);
            TextureSrv? srv = views.Srv;
            TextureUav? uav = views.Uav;
            if (!ReferenceEquals(views.Texture, material.Texture) && _installedShadeStages is { } stages)
                foreach (int address in _materialAssignments.Current.ActiveOffsets)
                {
                    _materialAssignments.Current.TryGetEntry(address, out var value, out _);
                    if (stages.ScalarLayouts.TryGetValue(address, out var layout) &&
                        layout.TextureProperties.Any(property => ReferenceEquals(value.GetTexture(property), asset)))
                        _textureScalarChanges.Add(address);
                }
            bool registered = _graph.TryGetTextureId(views.Texture, out _);
            if (registered && !hasFrameUploads) _materialTextureInputsToPrepare.Remove(asset);
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
        if (_missingTextureInputsToPrepare && GraphData.MissingMaterialTexture is { Inputs.IsDefaultOrEmpty: false } missing)
        {
            TextureSrv srv = _renderDevice.PrepareMissingTextureForInputs(ref frame, missing.Inputs.AsSpan(), out bool hasFrameUploads);
            _missingTextureInputsToPrepare = hasFrameUploads;
            if (!ReferenceEquals(srv, missing.Srv))
            {
                foreach (GraphParameterInput input in missing.Inputs)
                    frame.SetPersistentParameterInput(input, ResourceBinding.SampledTexture(srv));
                GraphData.MissingMaterialTexture = missing with { Srv = srv };
            }
        }
    }
}
