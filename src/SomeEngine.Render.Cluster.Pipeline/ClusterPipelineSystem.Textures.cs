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
    private IReadOnlyDictionary<EngineTexture, TextureVersions>? _materialTextureVersions;
    private bool _missingTextureInputsToPrepare;

    private (Dictionary<EngineTexture, List<GraphParameterInput>> Textures, List<GraphParameterInput> Missing)
        CollectMaterialTextureInputs()
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
        return (textureInputs, missingInputs);
    }

    private void UpdateMaterialTextures(ref RenderGraphEdit edit)
    {
        var (textureInputs, missingInputs) = CollectMaterialTextureInputs();
        UpdateMaterialTextures(ref edit, textureInputs, missingInputs);
    }

    private void UpdateMaterialTextures(ref RenderGraphEdit edit,
        Dictionary<EngineTexture, List<GraphParameterInput>> textureInputs, List<GraphParameterInput> missingInputs)
    {
        var textures = _graphData.MaterialTextures;
        IReadOnlyDictionary<EngineTexture, TextureVersions> versions = _resources.World.ReadTextureVersions();
        foreach ((EngineTexture asset, MaterialTexture previous) in _graphData.MaterialTextures)
            if (!textureInputs.ContainsKey(asset))
            {
                edit.ReleaseExternalTextureUse(previous.Use);
                textures = textures.Remove(asset);
            }
        foreach ((EngineTexture asset, List<GraphParameterInput> inputs) in textureInputs)
        {
            TextureVersions textureVersions = versions[asset];
            MaterialTexture value;
            if (textures.TryGetValue(asset, out var current))
            {
                value = current;
                if (!ReferenceEquals(_renderDevice.GetTexture(asset), _graph.GetExternalTexture(current.Use.Texture)))
                {
                    edit.ReleaseExternalTextureUse(current.Use);
                    value = RegisterMaterialTexture(ref edit, _renderDevice.RetainTexture(asset)) with
                        { Inputs = current.Inputs };
                }
            }
            else value = RegisterMaterialTexture(ref edit, _renderDevice.RetainTexture(asset));
            value = value with { Versions = textureVersions };
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
        GraphData.MaterialTextureDecalVersion = GraphData.Decals!.Version;
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

    private HashSet<EngineTexture>? CollectMaterialTextureChanges()
    {
        HashSet<EngineTexture>? changed = null;
        foreach ((EngineTexture texture, MaterialTexture next) in GraphData.MaterialTextures)
            if (_graphData.MaterialTextures.TryGetValue(texture, out MaterialTexture previous) &&
                (next.Use != previous.Use || next.Versions != previous.Versions))
                (changed ??= []).Add(texture);
        return changed;
    }

    private void RefreshMaterialTextures()
    {
        IReadOnlyDictionary<EngineTexture, TextureVersions> versions = _resources.World.ReadTextureVersions();
        uint decalVersion = GraphData.Decals!.Version;
        if (_graphData.MaterialTextureDecalVersion == decalVersion)
        {
            if (ReferenceEquals(versions, _materialTextureVersions)) return;
            // Resource-property revisions keep the accepted texture references current.
            // In-place Texture updates retain their keys and change versions or allocations.
            HashSet<EngineTexture>? versionsChanged = null;
            foreach ((EngineTexture texture, MaterialTexture value) in _graphData.MaterialTextures)
                if (versions[texture] != value.Versions)
                    (versionsChanged ??= []).Add(texture);
            if (versionsChanged is not null) RefreshMaterialTextureVersions(versionsChanged);
            _materialTextureVersions = versions;
            return;
        }
        RefreshMaterialTextureBindings(decalVersion, versions);
        _materialTextureVersions = versions;
    }

    private void RefreshMaterialTextureVersions(HashSet<EngineTexture> changed) =>
        EditGraph((ref RenderGraphEdit edit) => UpdateMaterialTextures(ref edit),
            () => AcceptMaterialTextureChanges(changed));

    private void RefreshMaterialTextureBindings(uint decalVersion, IReadOnlyDictionary<EngineTexture, TextureVersions> versions)
    {
        var (textureInputs, missingInputs) = CollectMaterialTextureInputs();
        HashSet<EngineTexture>? changed = null;
        foreach (EngineTexture texture in _graphData.MaterialTextures.Keys)
            if (!textureInputs.ContainsKey(texture)) (changed ??= []).Add(texture);
        foreach ((EngineTexture texture, List<GraphParameterInput> inputs) in textureInputs)
            if (!_graphData.MaterialTextures.TryGetValue(texture, out MaterialTexture previous) ||
                !previous.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs)) ||
                versions[texture] != previous.Versions)
                (changed ??= []).Add(texture);
        bool missingChanged = _graphData.MissingMaterialTexture is { } missing
            ? !missing.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(missingInputs))
            : missingInputs.Count != 0;
        if (changed is null && !missingChanged)
        {
            // The edit changed no texture association, so no graph mutation is needed.
            GraphData.MaterialTextureDecalVersion = decalVersion;
            return;
        }
        EditGraph((ref RenderGraphEdit edit) => UpdateMaterialTextures(ref edit, textureInputs, missingInputs),
            () => AcceptMaterialTextureChanges(changed));
    }

    private void AcceptMaterialTextureChanges(HashSet<EngineTexture>? changed)
    {
        if (changed is null) return;
        if (_installedShadeStages is { } stages)
            foreach (int address in _materialAssignments.Current.ActiveOffsets)
            {
                _materialAssignments.Current.TryGetEntry(address, out var material, out _);
                if (stages.ScalarLayouts.TryGetValue(address, out var layout) && layout.TextureProperties.Any(property =>
                        material.GetTexture(property) is { } texture && changed.Contains(texture)))
                    _textureScalarChanges.Add(address);
            }
        // AcceptMaterialTextures already queues the accepted inputs and removes retired ones.
        // Same-backing content changes refresh scalar preparation. Declared depth textures
        // also refresh retained depth and the casters that consume them.
        if (RecordTextureCasterChanges(changed)) _rasterRevision = checked(_rasterRevision + 1);
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
