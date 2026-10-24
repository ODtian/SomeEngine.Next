using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>Produces model-space bones from extracted CPU poses or uniform GPU clips.</summary>
public sealed partial class SkinningFeature : IGraphResourceProvider, IDisposable,
    ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>, IRenderGraphFrameExecutionObserver
{
    private const BufferUsages PoseUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly LinkedComputePipeline _advancePipeline, _loadPipeline, _samplePipeline, _storePipeline;
    private readonly LinkedComputePipeline _translatePipeline, _twoBonePipeline, _aimPipeline, _stridePipeline;
    private readonly GraphBufferInput _currentBones, _previousBones, _controlsInput, _sectionsInput, _workInput, _clipsInput;
    private readonly GraphBufferInput _playbackInput, _valuesInput, _blendStatesInput, _blendLocalsInput;
    private readonly GraphBufferInput _goalsInput, _offsetsInput, _rootsInput, _movementsInput;
    private readonly GraphBufferInput _historyCurrentInput, _historyPreviousInput, _jointIndices, _inverseBind, _definitionUpload, _cpuUpload;
    private readonly GraphBufferId _definitions;
    private GraphBufferId _historyCurrent, _historyPrevious, _readyCurrent, _readyPrevious;
    private GraphBufferId _playback, _readyPlayback, _blendStates, _readyBlendStates;
    private readonly List<GraphBufferId> _retiredHistories = [];
    private readonly Dictionary<Entity, Section> _sections = [];
    private readonly Dictionary<Entity, MeshInput> _meshes = [];
    private readonly List<Entity> _removed = [];
    private readonly List<Control> _controls = [];
    private readonly List<float> _controlValues = [];
    private readonly List<GpuPoseGoal> _goalValues = [];
    private readonly List<Vector4> _modelOffsets = [];
    private readonly List<RootCorrection> _rootCorrections = [];
    private readonly List<StrideMovement> _movements = [];
    private readonly List<SolverDispatch> _solverDispatches = [];
    private readonly List<Matrix4x4> _cpuValues = [];
    private readonly List<CpuCopy> _cpuCopies = [];
    private readonly List<GraphPassId> _posePasses = [];
    private readonly List<GraphPassId> _samplePasses = [];
    private readonly List<BufferRange> _workLevels = [];
    private Section[] _sectionOrder = [];
    private byte[] _definitionBytes = [];
    private BufferRange _sectionBytes, _jointBytes, _inverseBytes;
    private QueryHandle _sectionQuery, _meshQuery;
    private GraphPassId _uploadPass, _advancePass, _loadPass, _cpuPass, _storePass;
    private ulong _collection;
    private int _boneCount = 1;
    private int _blendStateCount = 1, _blendLocalCount = 1;
    private bool _definitionsChanged = true, _membershipChanged = true, _uploadRequired, _migrationRequired;

    public GraphBufferInput CurrentBones => _currentBones;
    public GraphBufferInput PreviousBones => _previousBones;
    public GraphPassId CompletePass { get; }

    internal readonly record struct PoseInput(uint Starts, ulong Number, ulong GeometryNumber, bool IsGpu);

    internal PoseInput ReadPoseInput(Entity entity)
    {
        Section section = _sections[entity];
        return new(section.Data.Starts, section.Data.InputNumber, section.FrameVersion, section.Data.IsGpu);
    }

    public SkinningFeature(IGraphicsBackend backend, Device device, global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        (_backend, _device, _graph) = (backend, device, graph);
        var shader = GpuPoseShaders.Get(device.Capabilities.CompileTarget);
        _advancePipeline = LinkedComputePipeline.Create(backend, device, shader, "AdvancePoseStates", "Advance GPU pose states");
        _loadPipeline = LinkedComputePipeline.Create(backend, device, shader, "LoadPoseHistory", "Load pose history");
        _samplePipeline = LinkedComputePipeline.Create(backend, device, shader, "SamplePoseLevel", "Sample GPU pose level");
        _storePipeline = LinkedComputePipeline.Create(backend, device, shader, "StorePoseHistory", "Store pose history");
        _translatePipeline = LinkedComputePipeline.Create(backend, device, shader, "TranslatePose", "Translate model pose");
        _twoBonePipeline = LinkedComputePipeline.Create(backend, device, shader, "SolveTwoBone", "Solve two-bone pose");
        _aimPipeline = LinkedComputePipeline.Create(backend, device, shader, "SolveAim", "Solve aim pose");
        _stridePipeline = LinkedComputePipeline.Create(backend, device, shader, "WarpStride", "Warp stride and body");
        _xpbdPipeline = LinkedComputePipeline.Create(backend, device, shader, "SimulateXpbdChain", "Simulate secondary bone chains");
        using RenderGraphEdit edit = graph.BeginEdit();
        _currentBones = edit.CreateBufferInput(PoseUsages);
        _previousBones = edit.CreateBufferInput(PoseUsages);
        _historyCurrentInput = edit.CreateBufferInput(PoseUsages);
        _historyPreviousInput = edit.CreateBufferInput(PoseUsages);
        _playbackInput = edit.CreateBufferInput(PoseUsages);
        _blendStatesInput = edit.CreateBufferInput(PoseUsages);
        _blendLocalsInput = edit.CreateBufferInput(BufferUsages.ShaderWrite);
        _goalsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _offsetsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _rootsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _movementsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _xpbdFramesInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _xpbdPointsInput = edit.CreateBufferInput(PoseUsages);
        _xpbdWorkInput = edit.CreateBufferInput(BufferUsages.ShaderWrite);
        _valuesInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _controlsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _sectionsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _workInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _clipsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _jointIndices = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _inverseBind = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _definitionUpload = edit.CreateBufferInput(BufferUsages.CopySource);
        _cpuUpload = edit.CreateBufferInput(BufferUsages.CopySource);
        _definitions = edit.CreatePersistentBuffer(new BufferDesc(256, BufferUsages.ShaderRead | BufferUsages.CopyDestination, Label: "Pose definitions"));
        CompletePass = edit.AddCopyFramePass("Model poses complete", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref int _) => { }, static (ref CopyPassCommandScope _, in int _) => { });
        edit.Commit();
    }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        _sectionQuery = context.World.Query(new QueryDefinitionBuilder().Read<RenderBoneSectionData>().Write<RenderPoseSection>());
        _meshQuery = context.World.Query(new QueryDefinitionBuilder().Read<RenderMeshSkin>().Write<RenderSkinBinding>());
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        _collection++;
        SkinningFeature feature = this;
        context.World.ExecuteQuery(_sectionQuery, ref feature, static (QueryCursor rows, ref SkinningFeature feature) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                ReadOnlySpan<Entity> entities = chunk.Entities;
                ReadOnlySpan<RenderBoneSectionData> values = chunk.Read<RenderBoneSectionData>();
                for (int index = 0; index < entities.Length; index++)
                {
                    Entity entity = entities[index];
                    ref readonly RenderBoneSectionData data = ref values[index];
                    if (!feature._sections.TryGetValue(entity, out Section? section))
                    {
                        feature._sections.Add(entity, section = new(entity));
                        feature._membershipChanged = feature._definitionsChanged = true;
                    }
                    if (!ReferenceEquals(section.Data.Parents, data.Parents) || !ReferenceEquals(section.Data.Clip, data.Clip)
                        || !ReferenceEquals(section.Data.StateMachine, data.StateMachine)
                        || !ReferenceEquals(section.Data.Blend, data.Blend) || !ReferenceEquals(section.Data.Rig, data.Rig)
                        || !ReferenceEquals(section.Data.Evaluation, data.Evaluation)
                        || !section.Data.CpuBones.Equals(data.CpuBones))
                        feature._definitionsChanged = true;
                    if (!ReferenceEquals(section.Data.Blend, data.Blend) || !ReferenceEquals(section.Data.Rig, data.Rig)) feature._membershipChanged = true;
                    section.Data = data;
                    section.Collection = feature._collection;
                }
            }
        });
        _removed.Clear();
        foreach ((Entity entity, Section section) in _sections)
            if (section.Collection != _collection) _removed.Add(entity);
        foreach (Entity entity in _removed) { _sections.Remove(entity); _membershipChanged = _definitionsChanged = true; }
        context.World.ExecuteQuery(_meshQuery, ref feature, static (QueryCursor rows, ref SkinningFeature feature) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                ReadOnlySpan<Entity> entities = chunk.Entities;
                ReadOnlySpan<RenderMeshSkin> values = chunk.Read<RenderMeshSkin>();
                for (int index = 0; index < entities.Length; index++)
                {
                    Entity entity = entities[index];
                    RenderMeshSkin data = values[index];
                    if (!feature._meshes.TryGetValue(entity, out MeshInput? mesh))
                    {
                        feature._meshes.Add(entity, mesh = new());
                        feature._definitionsChanged = true;
                    }
                    if (mesh.Data != data) feature._definitionsChanged = true;
                    mesh.Data = data;
                    mesh.Collection = feature._collection;
                }
            }
        });
        _removed.Clear();
        foreach ((Entity entity, MeshInput mesh) in _meshes)
            if (mesh.Collection != _collection) _removed.Add(entity);
        foreach (Entity entity in _removed) { _meshes.Remove(entity); _definitionsChanged = true; }

        if (_definitionsChanged) Rebuild(context.World);
        else RetireHistories();
        PrepareEvaluations();
        context.World.ExecuteQuery(_sectionQuery, ref feature, static (QueryCursor rows, ref SkinningFeature feature) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                ReadOnlySpan<Entity> entities = chunk.Entities;
                Span<RenderPoseSection> values = chunk.Write<RenderPoseSection>();
                for (int index = 0; index < entities.Length; index++)
                {
                    Section section = feature._sections[entities[index]];
                    ref readonly RenderBoneSectionData data = ref section.Data;
                    values[index] = new(section.Range, data.Starts, data.InputNumber, section.FrameTime,
                        data.StateMachine is null && data.Blend is null ? data.SampleTime : data.PlaybackTime, section.Index, section.FrameVersion);
                }
            }
        });
        RequestReadbacks();
    }

    private void ReadFrameInputs()
    {
        _controls.Clear(); _controlValues.Clear(); _cpuValues.Clear(); _cpuCopies.Clear();
        _goalValues.Clear(); _modelOffsets.Clear(); _rootCorrections.Clear(); _movements.Clear(); _xpbdFrames.Clear();
        foreach (Section section in _sectionOrder)
        {
            ref readonly RenderBoneSectionData data = ref section.Data;
            uint flags = (section.Evaluate ? 1u : 0u) | (!section.HasResult ? 2u : 0u) | (data.IsGpu ? 4u : 0u)
                | (data.StateMachine is not null ? 8u : 0u) | (data.Playing ? 16u : 0u)
                | (section.AcceptedInput.AnimationStarts != data.Identity.AnimationStarts
                    || !ReferenceEquals(section.AcceptedInput.StateMachine, data.StateMachine)
                    || !ReferenceEquals(section.AcceptedInput.Clip, data.Clip)
                    || !ReferenceEquals(section.AcceptedInput.Blend, data.Blend) ? 32u : 0u)
                | (data.Blend is not null ? 64u : 0u) | (section.AdvanceSource ? 128u : 0u)
                | (section.SampleSource ? 256u : 0u) | (section.ResetSamples ? 512u : 0u);
            uint inputOffset = (uint)_controlValues.Count;
            _controlValues.AddRange(data.Controls);
            if (data.Rig is not null) _goalValues.AddRange(data.Goals);
            _modelOffsets.Add(new(data.ModelOffset, 0));
            _rootCorrections.Add(new(new(data.RootCorrection.Translation, data.ExtractRootMotion ? 1 : 0), data.RootCorrection.Rotation));
            GpuPoseMovement movement = data.Movement;
            _movements.Add(new(new(movement.Velocity, movement.Weight), new(movement.AnimationVelocity, 0), new(movement.FacingDirection, 0)));
            if (_xpbdPointCount > 1) _xpbdFrames.Add(ReadXpbdFrame(section));
            _controls.Add(new(data.Blend is null ? section.ClipOffset : (uint)section.BlendLocalStart,
                data.SampleTime, flags, data.Starts, data.InputNumber, data.CalculationTime,
                data.Blend is null ? section.MachineOffset : section.BlendOffset, inputOffset, data.PlaybackTime,
                (uint)section.BlendStart, section.ReferenceOffset, section.Presentation.Amount, 0, 0));
            if ((!data.IsGpu || data.CpuBones.Length != 0) && (!section.HasResult || section.CpuVersion != data.Version
                || !section.AcceptedCpuBones.Equals(data.CpuBones)
                || !data.IsGpu && (section.AcceptedInput.Clip is not null
                    || section.AcceptedInput.StateMachine is not null || section.AcceptedInput.Blend is not null)))
            {
                if (!data.IsGpu)
                {
                    _cpuCopies.Add(new(_cpuValues.Count, section.Range.Start, section.Range.Count));
                    _cpuValues.AddRange(data.Current);
                }
                else
                    foreach (short bone in data.CpuBones.Span)
                    {
                        _cpuCopies.Add(new(_cpuValues.Count, section.Range.Start + bone, 1));
                        _cpuValues.Add(data.Current[bone]);
                    }
            }
        }
    }

    private void PrepareEvaluations()
    {
        foreach (Section section in _sectionOrder) section.EvaluationVisited = false;
        foreach (Section section in _sectionOrder) Evaluate(section);

        void Evaluate(Section section)
        {
            if (section.EvaluationVisited) return;
            section.EvaluationVisited = true;
            ref readonly RenderBoneSectionData data = ref section.Data;
            section.FrameInput = new(data.InputNumber, data.Starts, data.Identity.AnimationStarts, data.Clip, data.StateMachine, data.Blend, data.Rig);
            section.AdvanceSource = !section.HasResult || section.AcceptedInput != section.FrameInput;
            if (section.XpbdCount != 0)
                section.XpbdBinding = new(data.Identity.Target, data.Identity.Instance, data.Rig, data.Starts, data.Identity.AnimationStarts, data.BindingVersion);
            section.ResetXpbd = section.XpbdCount != 0 && section.AcceptedXpbd != section.XpbdBinding;
            bool changed = section.AdvanceSource || data.IsGpu && !section.HasSamples || section.AcceptedBinding != data.BindingVersion
                || section.ResetXpbd
                || !ReferenceEquals(section.AcceptedEvaluation, data.Evaluation) || section.AcceptedExtractRoot != data.ExtractRootMotion
                || !section.AcceptedCpuBones.Equals(data.CpuBones)
                || (!data.IsGpu || data.CpuBones.Length != 0) && section.CpuVersion != data.Version;
            double time = data.CalculationTime;
            foreach (short bone in data.CpuBones.Span) time = Math.Max(time, data.Calculations[bone].Time);
            for (int index = 0; index < section.Dependencies.Length; index++)
            {
                Section parent = section.Dependencies[index];
                Evaluate(parent);
                changed |= section.ParentVersions[index] != parent.FrameVersion;
                time = Math.Max(time, parent.FrameTime);
            }
            section.Evaluate = changed;
            section.FrameVersion = section.Version + (changed ? 1ul : 0ul);
            section.FrameTime = changed ? time : section.Time;
            PrepareSampling(section, time);
        }
    }

    private void Rebuild(RenderWorld world)
    {
        _sectionOrder = [.. _sections.Values];
        int boneCount = 0, blendStates = 0, blendLocals = 0;
        for (int index = 0; index < _sectionOrder.Length; index++)
        {
            Section section = _sectionOrder[index];
            section.Index = index;
            section.Range = new(boneCount, section.Data.Skeleton.BoneCount);
            section.BlendStart = blendStates;
            section.BlendLocalStart = blendLocals;
            int steps = section.Data.Blend?.Steps.Length ?? 0;
            blendStates += steps;
            blendLocals += steps * section.Range.Count;
            section.Depths = new int[section.Range.Count];
            section.Dependencies = section.Data.Parents.Where(parent => parent.Section != Entity.Null && parent.Section != section.Entity)
                .Select(parent => _sections[parent.Section]).Distinct().ToArray();
            section.ParentVersions = new ulong[section.Dependencies.Length];
            Array.Fill(section.Depths, -1);
            boneCount += section.Range.Count;
        }
        _boneCount = Math.Max(1, boneCount);
        _blendStateCount = Math.Max(1, blendStates);
        _blendLocalCount = Math.Max(1, blendLocals);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        WriteGpuDefinitions(writer);
        WriteEvaluation(writer);
        var work = new List<List<BoneWork>>();
        foreach (Section section in _sectionOrder)
        {
            if (!section.Data.IsGpu) continue;
            for (int bone = 0; bone < section.Range.Count; bone++)
            {
                if (section.Data.CpuBones.Span.BinarySearch((short)bone) >= 0) continue;
                int depth = Depth(section, bone);
                while (work.Count <= depth) work.Add([]);
                RenderBoneParent parent = section.Data.Parents[bone];
                uint parentBone = parent.Section == Entity.Null ? uint.MaxValue : (uint)(_sections[parent.Section].Range.Start + parent.Bone);
                work[depth].Add(new((uint)section.Index, (uint)bone, parentBone, (uint)(section.Range.Start + bone)));
            }
        }
        _workLevels.Clear();
        foreach (List<BoneWork> level in work)
        {
            Align(writer, 16);
            ulong start = (ulong)bytes.Position;
            writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(level)));
            _workLevels.Add(new(start, (ulong)(level.Count * 16)));
        }
        WriteSolverWork(writer);
        Align(writer, 16);
        ulong sectionsStart = (ulong)bytes.Position;
        foreach (Section section in _sectionOrder)
            for (int bone = 0; bone < section.Range.Count; bone++) writer.Write((uint)section.Index);
        if (boneCount == 0) writer.Write(uint.MaxValue);
        _sectionBytes = new(sectionsStart, (ulong)_boneCount * 4);
        Align(writer, 64);
        var bindOffsets = new Dictionary<SkinBinding, uint>();
        ulong inverseStart = (ulong)bytes.Position;
        foreach (MeshInput input in _meshes.Values)
        {
            RenderMeshSkin mesh = input.Data;
            if (bindOffsets.ContainsKey(mesh.Binding)) continue;
            bindOffsets.Add(mesh.Binding, (uint)(((ulong)bytes.Position - inverseStart) / 64));
            writer.Write(MemoryMarshal.AsBytes(mesh.Binding.InverseBindMatrices));
        }
        if ((ulong)bytes.Position == inverseStart) WriteMatrix(writer, Matrix4x4.Identity);
        _inverseBytes = new(inverseStart, (ulong)bytes.Position - inverseStart);
        Align(writer, 4);
        ulong jointsStart = (ulong)bytes.Position;
        foreach ((Entity entity, MeshInput input) in _meshes)
        {
            RenderMeshSkin mesh = input.Data;
            uint jointOffset = (uint)(((ulong)bytes.Position - jointsStart) / 4);
            foreach (RenderSkinJoint joint in mesh.Joints) writer.Write((uint)(_sections[joint.Section].Range.Start + joint.Bone));
            world.Replace(entity, new RenderSkinBinding(jointOffset, bindOffsets[mesh.Binding]));
        }
        if ((ulong)bytes.Position == jointsStart) writer.Write(0u);
        _jointBytes = new(jointsStart, (ulong)bytes.Position - jointsStart);
        Align(writer, 64);
        _definitionBytes = bytes.ToArray();

        using RenderGraphEdit edit = _graph.BeginEdit();
        foreach (GraphPassId pass in _posePasses) edit.Remove(pass);
        _posePasses.Clear(); _samplePasses.Clear();
        foreach (GraphBufferId buffer in _retiredHistories) edit.Remove(buffer);
        _retiredHistories.Clear();
        edit.SetBufferDescription(_definitions, new BufferDesc((ulong)_definitionBytes.Length,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination, Label: "Pose definitions"));
        var migrations = new List<HistoryCopy>();
        if (_membershipChanged)
        {
            if (_historyCurrent != default && _historyCurrent != _readyCurrent)
            { edit.Remove(_historyCurrent); edit.Remove(_historyPrevious); edit.Remove(_playback); edit.Remove(_blendStates); edit.Remove(_xpbdPoints); }
            _historyCurrent = edit.CreatePersistentBuffer(new BufferDesc((ulong)_boneCount * 128, PoseUsages, Label: "Current pose and local sample history"));
            _historyPrevious = edit.CreatePersistentBuffer(new BufferDesc((ulong)_boneCount * 128, PoseUsages, Label: "Previous pose and local sample history"));
            _playback = edit.CreatePersistentBuffer(new BufferDesc((ulong)Math.Max(1, _sectionOrder.Length) * 64,
                PoseUsages, Label: "GPU pose playback"));
            _blendStates = edit.CreatePersistentBuffer(new BufferDesc((ulong)_blendStateCount * 32,
                PoseUsages, Label: "GPU blend phases and selections"));
            _xpbdPoints = edit.CreatePersistentBuffer(new BufferDesc((ulong)_xpbdPointCount * 32,
                PoseUsages, Label: "GPU secondary positions and velocities"));
            foreach (Section section in _sectionOrder)
                if (section.HasResult) migrations.Add(new(section.StoredRange, section.Range, section.StoredIndex, section.Index,
                    section.StoredBlendStart, section.BlendStart,
                    ReferenceEquals(section.StoredBlend, section.Data.Blend) ? section.StoredBlend?.Steps.Length ?? 0 : 0,
                    section.StoredXpbdStart, section.XpbdStart, ReferenceEquals(section.StoredRig, section.Data.Rig) ? section.XpbdCount : 0));
            _migrationRequired = true;
        }
        var upload = new DefinitionCopy(_definitionUpload, _definitions, _definitionBytes.Length,
            _migrationRequired, _readyCurrent, _readyPrevious, _readyPlayback, _readyBlendStates, _readyXpbdPoints,
            _historyCurrent, _historyPrevious, _playback, _blendStates, _xpbdPoints, [.. migrations]);
        _uploadPass = edit.AddCopyFramePass("Upload pose definitions", PassQueueSelection.AnyOfType(QueueType.Graphics), upload,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), DefinitionCopy.Declare, DefinitionCopy.Record);
        _posePasses.Add(_uploadPass);
        _loadPass = AddKernel(edit, _loadPipeline, Kernel.Load, BufferRange.Whole, _boneCount);
        edit.OrderAfter(_loadPass, _uploadPass);
        _advancePass = AddKernel(edit, _advancePipeline, Kernel.Advance, BufferRange.Whole, _sectionOrder.Length);
        edit.OrderAfter(_advancePass, _loadPass);
        _cpuPass = edit.AddCopyFramePass("CPU model ranges complete", PassQueueSelection.AnyOfType(QueueType.Graphics),
            0, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref int _) => { }, static (ref CopyPassCommandScope _, in int _) => { });
        _posePasses.Add(_cpuPass);
        edit.OrderAfter(_cpuPass, _advancePass);
        GraphPassId predecessor = _cpuPass;
        foreach (BufferRange level in _workLevels)
        {
            GraphPassId pass = AddKernel(edit, _samplePipeline, Kernel.Sample, level, (int)(level.Size / 16));
            edit.OrderAfter(pass, predecessor);
            _samplePasses.Add(pass);
            predecessor = pass;
        }
        foreach (SolverDispatch solve in _solverDispatches)
        {
            LinkedComputePipeline pipeline = solve.Kernel switch
            {
                Kernel.Translate => _translatePipeline, Kernel.TwoBone => _twoBonePipeline,
                Kernel.Stride => _stridePipeline, Kernel.Xpbd => _xpbdPipeline, _ => _aimPipeline,
            };
            GraphPassId pass = AddKernel(edit, pipeline, solve.Kernel, solve.Work, solve.Count);
            edit.OrderAfter(pass, predecessor);
            predecessor = pass;
        }
        _storePass = AddKernel(edit, _storePipeline, Kernel.Store, BufferRange.Whole, _boneCount);
        edit.OrderAfter(_storePass, predecessor);
        edit.OrderAfter(CompletePass, _storePass);
        edit.Commit();
        _uploadRequired = true;
        _membershipChanged = _definitionsChanged = false;
    }

    private int Depth(Section section, int bone)
    {
        if (section.Depths[bone] >= 0) return section.Depths[bone];
        RenderBoneParent parent = section.Data.Parents[bone];
        int depth = 0;
        if (parent.Section != Entity.Null)
        {
            Section source = _sections[parent.Section];
            if (source.Data.IsGpu && source.Data.CpuBones.Span.BinarySearch((short)parent.Bone) < 0)
                depth = Depth(source, parent.Bone) + 1;
        }
        return section.Depths[bone] = depth;
    }

    private GraphPassId AddKernel(RenderGraphEdit edit, LinkedComputePipeline pipeline, Kernel kernel, BufferRange work, int count)
    {
        GraphPassId id = edit.AddComputeFramePass($"GPU poses {kernel}", PassQueueSelection.AnyCompatible(),
            new DispatchArguments((uint)((count + 63) / 64), 1, 1), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline, pipeline.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch));
        PassDefinition pass = edit.GetPass(id);
        Resource[] resources = ShaderResourceLayout.Resolve<Resource>(pipeline.ParameterLayout, static (name, _) => name switch
        {
            "GpuPoseControls" => Resource.Controls, "GpuPoseSections" => Resource.Sections,
            "GpuPoseWork" => Resource.Work, "GpuPoseClips" => Resource.Clips,
            "GpuPoseInputs" => Resource.Values, "GpuPosePlaybackStates" => Resource.Playback,
            "GpuBlendStates" => Resource.BlendStates, "GpuBlendLocals" => Resource.BlendLocals,
            "GpuPoseGoals" => Resource.Goals, "GpuPoseOffsets" => Resource.Offsets,
            "GpuPoseRoots" => Resource.Roots,
            "GpuPoseMovements" => Resource.Movements,
            "GpuXpbdFrames" => Resource.XpbdFrames, "GpuXpbdPoints" => Resource.XpbdPoints, "GpuXpbdWork" => Resource.XpbdWork,
            "GpuPoseModels" => Resource.Models, "GpuPosePrevious" => Resource.Previous,
            "GpuPoseHistoryCurrent" => Resource.HistoryCurrent, "GpuPoseHistoryPrevious" => Resource.HistoryPrevious, _ => null,
        });
        foreach (Resource resource in resources)
        {
            switch (resource)
            {
                case Resource.Controls: pass.Bind(edit.CreateBufferSrv(_controlsInput, structureStride: 64), PipelineSync.ComputeShading); break;
                case Resource.Values: pass.Bind(edit.CreateBufferSrv(_valuesInput, structureStride: 4), PipelineSync.ComputeShading); break;
                case Resource.Goals: pass.Bind(edit.CreateBufferSrv(_goalsInput, structureStride: 48), PipelineSync.ComputeShading); break;
                case Resource.Offsets: pass.Bind(edit.CreateBufferSrv(_offsetsInput, structureStride: 16), PipelineSync.ComputeShading); break;
                case Resource.Roots: pass.Bind(edit.CreateBufferSrv(_rootsInput, structureStride: 32), PipelineSync.ComputeShading); break;
                case Resource.Movements: pass.Bind(edit.CreateBufferSrv(_movementsInput, structureStride: 48), PipelineSync.ComputeShading); break;
                case Resource.XpbdFrames: pass.Bind(edit.CreateBufferSrv(_xpbdFramesInput, structureStride: 144), PipelineSync.ComputeShading); break;
                case Resource.XpbdPoints: pass.Bind(edit.CreateBufferUav(_xpbdPointsInput, structureStride: 32), PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite); break;
                case Resource.XpbdWork: pass.Bind(edit.CreateBufferUav(_xpbdWorkInput, structureStride: 96), PipelineSync.ComputeShading,
                    GraphAccessMode.Write); break;
                case Resource.Playback:
                    pass.Bind(edit.CreateBufferUav(_playbackInput, structureStride: 64), PipelineSync.ComputeShading,
                        kernel == Kernel.Advance ? GraphAccessMode.ReadWrite : GraphAccessMode.Read); break;
                case Resource.BlendStates:
                    pass.Bind(edit.CreateBufferUav(_blendStatesInput, structureStride: 32), PipelineSync.ComputeShading,
                        kernel == Kernel.Advance ? GraphAccessMode.ReadWrite : GraphAccessMode.Read); break;
                case Resource.BlendLocals:
                    pass.Bind(edit.CreateBufferUav(_blendLocalsInput, structureStride: 48), PipelineSync.ComputeShading,
                        GraphAccessMode.Write); break;
                case Resource.Sections: pass.Bind(edit.CreateBufferSrv(_sectionsInput, structureStride: 4), PipelineSync.ComputeShading); break;
                case Resource.Work:
                    pass.Bind(edit.CreateBufferSrv(_workInput, kernel is Kernel.Sample or Kernel.Translate or Kernel.TwoBone or Kernel.Aim or Kernel.Stride or Kernel.Xpbd
                        ? work : new BufferRange(0, 16), structureStride: 16), PipelineSync.ComputeShading); break;
                case Resource.Clips: pass.Bind(edit.CreateBufferSrv(_clipsInput), PipelineSync.ComputeShading); break;
                case Resource.Models:
                    pass.Bind(edit.CreateBufferUav(_currentBones, structureStride: 64), PipelineSync.ComputeShading,
                        kernel == Kernel.Load ? GraphAccessMode.Write : kernel is Kernel.Sample or Kernel.Translate or Kernel.TwoBone or Kernel.Aim or Kernel.Stride or Kernel.Xpbd
                            ? GraphAccessMode.ReadWrite : GraphAccessMode.Read,
                        kernel == Kernel.Load ? WriteCoverage.Complete : WriteCoverage.Partial); break;
                case Resource.Previous:
                    pass.Bind(edit.CreateBufferUav(_previousBones, structureStride: 64), PipelineSync.ComputeShading,
                        kernel == Kernel.Load ? GraphAccessMode.Write : kernel == Kernel.Store ? GraphAccessMode.ReadWrite : GraphAccessMode.Read,
                        kernel == Kernel.Load ? WriteCoverage.Complete : WriteCoverage.Partial); break;
                case Resource.HistoryCurrent:
                    pass.Bind(edit.CreateBufferUav(_historyCurrentInput, structureStride: 64), PipelineSync.ComputeShading,
                        kernel is Kernel.Store or Kernel.Sample ? GraphAccessMode.ReadWrite : GraphAccessMode.Read); break;
                case Resource.HistoryPrevious:
                    pass.Bind(edit.CreateBufferUav(_historyPreviousInput, structureStride: 64), PipelineSync.ComputeShading,
                        kernel is Kernel.Store or Kernel.Sample ? GraphAccessMode.ReadWrite : GraphAccessMode.Read); break;
            }
        }
        _posePasses.Add(id);
        return id;
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        RenderGraphFrame frame = context.Graph;
        ReadFrameInputs();
        if (_controls.Count == 0) _controls.Add(default);
        if (_controlValues.Count == 0) _controlValues.Add(0);
        if (_goalValues.Count == 0) _goalValues.Add(default);
        if (_modelOffsets.Count == 0) _modelOffsets.Add(default);
        if (_rootCorrections.Count == 0) _rootCorrections.Add(default);
        if (_movements.Count == 0) _movements.Add(default);
        if (_xpbdFrames.Count == 0) _xpbdFrames.Add(default);
        frame.SetBufferInput(_currentBones, new(frame.CreateBuffer(new BufferDesc((ulong)_boneCount * 64, PoseUsages, Label: "Current model bones")), BufferRange.Whole));
        frame.SetBufferInput(_previousBones, new(frame.CreateBuffer(new BufferDesc((ulong)_boneCount * 64, PoseUsages, Label: "Previous model bones")), BufferRange.Whole));
        frame.SetBufferInput(_historyCurrentInput, new(_historyCurrent, BufferRange.Whole));
        frame.SetBufferInput(_historyPreviousInput, new(_historyPrevious, BufferRange.Whole));
        frame.SetBufferInput(_playbackInput, new(_playback, BufferRange.Whole));
        frame.SetBufferInput(_blendStatesInput, new(_blendStates, BufferRange.Whole));
        frame.SetBufferInput(_xpbdPointsInput, new(_xpbdPoints, BufferRange.Whole));
        frame.SetBufferInput(_xpbdWorkInput, new(frame.CreateBuffer(new BufferDesc((ulong)_xpbdPointCount * 96,
            BufferUsages.ShaderWrite, Label: "GPU secondary constraint workspace")), BufferRange.Whole));
        frame.SetBufferInput(_xpbdFramesInput, frame.Upload<XpbdFrame>(CollectionsMarshal.AsSpan(_xpbdFrames), BufferUsages.ShaderRead));
        frame.SetBufferInput(_blendLocalsInput, new(frame.CreateBuffer(new BufferDesc((ulong)_blendLocalCount * 48,
            BufferUsages.ShaderWrite, Label: "GPU blend local workspace")), BufferRange.Whole));
        frame.SetBufferInput(_valuesInput, frame.Upload<float>(CollectionsMarshal.AsSpan(_controlValues), BufferUsages.ShaderRead));
        frame.SetBufferInput(_goalsInput, frame.Upload<GpuPoseGoal>(CollectionsMarshal.AsSpan(_goalValues), BufferUsages.ShaderRead));
        frame.SetBufferInput(_offsetsInput, frame.Upload<Vector4>(CollectionsMarshal.AsSpan(_modelOffsets), BufferUsages.ShaderRead));
        frame.SetBufferInput(_rootsInput, frame.Upload<RootCorrection>(CollectionsMarshal.AsSpan(_rootCorrections), BufferUsages.ShaderRead));
        frame.SetBufferInput(_movementsInput, frame.Upload<StrideMovement>(CollectionsMarshal.AsSpan(_movements), BufferUsages.ShaderRead));
        frame.SetBufferInput(_controlsInput, frame.Upload<Control>(CollectionsMarshal.AsSpan(_controls), BufferUsages.ShaderRead));
        frame.SetBufferInput(_sectionsInput, new(_definitions, _sectionBytes));
        frame.SetBufferInput(_jointIndices, new(_definitions, _jointBytes));
        frame.SetBufferInput(_inverseBind, new(_definitions, _inverseBytes));
        frame.SetBufferInput(_workInput, new(_definitions, BufferRange.Whole));
        frame.SetBufferInput(_clipsInput, new(_definitions, BufferRange.Whole));
        frame.SetPassEnabled(_loadPass, _sectionOrder.Length != 0);
        frame.SetPassEnabled(_advancePass, _sectionOrder.Length != 0);
        frame.SetPassEnabled(_storePass, _sectionOrder.Length != 0);
        if (_uploadRequired) frame.SetBufferInput(_definitionUpload, frame.Upload(_definitionBytes, BufferUsages.CopySource));
        frame.SetPassEnabled(_uploadPass, _uploadRequired);
        if (_cpuCopies.Count != 0)
        {
            frame.SetBufferInput(_cpuUpload, frame.Upload<Matrix4x4>(CollectionsMarshal.AsSpan(_cpuValues), BufferUsages.CopySource));
            GraphPassId copy = frame.AddCopyPass("Copy CPU model ranges", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new CpuPoseCopy(_cpuUpload, _currentBones, _cpuCopies), new PassOptions(Culling: PassCullingMode.NeverCull),
                CpuPoseCopy.Declare, CpuPoseCopy.Record);
            frame.OrderAfter(copy, _advancePass);
            frame.OrderAfter(_cpuPass, copy);
        }
        frame.SetPassEnabled(_cpuPass, _sectionOrder.Length != 0);
        frame.ObserveExecution(this);
        RecordReadbacks(ref frame);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (result.TryGetPassCompletion(_uploadPass, out _))
        {
            _uploadRequired = false;
            if (_migrationRequired)
            {
                if (_readyCurrent != default)
                { _retiredHistories.Add(_readyCurrent); _retiredHistories.Add(_readyPrevious); _retiredHistories.Add(_readyPlayback);
                  _retiredHistories.Add(_readyBlendStates); _retiredHistories.Add(_readyXpbdPoints); }
                _readyCurrent = _historyCurrent; _readyPrevious = _historyPrevious;
                _readyPlayback = _playback;
                _readyBlendStates = _blendStates;
                _readyXpbdPoints = _xpbdPoints;
                foreach (Section section in _sectionOrder)
                {
                    section.StoredRange = section.Range; section.StoredIndex = section.Index;
                    section.StoredBlendStart = section.BlendStart; section.StoredBlend = section.Data.Blend;
                    section.StoredXpbdStart = section.XpbdStart; section.StoredRig = section.Data.Rig;
                }
                _migrationRequired = false;
            }
        }
        if (result.TryGetPassCompletion(_storePass, out _))
            foreach (Section section in _sectionOrder)
            {
                if (section.XpbdCount != 0 && section.Evaluate)
                {
                    section.AcceptedXpbd = section.XpbdBinding;
                    section.XpbdTime = section.FrameTime;
                }
                section.AcceptedInput = section.FrameInput;
                section.CpuVersion = section.Data.Version;
                section.AcceptedBinding = section.Data.BindingVersion;
                section.AcceptedCpuBones = section.Data.CpuBones;
                section.Version = section.FrameVersion;
                section.Time = section.FrameTime;
                section.AcceptedEvaluation = section.Data.Evaluation;
                section.AcceptedExtractRoot = section.Data.ExtractRootMotion;
                if (section.SampleSource)
                {
                    section.PreviousSampleTime = section.Presentation.PreviousTime;
                    section.SampleTime = section.Presentation.CurrentTime;
                    section.PreviousSampleIdentity = section.Presentation.PreviousInput;
                    section.SampleIdentity = section.Presentation.CurrentInput;
                    section.HasSamples = true;
                }
                for (int index = 0; index < section.Dependencies.Length; index++)
                    section.ParentVersions[index] = section.Dependencies[index].FrameVersion;
                section.HasResult = true;
                section.StoredRange = section.Range;
                section.StoredIndex = section.Index;
                section.StoredBlendStart = section.BlendStart;
                section.StoredBlend = section.Data.Blend;
                section.StoredXpbdStart = section.XpbdStart;
                section.StoredRig = section.Data.Rig;
            }
        AcceptReadbacks(result);
    }

    private void RetireHistories()
    {
        if (_retiredHistories.Count == 0) return;
        using RenderGraphEdit edit = _graph.BeginEdit();
        edit.Remove(_uploadPass);
        _posePasses.Remove(_uploadPass);
        var upload = new DefinitionCopy(_definitionUpload, _definitions, _definitionBytes.Length,
            false, default, default, default, default, default, _historyCurrent, _historyPrevious, _playback, _blendStates, _xpbdPoints, []);
        _uploadPass = edit.AddCopyFramePass("Upload pose definitions", PassQueueSelection.AnyOfType(QueueType.Graphics), upload,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), DefinitionCopy.Declare, DefinitionCopy.Record);
        _posePasses.Add(_uploadPass);
        edit.OrderAfter(_advancePass, _uploadPass);
        foreach (GraphBufferId buffer in _retiredHistories) edit.Remove(buffer);
        _retiredHistories.Clear();
        edit.Commit();
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    { context.World.ReleaseQuery(_sectionQuery); context.World.ReleaseQuery(_meshQuery); }

    public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        binder = type == ResourceBindingType.BufferSrv ? name switch
        {
            "SkinCurrentBones" => BindCurrent, "SkinPreviousBones" => BindPrevious,
            "SkinJointIndices" => BindJoints, "SkinInverseBind" => BindInverse, _ => null!,
        } : null!;
        return binder is not null;
    }
    private void BindCurrent(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    { pass.Bind(graph.CreateBufferSrv(_currentBones, structureStride: 64), sync); graph.OrderAfter(pass.Id, CompletePass); }
    private void BindPrevious(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    { pass.Bind(graph.CreateBufferSrv(_previousBones, structureStride: 64), sync); graph.OrderAfter(pass.Id, CompletePass); }
    private void BindJoints(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    { pass.Bind(graph.CreateBufferSrv(_jointIndices, structureStride: 4), sync); graph.OrderAfter(pass.Id, CompletePass); }
    private void BindInverse(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    { pass.Bind(graph.CreateBufferSrv(_inverseBind, structureStride: 64), sync); graph.OrderAfter(pass.Id, CompletePass); }

    public void Dispose()
    {
        CompleteReadbacks(wait: true);
        foreach (Readback readback in _readbacks)
        {
            readback.Buffer?.Dispose();
            readback.Request?.Cancel();
        }
        _readbacks.Clear();
        _advancePipeline.Dispose(); _loadPipeline.Dispose(); _samplePipeline.Dispose(); _storePipeline.Dispose();
        _translatePipeline.Dispose(); _twoBonePipeline.Dispose(); _aimPipeline.Dispose(); _stridePipeline.Dispose();
        _xpbdPipeline.Dispose();
    }

    private static void Align(BinaryWriter writer, int alignment)
    { while (writer.BaseStream.Position % alignment != 0) writer.Write((byte)0); }
    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 matrix) =>
        writer.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in matrix, 1)));

    private enum Kernel { Advance, Load, Sample, Store, Translate, TwoBone, Aim, Stride, Xpbd }
    private enum Resource { Controls, Values, Goals, Offsets, Roots, Movements, XpbdFrames, XpbdPoints, XpbdWork, Playback, BlendStates, BlendLocals, Sections, Work, Clips, Models, Previous, HistoryCurrent, HistoryPrevious }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Control(uint ClipOffset, float SampleTime, uint Flags, uint Starts, ulong InputNumber,
        double CalculationTime, uint MachineOffset, uint InputOffset, float PlaybackTime, uint BlendStart,
        uint ReferenceOffset, float PresentationAmount, uint Padding0, uint Padding1);
    private readonly record struct RootCorrection(Vector4 Translation, Quaternion Rotation);
    private readonly record struct StrideMovement(Vector4 Velocity, Vector4 AnimationVelocity, Vector4 FacingDirection);
    private readonly record struct BoneWork(uint Section, uint Bone, uint Parent, uint Output);
    private readonly record struct Input(ulong Number, uint Starts, uint AnimationStarts, QuantizedClipData? Clip,
        GpuPoseStateMachine? StateMachine, GpuPoseBlend? Blend, GpuPoseRig? Rig);
    private readonly record struct CpuCopy(int Source, int Destination, int Count);
    private readonly record struct HistoryCopy(PoseRange Source, PoseRange Destination, int SourceIndex, int DestinationIndex,
        int SourceBlendStart, int DestinationBlendStart, int BlendCount, int SourceXpbdStart, int DestinationXpbdStart, int XpbdCount);
    private sealed class MeshInput
    {
        internal RenderMeshSkin Data;
        internal ulong Collection;
    }

    private sealed class Section(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal RenderBoneSectionData Data;
        internal ulong Collection, CpuVersion;
        internal ulong AcceptedBinding, Version, FrameVersion;
        internal double Time, FrameTime;
        internal ReadOnlyMemory<short> AcceptedCpuBones;
        internal Section[] Dependencies = [];
        internal ulong[] ParentVersions = [];
        internal PoseRange Range, StoredRange;
        internal int Index, StoredIndex;
        internal int BlendStart, BlendLocalStart, StoredBlendStart;
        internal int SolverEnd, GoalStart;
        internal int XpbdStart, XpbdCount, StoredXpbdStart;
        internal GpuPoseRig? StoredRig;
        internal XpbdBinding AcceptedXpbd, XpbdBinding;
        internal double XpbdTime;
        internal bool ResetXpbd;
        internal GpuPoseBlend? StoredBlend;
        internal int[] Depths = [];
        internal uint ClipOffset, MachineOffset, BlendOffset;
        internal bool HasResult;
        internal bool Evaluate, AdvanceSource, EvaluationVisited;
        internal bool HasSamples, SampleSource, ResetSamples, AcceptedExtractRoot;
        internal double SampleTime, PreviousSampleTime;
        internal GpuPoseEvaluation? AcceptedEvaluation;
        internal GpuPosePresentation Presentation;
        internal GpuPoseIdentity PreviousSampleIdentity, SampleIdentity;
        internal uint ReferenceOffset;
        internal bool[] SelectedBones = [];
        internal short[] SampledBones = [];
        internal Input AcceptedInput, FrameInput;
    }

    private readonly record struct CpuPoseCopy(GraphBufferInput Source, GraphBufferInput Destination, List<CpuCopy> Regions)
    {
        internal static void Declare(ref PassDefinition pass, ref CpuPoseCopy data)
        {
            foreach (CpuCopy region in data.Regions)
            {
                pass.Read(data.Source, new BufferRange((ulong)region.Source * 64, (ulong)region.Count * 64),
                    PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Destination, new BufferRange((ulong)region.Destination * 64, (ulong)region.Count * 64),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }
        }
        internal static void Record(ref CopyPassCommandScope commands, in CpuPoseCopy data)
        {
            BufferRegion source = commands.GetBufferRegion(data.Source), destination = commands.GetBufferRegion(data.Destination);
            foreach (CpuCopy region in data.Regions)
                commands.CopyBuffer(new(source.Buffer, source.Range.Offset + (ulong)region.Source * 64,
                    destination.Buffer, destination.Range.Offset + (ulong)region.Destination * 64, (ulong)region.Count * 64));
        }
    }

    private readonly record struct DefinitionCopy(GraphBufferInput Upload, GraphBufferId Definitions, int Bytes,
        bool Migrate, GraphBufferId BeforeCurrent, GraphBufferId BeforePrevious, GraphBufferId BeforePlayback, GraphBufferId BeforeBlendStates, GraphBufferId BeforeXpbd,
        GraphBufferId Current, GraphBufferId Previous, GraphBufferId Playback, GraphBufferId BlendStates, GraphBufferId Xpbd, HistoryCopy[] Regions)
    {
        internal static void Declare(ref PassDefinition pass, ref DefinitionCopy data)
        {
            pass.Read(data.Upload, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Write(data.Definitions, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            if (!data.Migrate) return;
            pass.Write(data.Current, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.Previous, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.Playback, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.BlendStates, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.Xpbd, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            if (data.BeforeCurrent == default) return;
            pass.Read(data.BeforeCurrent, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Read(data.BeforePrevious, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Read(data.BeforePlayback, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Read(data.BeforeBlendStates, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Read(data.BeforeXpbd, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
        }
        internal static void Record(ref CopyPassCommandScope commands, in DefinitionCopy data)
        {
            BufferRegion upload = commands.GetBufferRegion(data.Upload);
            commands.CopyBuffer(new(upload.Buffer, upload.Range.Offset, commands.GetBuffer(data.Definitions), 0, (ulong)data.Bytes));
            if (!data.Migrate) return;
            SomeEngine.Graphics.Buffer current = commands.GetBuffer(data.Current), previous = commands.GetBuffer(data.Previous);
            commands.ClearBuffer(current, BufferRange.Whole, 0); commands.ClearBuffer(previous, BufferRange.Whole, 0);
            SomeEngine.Graphics.Buffer playback = commands.GetBuffer(data.Playback);
            commands.ClearBuffer(playback, BufferRange.Whole, 0);
            SomeEngine.Graphics.Buffer blendStates = commands.GetBuffer(data.BlendStates);
            commands.ClearBuffer(blendStates, BufferRange.Whole, 0);
            SomeEngine.Graphics.Buffer xpbd = commands.GetBuffer(data.Xpbd);
            commands.ClearBuffer(xpbd, BufferRange.Whole, 0);
            foreach (HistoryCopy region in data.Regions)
            {
                commands.CopyBuffer(new(commands.GetBuffer(data.BeforeCurrent), (ulong)region.Source.Start * 64,
                    current, (ulong)region.Destination.Start * 64, (ulong)region.Source.Count * 64));
                commands.CopyBuffer(new(commands.GetBuffer(data.BeforePrevious), (ulong)region.Source.Start * 64,
                    previous, (ulong)region.Destination.Start * 64, (ulong)region.Source.Count * 64));
                ulong sourceLocals = commands.GetBuffer(data.BeforeCurrent).Info.Size / 128 + (ulong)region.Source.Start;
                ulong destinationLocals = current.Info.Size / 128 + (ulong)region.Destination.Start;
                commands.CopyBuffer(new(commands.GetBuffer(data.BeforeCurrent), sourceLocals * 64,
                    current, destinationLocals * 64, (ulong)region.Source.Count * 64));
                commands.CopyBuffer(new(commands.GetBuffer(data.BeforePrevious), sourceLocals * 64,
                    previous, destinationLocals * 64, (ulong)region.Source.Count * 64));
                commands.CopyBuffer(new(commands.GetBuffer(data.BeforePlayback), (ulong)region.SourceIndex * 64,
                    playback, (ulong)region.DestinationIndex * 64, 64));
                if (region.BlendCount > 0)
                    commands.CopyBuffer(new(commands.GetBuffer(data.BeforeBlendStates), (ulong)region.SourceBlendStart * 32,
                        blendStates, (ulong)region.DestinationBlendStart * 32, (ulong)region.BlendCount * 32));
                if (region.XpbdCount > 0)
                    commands.CopyBuffer(new(commands.GetBuffer(data.BeforeXpbd), (ulong)region.SourceXpbdStart * 32,
                        xpbd, (ulong)region.DestinationXpbdStart * 32, (ulong)region.XpbdCount * 32));
            }
        }
    }
}
