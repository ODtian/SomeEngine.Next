using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private sealed class VulkanPipelineLayoutState
    {
        private readonly VulkanDevice _device;
        private VkPipelineLayout _native;
        private VkDescriptorSetLayout[] _setLayouts;
        private VkSampler[] _staticSamplers;
        private readonly VariableLayoutReflection _globalLayout;
        private readonly Dictionary<VariableLayoutReflection, VulkanBlockLayout> _sourceBlocks;
        private readonly Dictionary<VariableLayoutReflection, VulkanBlockLayout> _effectiveBlocks;
        private readonly Dictionary<EntryPointReflection, VulkanSpirvBindingTarget[]> _spirvTargets;

        internal VulkanPipelineLayoutState(
            VulkanDevice device,
            VkPipelineLayout native,
            VkDescriptorSetLayout[] setLayouts,
            VkSampler[] staticSamplers,
            VariableLayoutReflection globalLayout,
            Dictionary<VariableLayoutReflection, VulkanBlockLayout> blocks,
            Dictionary<EntryPointReflection, VulkanSpirvBindingTarget[]> spirvTargets)
        {
            _device = device;
            _native = native;
            _setLayouts = setLayouts;
            _staticSamplers = staticSamplers;
            _globalLayout = globalLayout;
            _sourceBlocks = blocks;
            _effectiveBlocks = new Dictionary<VariableLayoutReflection, VulkanBlockLayout>(blocks);
            _spirvTargets = spirvTargets;
        }

        internal VkPipelineLayout Native => _native;
        internal IReadOnlyDictionary<VariableLayoutReflection, VulkanBlockLayout> Blocks =>
            _effectiveBlocks;
        internal ReadOnlySpan<VkDescriptorSetLayout> SetLayouts => _setLayouts;

        internal ReadOnlySpan<VulkanSpirvBindingTarget> GetSpirvTargets(
            EntryPointReflection entry) =>
            _spirvTargets.TryGetValue(entry, out VulkanSpirvBindingTarget[]? targets)
                ? targets
                : throw new ArgumentException(
                    "The entry point is not part of the current Vulkan Pipeline layout.",
                    nameof(entry));

        internal VulkanBlockLayout GetBlock(VariableLayoutReflection layout) =>
            _effectiveBlocks.TryGetValue(layout, out VulkanBlockLayout? block)
                ? block
                : throw new ArgumentException(
                    "The Slang parameter layout is not part of the current Vulkan Pipeline.",
                    nameof(layout));

        internal void ActivateEntryBindings(
            ReadOnlySpan<VulkanSpirvEntryBindings> entries,
            bool includeEntrySlots = true)
        {
            var orderedTargets = new List<VulkanSpirvBindingTarget>();
            var active = new HashSet<VulkanSpirvBindingTarget>();
            foreach (ref readonly VulkanSpirvEntryBindings entry in entries)
                foreach (VulkanSpirvBindingTarget binding in entry.Bindings)
                    if (active.Add(binding))
                        orderedTargets.Add(binding);
            foreach (ref readonly VulkanSpirvEntryBindings entry in entries)
            {
                if (entry.Entry.VarLayout != VariableLayoutReflection.Null &&
                    _sourceBlocks.TryGetValue(entry.Entry.VarLayout, out VulkanBlockLayout? source))
                {
                    _effectiveBlocks[entry.Entry.VarLayout] = CreateActiveBlock(
                        source.ReflectedLayout,
                        [source],
                        entry.Bindings,
                        source.Ordinary);
                }
            }
            if (_globalLayout == VariableLayoutReflection.Null ||
                !_sourceBlocks.TryGetValue(_globalLayout, out VulkanBlockLayout? global))
                return;

            var blocks = new List<VulkanBlockLayout> { global };
            if (includeEntrySlots)
            {
                foreach (ref readonly VulkanSpirvEntryBindings entry in entries)
                {
                    if (entry.Entry.VarLayout != VariableLayoutReflection.Null &&
                        _sourceBlocks.TryGetValue(entry.Entry.VarLayout, out VulkanBlockLayout? block))
                        blocks.Add(block);
                }
            }
            _effectiveBlocks[_globalLayout] = CreateActiveBlock(
                _globalLayout,
                blocks,
                orderedTargets,
                global.Ordinary);
        }

        private static VulkanBlockLayout CreateActiveBlock(
            VariableLayoutReflection reflectedLayout,
            IReadOnlyList<VulkanBlockLayout> sourceBlocks,
            IReadOnlyList<VulkanSpirvBindingTarget> targets,
            VulkanOrdinaryBinding? ordinary)
        {
            var candidates = new Dictionary<VulkanSpirvBindingTarget, List<VulkanDescriptorSlot>>();
            foreach (VulkanBlockLayout block in sourceBlocks)
            {
                foreach (VulkanDescriptorSlot slot in block.Slots)
                {
                    var target = new VulkanSpirvBindingTarget(
                        slot.Set,
                        slot.Binding,
                        slot.Name);
                    if (!candidates.TryGetValue(target, out List<VulkanDescriptorSlot>? values))
                    {
                        values = [];
                        candidates.Add(target, values);
                    }
                    values.Add(slot);
                }
            }
            var slots = new List<VulkanDescriptorSlot>();
            var identities = new HashSet<(uint Set, uint Binding, uint Element)>();
            foreach (VulkanSpirvBindingTarget target in targets)
            {
                if (!candidates.TryGetValue(target, out List<VulkanDescriptorSlot>? values))
                    continue;
                foreach (VulkanDescriptorSlot slot in values.OrderBy(static value => value.ArrayElement))
                    if (identities.Add((slot.Set, slot.Binding, slot.ArrayElement)))
                        slots.Add(slot);
            }
            var sets = new HashSet<uint>(slots.Select(static slot => slot.Set));
            if (ordinary is VulkanOrdinaryBinding ordinaryBinding && !ordinaryBinding.PushConstants)
                sets.Add(ordinaryBinding.Set);
            return new VulkanBlockLayout(
                reflectedLayout,
                slots.ToArray(),
                ordinary,
                sets.Order().ToArray(),
                sourceBlocks.SelectMany(static block => block.SpirvTargets)
                    .Distinct()
                    .ToArray());
        }

        internal void Release()
        {
            VkPipelineLayout native = _native;
            _native = default;
            if (native.Handle != 0)
                _device.Backend.Api.DestroyPipelineLayout(_device.Native, native, null);
            foreach (VkDescriptorSetLayout layout in _setLayouts)
                if (layout.Handle != 0)
                    _device.Backend.Api.DestroyDescriptorSetLayout(_device.Native, layout, null);
            foreach (VkSampler sampler in _staticSamplers)
                if (sampler.Handle != 0)
                    _device.Backend.Api.DestroySampler(_device.Native, sampler, null);
            _setLayouts = [];
            _staticSamplers = [];
        }
    }

    private sealed class VulkanBlockLayout(
        VariableLayoutReflection reflectedLayout,
        VulkanDescriptorSlot[] slots,
        VulkanOrdinaryBinding? ordinary,
        uint[] setIndices,
        VulkanSpirvBindingTarget[] spirvTargets)
    {
        private readonly object _slotOrderGate = new();
        private ResourceBindingType[]? _resolvedTypes;
        private VulkanDescriptorSlot[]? _resolvedSlots;

        internal VariableLayoutReflection ReflectedLayout { get; } = reflectedLayout;
        internal VulkanDescriptorSlot[] Slots { get; } = slots;
        internal VulkanOrdinaryBinding? Ordinary { get; } = ordinary;
        internal uint[] SetIndices { get; } = setIndices;
        internal VulkanSpirvBindingTarget[] SpirvTargets { get; } = spirvTargets;

        internal VulkanDescriptorSlot[] ResolveSlots(ReadOnlySpan<ResourceBinding> bindings)
        {
            VulkanDescriptorSlot[]? resolved = Volatile.Read(ref _resolvedSlots);
            ResourceBindingType[]? types = _resolvedTypes;
            if (resolved is not null && types is not null && TypesMatch(types, bindings))
                return resolved;
            lock (_slotOrderGate)
            {
                resolved = _resolvedSlots;
                types = _resolvedTypes;
                if (resolved is not null && types is not null && TypesMatch(types, bindings))
                    return resolved;
                resolved = CreateSlotOrder(bindings);
                types = bindings.ToArray().Select(static value => value.Type).ToArray();
                _resolvedTypes = types;
                Volatile.Write(ref _resolvedSlots, resolved);
                return resolved;
            }
        }

        private VulkanDescriptorSlot[] CreateSlotOrder(ReadOnlySpan<ResourceBinding> bindings)
        {
            if (bindings.Length != Slots.Length)
            {
                throw new ArgumentException(
                    $"The parameter block requires {Slots.Length} resource bindings, " +
                    $"but received {bindings.Length}.",
                    nameof(bindings));
            }
            var byType = Slots.GroupBy(static slot => slot.BindingType)
                .ToDictionary(
                    static values => values.Key,
                    static values => new Queue<VulkanDescriptorSlot>(values));
            var result = new VulkanDescriptorSlot[bindings.Length];
            for (int index = 0; index < bindings.Length; index++)
            {
                ResourceBindingType type = bindings[index].Type;
                if (!byType.TryGetValue(type, out Queue<VulkanDescriptorSlot>? candidates) ||
                    !candidates.TryDequeue(out result[index]))
                {
                    throw new ArgumentException(
                        $"Resource binding {index} has unexpected type {type}.",
                        nameof(bindings));
                }
            }
            return result;
        }

        private static bool TypesMatch(
            ReadOnlySpan<ResourceBindingType> expected,
            ReadOnlySpan<ResourceBinding> bindings)
        {
            if (expected.Length != bindings.Length)
                return false;
            for (int index = 0; index < expected.Length; index++)
                if (expected[index] != bindings[index].Type)
                    return false;
            return true;
        }
    }

    private readonly record struct VulkanDescriptorSlot(
        ResourceBindingType BindingType,
        DescriptorType DescriptorType,
        uint Set,
        uint Binding,
        uint ArrayElement,
        DescriptorSlotDesc Shape,
        string? Name);

    private readonly record struct VulkanSpirvEntryBindings(
        EntryPointReflection Entry,
        VulkanSpirvBindingTarget[] Bindings);

    private readonly record struct VulkanOrdinaryBinding(
        uint Size,
        bool PushConstants,
        uint Set,
        uint Binding,
        uint PushConstantOffset,
        ShaderStageFlags Stages);

    private sealed class VulkanPipelineLayoutCompiler
    {
        private readonly VulkanDevice _device;
        private readonly Dictionary<uint, Dictionary<uint, SetBindingBuild>> _sets = [];
        private readonly Dictionary<VariableLayoutReflection, VulkanBlockLayout> _blocks = [];
        private readonly HashSet<VariableLayoutReflection> _inProgress = [];
        private readonly List<PushConstantRange> _pushConstants = [];
        private readonly Dictionary<VariableReflection, SamplerDesc> _staticSamplerDescriptions = [];
        private readonly HashSet<VariableReflection> _resolvedStaticSamplers = [];
        private readonly List<VkSampler> _nativeStaticSamplers = [];

        private VulkanPipelineLayoutCompiler(
            VulkanDevice device,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers)
        {
            _device = device;
            foreach (ref readonly StaticSamplerBinding sampler in staticSamplers)
            {
                if (sampler.Sampler == VariableReflection.Null ||
                    !_staticSamplerDescriptions.TryAdd(sampler.Sampler, sampler.Description))
                    throw new ArgumentException("A Vulkan static sampler declaration is null or duplicated.", nameof(staticSamplers));
            }
        }

        internal static VulkanPipelineLayoutState Compile(
            VulkanDevice device,
            ShaderReflection reflection,
            ReadOnlySpan<EntryPointReflection> entries,
            ReadOnlySpan<StaticSamplerBinding> staticSamplers = default)
        {
            var compiler = new VulkanPipelineLayoutCompiler(device, staticSamplers);
            VariableLayoutReflection global = reflection.GetGlobalParamsVarLayout()
                ?? VariableLayoutReflection.Null;
            if (global != VariableLayoutReflection.Null)
                compiler.AddBlock(global, ShaderStageFlags.All);
            foreach (EntryPointReflection entry in entries)
            {
                if (entry.VarLayout != VariableLayoutReflection.Null)
                    compiler.AddBlock(entry.VarLayout, ToNativeShaderStage(entry.Stage));
            }
            compiler.RequireStaticSamplersResolved();
            return compiler.Build(global, entries);
        }

        private void AddBlock(
            VariableLayoutReflection layout,
            ShaderStageFlags stages,
            uint setBase = 0,
            SlangBindingType reflectedOrdinaryType = SlangBindingType.Unknown)
        {
            if (_blocks.TryGetValue(layout, out VulkanBlockLayout? existing))
            {
                MergeBlockStages(existing, stages);
                return;
            }
            if (!_inProgress.Add(layout))
                throw new GraphicsException(GraphicsError.PipelineCreation, "Slang parameter-block reflection contains a cycle.");
            try
            {
                AddBlockCore(layout, stages, setBase, reflectedOrdinaryType);
            }
            finally
            {
                _inProgress.Remove(layout);
            }
        }

        private void AddBlockCore(
            VariableLayoutReflection layout,
            ShaderStageFlags stages,
            uint setBase,
            SlangBindingType reflectedOrdinaryType)
        {
            TypeLayoutReflection dataLayout = GetParameterDataLayout(layout);
            uint ordinarySize = GetOrdinaryDataSize(dataLayout, layout);
            VulkanOrdinaryBinding? ordinary = ordinarySize == 0
                ? null
                : AddOrdinaryBinding(layout, dataLayout, ordinarySize, stages, setBase, reflectedOrdinaryType);
            var slots = new List<VulkanDescriptorSlot>();
            var spirvTargets = new List<VulkanSpirvBindingTarget>();
            var usedSets = new HashSet<uint>();
            if (ordinary is VulkanOrdinaryBinding ordinaryBinding && !ordinaryBinding.PushConstants)
                usedSets.Add(ordinaryBinding.Set);
            AddDescriptorBindings(
                layout,
                dataLayout,
                stages,
                setBase,
                slots,
                usedSets,
                spirvTargets);
            var block = new VulkanBlockLayout(
                layout,
                slots.ToArray(),
                ordinary,
                usedSets.Order().ToArray(),
                spirvTargets.Distinct().ToArray());
            _blocks.Add(layout, block);
            AddChildBlocks(layout, dataLayout, stages, setBase);
        }

        private VulkanOrdinaryBinding AddOrdinaryBinding(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            uint size,
            ShaderStageFlags stages,
            uint setBase,
            SlangBindingType reflectedType)
        {
            bool pushConstants = reflectedType == SlangBindingType.PushConstant ||
                UsesCategory(layout, SlangParameterCategory.PushConstantBuffer) ||
                UsesCategory(dataLayout, SlangParameterCategory.PushConstantBuffer);
            if (pushConstants)
            {
                uint offset = ResolveUInt(layout.GetOffset(SlangParameterCategory.PushConstantBuffer), 0);
                _pushConstants.Add(new PushConstantRange(stages, offset, size));
                return new VulkanOrdinaryBinding(size, true, 0, 0, offset, stages);
            }
            SlangParameterCategory category = SlangParameterCategory.DescriptorTableSlot;
            uint binding = NormalizeOrdinaryDescriptorBinding(
                ResolveUInt(layout.GetOffset(category), 0));
            uint set = checked(setBase + ResolveUInt(layout.GetBindingSpace(category), 0));
            AddSetBinding(
                set,
                binding,
                DescriptorType.UniformBuffer,
                1,
                stages,
                DescriptorBindingFlags.PartiallyBoundBit);
            return new VulkanOrdinaryBinding(size, false, set, binding, 0, stages);
        }

        private void AddDescriptorBindings(
            VariableLayoutReflection layout,
            TypeLayoutReflection dataLayout,
            ShaderStageFlags stages,
            uint setBase,
            List<VulkanDescriptorSlot> slots,
            HashSet<uint> usedSets,
            List<VulkanSpirvBindingTarget> spirvTargets)
        {
            for (nint bindingRange = 0; bindingRange < dataLayout.BindingRangeCount; bindingRange++)
            {
                nint descriptorRangeCount = dataLayout.GetBindingRangeDescriptorRangeCount(bindingRange);
                if (descriptorRangeCount <= 0)
                    continue;
                nint setIndex = dataLayout.GetBindingRangeDescriptorSetIndex(bindingRange);
                nint firstRange = dataLayout.GetBindingRangeFirstDescriptorRangeIndex(bindingRange);
                TypeLayoutReflection leaf = dataLayout.GetBindingRangeLeafTypeLayout(bindingRange).UnwrapArray();
                for (nint relative = 0; relative < descriptorRangeCount; relative++)
                {
                    nint descriptorRange = firstRange + relative;
                    SlangBindingType source = dataLayout.GetDescriptorSetDescriptorRangeType(setIndex, descriptorRange);
                    SlangParameterCategory category = dataLayout.GetDescriptorSetDescriptorRangeCategory(setIndex, descriptorRange);
                    (ResourceBindingType bindingType, DescriptorType descriptorType) = ToNativeBinding(source, category);
                    uint set = checked(
                        setBase +
                        ResolveUInt(dataLayout.GetDescriptorSetSpaceOffset(setIndex), 0) +
                        ResolveUInt(layout.GetBindingSpace(category), 0) +
                        ResolveSubObjectSpace(dataLayout, bindingRange, category));
                    uint logicalBinding = checked(
                        ResolveUInt(dataLayout.GetDescriptorSetDescriptorRangeIndexOffset(setIndex, descriptorRange), 0) +
                        ResolveUInt(layout.GetOffset(category), 0));
                    uint binding = NormalizeReflectedDescriptorBinding(
                        logicalBinding,
                        source,
                        category);
                    nint reflectedCount = dataLayout.GetDescriptorSetDescriptorRangeDescriptorCount(setIndex, descriptorRange);
                    bool unbounded = unchecked((nuint)reflectedCount) == Slang.UnboundedSize;
                    uint count = unbounded
                        ? DescriptorCapacity(descriptorType)
                        : checked((uint)reflectedCount);
                    VariableReflection declaration = dataLayout.GetBindingRangeLeafVariable(bindingRange);
                    spirvTargets.Add(new VulkanSpirvBindingTarget(
                        set,
                        binding,
                        declaration == VariableReflection.Null
                            ? null
                            : declaration.Name));
                    SamplerDesc samplerDescription = default;
                    bool staticSampler = bindingType == ResourceBindingType.Sampler &&
                        declaration != VariableReflection.Null &&
                        _staticSamplerDescriptions.TryGetValue(declaration, out samplerDescription);
                    VkSampler immutableSampler = default;
                    if (staticSampler)
                    {
                        if (unbounded || count != 1)
                            throw new NotSupportedException("A Vulkan static sampler must be one scalar descriptor.");
                        immutableSampler = CreateStaticSampler(samplerDescription);
                        _resolvedStaticSamplers.Add(declaration);
                    }
                    AddSetBinding(
                        set,
                        binding,
                        descriptorType,
                        count,
                        stages,
                        DescriptorBindingFlags.PartiallyBoundBit,
                        immutableSampler);
                    usedSets.Add(set);
                    if (unbounded || staticSampler)
                        continue;
                    DescriptorSlotDesc shape = ResolveDescriptorSlot(leaf, bindingType);
                    for (uint element = 0; element < count; element++)
                    {
                        slots.Add(new VulkanDescriptorSlot(
                            bindingType,
                            descriptorType,
                            set,
                            binding,
                            element,
                            shape,
                            declaration == VariableReflection.Null
                                ? null
                                : declaration.Name));
                    }
                }
            }
        }

        private void AddChildBlocks(
            VariableLayoutReflection parent,
            TypeLayoutReflection dataLayout,
            ShaderStageFlags stages,
            uint setBase)
        {
            for (nint index = 0; index < dataLayout.SubObjectRangeCount; index++)
            {
                nint bindingRange = dataLayout.GetSubObjectRangeBindingRangeIndex(index);
                if (bindingRange < 0 || bindingRange >= dataLayout.BindingRangeCount ||
                    dataLayout.GetBindingRangeDescriptorRangeCount(bindingRange) != 0)
                    continue;
                SlangBindingType type = dataLayout.GetBindingRangeType(bindingRange) & SlangBindingType.BaseMask;
                if (type is not (SlangBindingType.ParameterBlock or SlangBindingType.ConstantBuffer or
                    SlangBindingType.InlineUniformData or SlangBindingType.PushConstant))
                    continue;
                VariableLayoutReflection child = dataLayout.GetSubObjectRangeOffset(index);
                if (child == VariableLayoutReflection.Null)
                    continue;
                uint childSetBase = setBase;
                if (type == SlangBindingType.ParameterBlock)
                {
                    childSetBase = checked(
                        setBase +
                        ResolveUInt(dataLayout.GetSubObjectRangeSpaceOffset(index), 0) +
                        ResolveUInt(child.GetOffset(SlangParameterCategory.SubElementRegisterSpace), 0));
                }
                AddBlock(child, stages, childSetBase, type);
            }
        }

        private void MergeBlockStages(VulkanBlockLayout block, ShaderStageFlags stages)
        {
            foreach (VulkanDescriptorSlot slot in block.Slots)
                AddSetBinding(slot.Set, slot.Binding, slot.DescriptorType, 1, stages, DescriptorBindingFlags.PartiallyBoundBit);
            if (block.Ordinary is VulkanOrdinaryBinding ordinary)
            {
                if (ordinary.PushConstants)
                    _pushConstants.Add(new PushConstantRange(stages, ordinary.PushConstantOffset, ordinary.Size));
                else
                    AddSetBinding(ordinary.Set, ordinary.Binding, DescriptorType.UniformBuffer, 1, stages, DescriptorBindingFlags.PartiallyBoundBit);
            }
        }

        private void AddSetBinding(
            uint set,
            uint binding,
            DescriptorType type,
            uint count,
            ShaderStageFlags stages,
            DescriptorBindingFlags flags)
            => AddSetBinding(set, binding, type, count, stages, flags, default);

        private void AddSetBinding(
            uint set,
            uint binding,
            DescriptorType type,
            uint count,
            ShaderStageFlags stages,
            DescriptorBindingFlags flags,
            VkSampler immutableSampler)
        {
            if (!_sets.TryGetValue(set, out Dictionary<uint, SetBindingBuild>? bindings))
            {
                bindings = [];
                _sets.Add(set, bindings);
            }
            if (bindings.TryGetValue(binding, out SetBindingBuild? existing))
            {
                if (existing.Type != type || existing.Count != count)
                    throw new GraphicsException(
                        GraphicsError.PipelineCreation,
                        $"Vulkan descriptor set {set}, binding {binding} has incompatible " +
                        $"Slang declarations: existing={existing.Type}[{existing.Count}], " +
                        $"incoming={type}[{count}].");
                existing.Stages |= stages;
                existing.Flags |= flags;
                if (immutableSampler.Handle != 0 &&
                    (existing.ImmutableSampler.Handle == 0 ||
                     existing.ImmutableSampler.Handle != immutableSampler.Handle))
                    throw new GraphicsException(GraphicsError.PipelineCreation, "A Vulkan descriptor binding has incompatible immutable samplers.");
                return;
            }
            bindings.Add(binding, new SetBindingBuild(type, count, stages, flags, immutableSampler));
        }

        private VulkanPipelineLayoutState Build(
            VariableLayoutReflection global,
            ReadOnlySpan<EntryPointReflection> entries)
        {
            Dictionary<EntryPointReflection, VulkanSpirvBindingTarget[]> spirvTargets =
                CreateSpirvTargetMap(global, entries);
            uint setCount = _sets.Count == 0 ? 0 : checked(_sets.Keys.Max() + 1);
            VkDescriptorSetLayout[] setLayouts = new VkDescriptorSetLayout[setCount];
            try
            {
                for (uint set = 0; set < setCount; set++)
                    setLayouts[set] = CreateSetLayout(_sets.GetValueOrDefault(set));
                PushConstantRange[] pushConstants = MergePushConstants(_pushConstants);
                fixed (VkDescriptorSetLayout* setPointer = setLayouts)
                fixed (PushConstantRange* pushPointer = pushConstants)
                {
                    PipelineLayoutCreateInfo createInfo = new()
                    {
                        SType = StructureType.PipelineLayoutCreateInfo,
                        SetLayoutCount = setCount,
                        PSetLayouts = setPointer,
                        PushConstantRangeCount = checked((uint)pushConstants.Length),
                        PPushConstantRanges = pushPointer,
                    };
                    VkPipelineLayout native = default;
                    ThrowIfFailed(
                        _device.Backend.Api.CreatePipelineLayout(_device.Native, &createInfo, null, &native),
                        "vkCreatePipelineLayout");
                    return new VulkanPipelineLayoutState(
                        _device,
                        native,
                        setLayouts,
                        _nativeStaticSamplers.ToArray(),
                        global,
                        _blocks,
                        spirvTargets);
                }
            }
            catch
            {
                foreach (VkDescriptorSetLayout layout in setLayouts)
                    if (layout.Handle != 0)
                        _device.Backend.Api.DestroyDescriptorSetLayout(_device.Native, layout, null);
                foreach (VkSampler sampler in _nativeStaticSamplers)
                    if (sampler.Handle != 0)
                        _device.Backend.Api.DestroySampler(_device.Native, sampler, null);
                _nativeStaticSamplers.Clear();
                throw;
            }
        }

        private Dictionary<EntryPointReflection, VulkanSpirvBindingTarget[]> CreateSpirvTargetMap(
            VariableLayoutReflection global,
            ReadOnlySpan<EntryPointReflection> entries)
        {
            var result = new Dictionary<EntryPointReflection, VulkanSpirvBindingTarget[]>();
            foreach (EntryPointReflection entry in entries)
            {
                var targets = new HashSet<VulkanSpirvBindingTarget>();
                var visited = new HashSet<VariableLayoutReflection>();
                AddSpirvTargets(global, targets, visited);
                AddSpirvTargets(entry.VarLayout, targets, visited);
                result.Add(
                    entry,
                    targets.OrderBy(static value => value.Set)
                        .ThenBy(static value => value.Binding)
                        .ToArray());
            }
            return result;
        }

        private void AddSpirvTargets(
            VariableLayoutReflection layout,
            HashSet<VulkanSpirvBindingTarget> targets,
            HashSet<VariableLayoutReflection> visited)
        {
            if (layout == VariableLayoutReflection.Null || !visited.Add(layout))
                return;
            if (!_blocks.TryGetValue(layout, out VulkanBlockLayout? block))
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "A Slang parameter layout is missing from the Vulkan SPIR-V binding map.");
            }
            targets.UnionWith(block.SpirvTargets);
            if (block.Ordinary is VulkanOrdinaryBinding ordinary && !ordinary.PushConstants)
                targets.Add(new VulkanSpirvBindingTarget(ordinary.Set, ordinary.Binding));

            TypeLayoutReflection dataLayout = GetParameterDataLayout(layout);
            for (nint index = 0; index < dataLayout.SubObjectRangeCount; index++)
            {
                nint bindingRange = dataLayout.GetSubObjectRangeBindingRangeIndex(index);
                if (bindingRange < 0 || bindingRange >= dataLayout.BindingRangeCount ||
                    dataLayout.GetBindingRangeDescriptorRangeCount(bindingRange) != 0)
                    continue;
                SlangBindingType type =
                    dataLayout.GetBindingRangeType(bindingRange) & SlangBindingType.BaseMask;
                if (type is not (SlangBindingType.ParameterBlock or SlangBindingType.ConstantBuffer or
                    SlangBindingType.InlineUniformData or SlangBindingType.PushConstant))
                    continue;
                AddSpirvTargets(
                    dataLayout.GetSubObjectRangeOffset(index),
                    targets,
                    visited);
            }
        }

        private VkDescriptorSetLayout CreateSetLayout(Dictionary<uint, SetBindingBuild>? source)
        {
            if (source is null || source.Count == 0)
            {
                DescriptorSetLayoutCreateInfo empty = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                };
                VkDescriptorSetLayout result = default;
                ThrowIfFailed(
                    _device.Backend.Api.CreateDescriptorSetLayout(_device.Native, &empty, null, &result),
                    "vkCreateDescriptorSetLayout(empty)");
                return result;
            }
            KeyValuePair<uint, SetBindingBuild>[] ordered = source.OrderBy(static pair => pair.Key).ToArray();
            DescriptorSetLayoutBinding[] bindings = new DescriptorSetLayoutBinding[ordered.Length];
            DescriptorBindingFlags[] flags = new DescriptorBindingFlags[ordered.Length];
            VkSampler[] immutableSamplers = ordered
                .Where(static pair => pair.Value.ImmutableSampler.Handle != 0)
                .Select(static pair => pair.Value.ImmutableSampler)
                .ToArray();
            int immutableIndex = 0;
            for (int index = 0; index < ordered.Length; index++)
            {
                bindings[index] = new DescriptorSetLayoutBinding(
                    ordered[index].Key,
                    ordered[index].Value.Type,
                    ordered[index].Value.Count,
                    ordered[index].Value.Stages,
                    null);
                flags[index] = ordered[index].Value.Flags;
            }
            fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
            fixed (DescriptorBindingFlags* flagPointer = flags)
            fixed (VkSampler* immutablePointer = immutableSamplers)
            {
                for (int index = 0; index < ordered.Length; index++)
                {
                    if (ordered[index].Value.ImmutableSampler.Handle != 0)
                        bindingPointer[index].PImmutableSamplers = immutablePointer + immutableIndex++;
                }
                DescriptorSetLayoutBindingFlagsCreateInfo bindingFlags = new()
                {
                    SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                    BindingCount = checked((uint)flags.Length),
                    PBindingFlags = flagPointer,
                };
                DescriptorSetLayoutCreateInfo createInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    PNext = &bindingFlags,
                    BindingCount = checked((uint)bindings.Length),
                    PBindings = bindingPointer,
                };
                VkDescriptorSetLayout result = default;
                ThrowIfFailed(
                    _device.Backend.Api.CreateDescriptorSetLayout(_device.Native, &createInfo, null, &result),
                    "vkCreateDescriptorSetLayout");
                return result;
            }
        }

        private uint DescriptorCapacity(DescriptorType type) => type == DescriptorType.Sampler
            ? _device.Capabilities.Limits.SamplerDescriptorCapacity
            : _device.Capabilities.Limits.ResourceDescriptorCapacity;

        private VkSampler CreateStaticSampler(in SamplerDesc desc)
        {
            ValidateSampler(desc);
            SamplerCreateInfo createInfo = new()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = ToNative(desc.MagFilter),
                MinFilter = ToNative(desc.MinFilter),
                MipmapMode = desc.MipFilter == FilterType.Linear
                    ? SamplerMipmapMode.Linear
                    : SamplerMipmapMode.Nearest,
                AddressModeU = ToNative(desc.AddressU),
                AddressModeV = ToNative(desc.AddressV),
                AddressModeW = ToNative(desc.AddressW),
                MipLodBias = desc.MipLodBias,
                AnisotropyEnable = desc.MaximumAnisotropy > 1,
                MaxAnisotropy = desc.MaximumAnisotropy,
                CompareEnable = desc.Comparison.HasValue,
                CompareOp = ToNative(desc.Comparison.GetValueOrDefault()),
                MinLod = desc.MinimumLod,
                MaxLod = desc.MaximumLod,
                BorderColor = ToNativeBorderColor(desc.BorderColor),
            };
            VkSampler native = default;
            ThrowIfFailed(
                _device.Backend.Api.CreateSampler(_device.Native, &createInfo, null, &native),
                "vkCreateSampler(static)");
            _nativeStaticSamplers.Add(native);
            return native;
        }

        private void RequireStaticSamplersResolved()
        {
            foreach (VariableReflection sampler in _staticSamplerDescriptions.Keys)
                if (!_resolvedStaticSamplers.Contains(sampler))
                    throw new ArgumentException($"Static sampler '{sampler.Name}' is not part of the linked Pipeline.");
        }

        private static TypeLayoutReflection GetParameterDataLayout(VariableLayoutReflection layout)
        {
            TypeLayoutReflection result = layout.TypeLayout.UnwrapArray();
            if (result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
                result = result.ElementTypeLayout.UnwrapArray();
            if (result == TypeLayoutReflection.Null)
                throw new GraphicsException(GraphicsError.PipelineCreation, "Slang returned a null parameter data layout.");
            return result;
        }

        private static uint GetOrdinaryDataSize(
            TypeLayoutReflection dataLayout,
            VariableLayoutReflection layout)
        {
            nuint size = dataLayout.GetSize(SlangParameterCategory.Uniform);
            if (size == Slang.UnknownSize || size == Slang.UnboundedSize || size > uint.MaxValue)
                throw new GraphicsException(GraphicsError.PipelineCreation, $"Slang layout '{layout.Name}' has an unresolved ordinary-data size.");
            return checked((uint)size);
        }

        private static (ResourceBindingType Binding, DescriptorType Descriptor) ToNativeBinding(
            SlangBindingType source,
            SlangParameterCategory category)
        {
            SlangBindingType type = source & SlangBindingType.BaseMask;
            bool writable = (source & SlangBindingType.MutableFlag) != 0;
            return type switch
            {
                SlangBindingType.Sampler => (ResourceBindingType.Sampler, DescriptorType.Sampler),
                SlangBindingType.ConstantBuffer => (ResourceBindingType.ConstantBuffer, DescriptorType.UniformBuffer),
                SlangBindingType.Texture => writable
                    ? (ResourceBindingType.TextureUav, DescriptorType.StorageImage)
                    : (ResourceBindingType.TextureSrv, DescriptorType.SampledImage),
                SlangBindingType.TypedBuffer => writable
                    ? (ResourceBindingType.BufferUav, DescriptorType.StorageTexelBuffer)
                    : (ResourceBindingType.BufferSrv, DescriptorType.UniformTexelBuffer),
                SlangBindingType.RawBuffer => writable
                    ? (ResourceBindingType.BufferUav, DescriptorType.StorageBuffer)
                    : (ResourceBindingType.BufferSrv, DescriptorType.StorageBuffer),
                SlangBindingType.RayTracingAccelerationStructure =>
                    (ResourceBindingType.AccelerationStructure, DescriptorType.AccelerationStructureKhr),
                SlangBindingType.InputRenderTarget =>
                    (ResourceBindingType.TextureSrv, DescriptorType.InputAttachment),
                SlangBindingType.CombinedTextureSampler when category == SlangParameterCategory.SamplerState =>
                    (ResourceBindingType.Sampler, DescriptorType.Sampler),
                SlangBindingType.CombinedTextureSampler =>
                    (ResourceBindingType.TextureSrv, DescriptorType.SampledImage),
                _ => throw new GraphicsException(GraphicsError.PipelineCreation, $"Slang binding type {source} has no Vulkan descriptor mapping."),
            };
        }

        private static DescriptorSlotDesc ResolveDescriptorSlot(
            TypeLayoutReflection leaf,
            ResourceBindingType type) => type switch
        {
            ResourceBindingType.ConstantBuffer or ResourceBindingType.Sampler or
            ResourceBindingType.AccelerationStructure => new DescriptorSlotDesc(type),
            ResourceBindingType.BufferSrv or ResourceBindingType.BufferUav =>
                ResolveBufferSlot(leaf, type),
            ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav => new DescriptorSlotDesc(
                type,
                ResolveCanonicalResourceFormat(leaf.ResourceResultType),
                TextureDimension: ResolveTextureDimension(leaf.ResourceShape)),
            _ => throw new GraphicsException(GraphicsError.PipelineCreation, $"Unsupported Vulkan descriptor slot {type}."),
        };

        private static DescriptorSlotDesc ResolveBufferSlot(
            TypeLayoutReflection leaf,
            ResourceBindingType type)
        {
            SlangResourceShape shape = leaf.ResourceShape & SlangResourceShape.BaseShapeMask;
            bool counter = type == ResourceBindingType.BufferUav &&
                leaf.ExplicitCounter != VariableLayoutReflection.Null;
            if (shape == SlangResourceShape.TextureBuffer)
                return new DescriptorSlotDesc(type, ResolveCanonicalResourceFormat(leaf.ResourceResultType), HasCounter: counter);
            if (shape == SlangResourceShape.StructuredBuffer)
            {
                TypeLayoutReflection element = leaf.ElementTypeLayout;
                nuint stride = element.GetStride(SlangParameterCategory.Uniform);
                if (stride == 0 || stride == Slang.UnknownSize || stride == Slang.UnboundedSize)
                    stride = element.GetSize(SlangParameterCategory.Uniform);
                return new DescriptorSlotDesc(type, StructureStride: checked((uint)stride), HasCounter: counter);
            }
            return new DescriptorSlotDesc(type, HasCounter: counter);
        }

        private static TextureViewDimension ResolveTextureDimension(SlangResourceShape shape)
        {
            const SlangResourceShape mask = SlangResourceShape.BaseShapeMask |
                SlangResourceShape.TextureArrayFlag | SlangResourceShape.TextureMultisampleFlag;
            return (shape & mask) switch
            {
                SlangResourceShape.Texture1D => TextureViewDimension.Texture1D,
                SlangResourceShape.Texture1DArray => TextureViewDimension.Texture1DArray,
                SlangResourceShape.Texture2D => TextureViewDimension.Texture2D,
                SlangResourceShape.Texture2DArray => TextureViewDimension.Texture2DArray,
                SlangResourceShape.Texture2DMultisample => TextureViewDimension.Texture2DMultisampled,
                SlangResourceShape.Texture2DMultisampleArray => TextureViewDimension.Texture2DMultisampledArray,
                SlangResourceShape.Texture3D => TextureViewDimension.Texture3D,
                SlangResourceShape.TextureCube => TextureViewDimension.Cube,
                SlangResourceShape.TextureCubeArray => TextureViewDimension.CubeArray,
                _ => throw new GraphicsException(GraphicsError.PipelineCreation, $"Slang texture shape {shape} is unsupported."),
            };
        }

        private static RhiFormat ResolveCanonicalResourceFormat(TypeReflection type)
        {
            uint components = type.Kind == SlangTypeKind.Vector
                ? Math.Max(type.RowCount, type.ColumnCount)
                : 1;
            components = components == 3 ? 4 : components;
            return (type.ScalarType, components) switch
            {
                (SlangScalarType.Bool or SlangScalarType.UInt32, 1) => RhiFormat.R32UInt,
                (SlangScalarType.Bool or SlangScalarType.UInt32, 2) => RhiFormat.R32G32UInt,
                (SlangScalarType.Bool or SlangScalarType.UInt32, 4) => RhiFormat.R32G32B32A32UInt,
                (SlangScalarType.Int32, 1) => RhiFormat.R32SInt,
                (SlangScalarType.Int32, 2) => RhiFormat.R32G32SInt,
                (SlangScalarType.Int32, 4) => RhiFormat.R32G32B32A32SInt,
                (SlangScalarType.Float32, 1) => RhiFormat.R32Float,
                (SlangScalarType.Float32, 2) => RhiFormat.R32G32Float,
                (SlangScalarType.Float32, 4) => RhiFormat.R32G32B32A32Float,
                (SlangScalarType.UInt16, 1) => RhiFormat.R16UInt,
                (SlangScalarType.UInt16, 2) => RhiFormat.R16G16UInt,
                (SlangScalarType.UInt16, 4) => RhiFormat.R16G16B16A16UInt,
                (SlangScalarType.Int16, 1) => RhiFormat.R16SInt,
                (SlangScalarType.Int16, 2) => RhiFormat.R16G16SInt,
                (SlangScalarType.Int16, 4) => RhiFormat.R16G16B16A16SInt,
                (SlangScalarType.Float16, 1) => RhiFormat.R16Float,
                (SlangScalarType.Float16, 2) => RhiFormat.R16G16Float,
                (SlangScalarType.Float16, 4) => RhiFormat.R16G16B16A16Float,
                _ => throw new GraphicsException(GraphicsError.PipelineCreation, $"Slang resource result '{type.Name}' has no RHI format."),
            };
        }

        private static uint ResolveSubObjectSpace(
            TypeLayoutReflection layout,
            nint bindingRange,
            SlangParameterCategory category)
        {
            for (nint index = 0; index < layout.SubObjectRangeCount; index++)
            {
                if (layout.GetSubObjectRangeBindingRangeIndex(index) != bindingRange)
                    continue;
                VariableLayoutReflection variable = layout.GetSubObjectRangeOffset(index);
                return variable == VariableLayoutReflection.Null
                    ? 0
                    : ResolveUInt(variable.GetBindingSpace(category), 0);
            }
            return 0;
        }

        private static bool UsesCategory(TypeLayoutReflection layout, SlangParameterCategory category)
        {
            for (uint index = 0; index < layout.CategoryCount; index++)
                if (layout.GetCategoryByIndex(index) == category)
                    return true;
            return false;
        }

        private static bool UsesCategory(VariableLayoutReflection layout, SlangParameterCategory category)
        {
            for (uint index = 0; index < layout.CategoryCount; index++)
                if (layout.GetCategoryByIndex(index) == category)
                    return true;
            return false;
        }

        private static uint ResolveUInt(nuint value, uint fallback) =>
            value == Slang.UnknownSize || value == Slang.UnboundedSize
                ? fallback
                : checked((uint)value);

        private static uint ResolveUInt(nint value, uint fallback) =>
            value < 0 || unchecked((nuint)value) == Slang.UnknownSize ||
            unchecked((nuint)value) == Slang.UnboundedSize
                ? fallback
                : checked((uint)value);

        private static ShaderStageFlags ToNativeShaderStage(SlangStage stage) => stage switch
        {
            SlangStage.Vertex => ShaderStageFlags.VertexBit,
            SlangStage.Hull => ShaderStageFlags.TessellationControlBit,
            SlangStage.Domain => ShaderStageFlags.TessellationEvaluationBit,
            SlangStage.Geometry => ShaderStageFlags.GeometryBit,
            SlangStage.Fragment => ShaderStageFlags.FragmentBit,
            SlangStage.Compute => ShaderStageFlags.ComputeBit,
            SlangStage.RayGeneration => ShaderStageFlags.RaygenBitKhr,
            SlangStage.Intersection => ShaderStageFlags.IntersectionBitKhr,
            SlangStage.AnyHit => ShaderStageFlags.AnyHitBitKhr,
            SlangStage.ClosestHit => ShaderStageFlags.ClosestHitBitKhr,
            SlangStage.Miss => ShaderStageFlags.MissBitKhr,
            SlangStage.Callable => ShaderStageFlags.CallableBitKhr,
            SlangStage.Mesh => ShaderStageFlags.MeshBitExt,
            SlangStage.Amplification => ShaderStageFlags.TaskBitExt,
            _ => ShaderStageFlags.All,
        };

        private static PushConstantRange[] MergePushConstants(List<PushConstantRange> source)
        {
            if (source.Count <= 1)
                return source.ToArray();
            return source
                .GroupBy(static range => (range.Offset, range.Size))
                .Select(static group => new PushConstantRange(
                    group.Aggregate(ShaderStageFlags.None, static (stages, range) => stages | range.StageFlags),
                    group.Key.Offset,
                    group.Key.Size))
                .ToArray();
        }

        private sealed class SetBindingBuild(
            DescriptorType type,
            uint count,
            ShaderStageFlags stages,
            DescriptorBindingFlags flags,
            VkSampler immutableSampler)
        {
            internal DescriptorType Type { get; } = type;
            internal uint Count { get; } = count;
            internal ShaderStageFlags Stages { get; set; } = stages;
            internal DescriptorBindingFlags Flags { get; set; } = flags;
            internal VkSampler ImmutableSampler { get; } = immutableSampler;
        }
    }
}
