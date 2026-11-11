using SomeEngine.ECS;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinRenderExtractor : IRenderExtractionRangeSystem
{
    public IRenderExtractionCollector CreateRangeCollector() => new SkinInputs();

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
    {
        MergeInputs((SkinInputs)collector);
        return false;
    }

    private void MergeInputs(SkinInputs values)
    {
        foreach (var value in values.Meshes)
        {
            _meshSources.Add(value.Source);
            if (!_meshes.TryGetValue(value.Source, out SourceMesh? mesh)) _meshes.Add(value.Source, mesh = new());
            Bind(mesh, value.Joints);
        }
        foreach (var value in values.Targets)
        {
            _targetSources.Add(value.Source);
            if (!_targets.TryGetValue(value.Source, out SourceTarget? target)) _targets.Add(value.Source, target = new());
            target.Data = value.Target;
        }
    }

    private sealed class SkinInputs : IRenderExtractionCollector
    {
        internal readonly List<(Entity Source, SkinBindingInstance Joints)> Meshes = [];
        internal readonly List<(Entity Source, GpuPoseTarget Target)> Targets = [];

        public void Reset() { Meshes.Clear(); Targets.Clear(); }
        public void Dispose() => Reset();

        public void Collect(QueryChunk packet, ulong lastSystemVersion, bool fullCollection)
        {
            if (packet.TryRead<MeshSkin>(out var meshes)
                && (fullCollection || packet.WasWritten<MeshSkin>(lastSystemVersion)))
                for (int row = 0; row < packet.Count; row++) if (packet.Has<MeshSkin>(row)) Meshes.Add((packet.Entities[row], meshes[row].Joints));
            if (packet.TryRead<GpuPoseTarget>(out var targets)
                && (fullCollection || packet.WasWritten<GpuPoseTarget>(lastSystemVersion)))
                for (int row = 0; row < packet.Count; row++) if (packet.Has<GpuPoseTarget>(row)) Targets.Add((packet.Entities[row], targets[row]));
        }
    }
}
