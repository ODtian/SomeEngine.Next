using System.Runtime.InteropServices;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private const uint DescriptorRegisterClassStride = 65_536;

    private enum DescriptorRegisterClass : uint
    {
        UnorderedAccess = 0,
        ShaderResource = 1,
        Sampler = 2,
        ConstantBuffer = 3,
    }

    private enum SpirvTypeKind : byte
    {
        Unknown,
        Pointer,
        Array,
        RuntimeArray,
        Struct,
        Image,
        Sampler,
        SampledImage,
        AccelerationStructure,
    }

    [Flags]
    private enum SpirvDecorationFlags : byte
    {
        None = 0,
        Block = 1 << 0,
        BufferBlock = 1 << 1,
        NonWritable = 1 << 2,
        NonReadable = 1 << 3,
    }

    internal readonly record struct VulkanSpirvBindingTarget(
        uint Set,
        uint Binding,
        string? Name = null,
        uint SourceSet = uint.MaxValue)
    {
        internal uint LogicalSet => SourceSet == uint.MaxValue ? Set : SourceSet;
    }

    private readonly record struct SpirvBindingDecoration(int ValueWord, uint Target);

    private readonly record struct ResolvedSpirvBinding(
        int ValueWord,
        int SetValueWord,
        uint Target,
        uint Set,
        uint LogicalBinding,
        DescriptorRegisterClass RegisterClass,
        string? Name);

    private sealed class SpirvBindingFacts
    {
        private readonly uint _bound;

        private SpirvBindingFacts(int bound)
        {
            _bound = checked((uint)bound);
            Kinds = new SpirvTypeKind[bound];
            ReferencedTypes = new uint[bound];
            ImageSampledOperands = new byte[bound];
            VariableTypes = new uint[bound];
            VariableStorageClasses = new uint[bound];
            DescriptorSets = new uint[bound];
            DescriptorSetValueWords = new int[bound];
            Array.Fill(DescriptorSetValueWords, -1);
            Decorations = new SpirvDecorationFlags[bound];
        }

        internal SpirvTypeKind[] Kinds { get; }
        internal uint[] ReferencedTypes { get; }
        internal byte[] ImageSampledOperands { get; }
        internal uint[] VariableTypes { get; }
        internal uint[] VariableStorageClasses { get; }
        internal uint[] DescriptorSets { get; }
        internal int[] DescriptorSetValueWords { get; }
        internal SpirvDecorationFlags[] Decorations { get; }
        internal List<SpirvBindingDecoration> Bindings { get; } = [];
        internal Dictionary<uint, string> Names { get; } = [];

        internal static SpirvBindingFacts Parse(ReadOnlySpan<uint> words, int bound)
        {
            var result = new SpirvBindingFacts(bound);
            for (int index = 5; index < words.Length;)
            {
                uint instruction = words[index];
                int wordCount = checked((int)(instruction >> 16));
                if (wordCount <= 0 || index > words.Length - wordCount)
                    throw InvalidSpirv($"SPIR-V instruction at word {index} is malformed.");
                result.ParseInstruction(words, index, wordCount, instruction & 0xffff);
                index += wordCount;
            }
            return result;
        }

        internal int RequireId(uint id)
        {
            if (id == 0 || id >= _bound)
                throw InvalidSpirv($"SPIR-V references invalid ID {id} with bound {_bound}.");
            return checked((int)id);
        }

        private void ParseInstruction(
            ReadOnlySpan<uint> words,
            int index,
            int wordCount,
            uint opcode)
        {
            switch (opcode)
            {
                case 5 when wordCount >= 3: // OpName
                    Names[words[index + 1]] = DecodeSpirvString(
                        words.Slice(index + 2, wordCount - 2));
                    break;
                case 25 when wordCount >= 9: // OpTypeImage
                    SetKind(words[index + 1], SpirvTypeKind.Image);
                    ImageSampledOperands[RequireId(words[index + 1])] =
                        checked((byte)words[index + 7]);
                    break;
                case 26 when wordCount >= 2: // OpTypeSampler
                    SetKind(words[index + 1], SpirvTypeKind.Sampler);
                    break;
                case 27 when wordCount >= 3: // OpTypeSampledImage
                    SetKind(words[index + 1], SpirvTypeKind.SampledImage, words[index + 2]);
                    break;
                case 28 when wordCount >= 4: // OpTypeArray
                    SetKind(words[index + 1], SpirvTypeKind.Array, words[index + 2]);
                    break;
                case 29 when wordCount >= 3: // OpTypeRuntimeArray
                    SetKind(words[index + 1], SpirvTypeKind.RuntimeArray, words[index + 2]);
                    break;
                case 30 when wordCount >= 2: // OpTypeStruct
                    SetKind(words[index + 1], SpirvTypeKind.Struct);
                    break;
                case 32 when wordCount >= 4: // OpTypePointer
                    SetKind(words[index + 1], SpirvTypeKind.Pointer, words[index + 3]);
                    break;
                case 59 when wordCount >= 4: // OpVariable
                    ParseVariable(words, index);
                    break;
                case 71 when wordCount >= 3: // OpDecorate
                    ParseDecoration(words, index, wordCount);
                    break;
                case 72 when wordCount >= 4: // OpMemberDecorate
                    Decorations[RequireId(words[index + 1])] |=
                        ToDecorationFlag(words[index + 3]);
                    break;
                case 5341 when wordCount >= 2: // OpTypeAccelerationStructureKHR
                    SetKind(words[index + 1], SpirvTypeKind.AccelerationStructure);
                    break;
            }
        }

        private void ParseVariable(ReadOnlySpan<uint> words, int index)
        {
            int id = RequireId(words[index + 2]);
            VariableTypes[id] = words[index + 1];
            VariableStorageClasses[id] = words[index + 3];
        }

        private void ParseDecoration(ReadOnlySpan<uint> words, int index, int wordCount)
        {
            int id = RequireId(words[index + 1]);
            uint decoration = words[index + 2];
            if (decoration == 33 && wordCount >= 4)
                Bindings.Add(new SpirvBindingDecoration(index + 3, words[index + 1]));
            else if (decoration == 34 && wordCount >= 4)
            {
                DescriptorSets[id] = words[index + 3];
                DescriptorSetValueWords[id] = index + 3;
            }
            else
                Decorations[id] |= ToDecorationFlag(decoration);
        }

        private void SetKind(uint id, SpirvTypeKind kind, uint referencedType = 0)
        {
            int index = RequireId(id);
            Kinds[index] = kind;
            if (referencedType == 0)
                return;
            _ = RequireId(referencedType);
            ReferencedTypes[index] = referencedType;
        }
    }

    internal static byte[] NormalizeSpirvDescriptorBindings(
        ReadOnlySpan<byte> code,
        ReadOnlySpan<VulkanSpirvBindingTarget> targets,
        out VulkanSpirvBindingTarget[] activeTargets)
    {
        if (code.Length < 5 * sizeof(uint) || (code.Length & 3) != 0)
            throw InvalidSpirv("SPIR-V is truncated or not word aligned.");
        byte[] normalized = code.ToArray();
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(normalized.AsSpan());
        if (words[0] != 0x07230203)
            throw InvalidSpirv("SPIR-V has an invalid magic word.");
        uint boundValue = words[3];
        if (boundValue == 0 || boundValue > int.MaxValue ||
            boundValue > checked((uint)Math.Max(words.Length * 16L, 4_096L)))
            throw InvalidSpirv($"SPIR-V declares an unreasonable ID bound {boundValue}.");
        int bound = checked((int)boundValue);
        SpirvBindingFacts facts = SpirvBindingFacts.Parse(words, bound);
        ResolvedSpirvBinding[] bindings = ResolveSpirvBindings(words, facts);
        VulkanSpirvBindingTarget[] mappedTargets = ApplySpirvBindingTargets(
            words,
            bindings,
            targets);
        activeTargets = CollectActiveSpirvTargets(bindings, mappedTargets);
        return AddSpirvAliasingDecorations(normalized, bindings, mappedTargets);
    }

    private static byte[] AddSpirvAliasingDecorations(
        byte[] code,
        ReadOnlySpan<ResolvedSpirvBinding> bindings,
        ReadOnlySpan<VulkanSpirvBindingTarget> targets)
    {
        var aliased = new HashSet<uint>();
        ResolvedSpirvBinding[] bindingValues = bindings.ToArray();
        VulkanSpirvBindingTarget[] targetValues = targets.ToArray();
        foreach (IGrouping<(uint Set, uint Binding), int> group in
                 Enumerable.Range(0, bindingValues.Length).GroupBy(index => (
                     targetValues[index].Set,
                     targetValues[index].Binding)))
        {
            if (group.Select(index => bindingValues[index].Target).Distinct().Count() <= 1)
                continue;
            foreach (int index in group)
                aliased.Add(bindingValues[index].Target);
        }
        if (aliased.Count == 0)
            return code;
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
        var existing = new HashSet<uint>();
        int insertion = -1;
        for (int index = 5; index < words.Length;)
        {
            uint instruction = words[index];
            int wordCount = checked((int)(instruction >> 16));
            uint opcode = instruction & 0xffff;
            if (wordCount <= 0 || index > words.Length - wordCount)
                throw InvalidSpirv("SPIR-V alias instrumentation found a malformed instruction.");
            if (opcode == 71 && wordCount >= 3 && words[index + 2] == 20)
                existing.Add(words[index + 1]);
            if (insertion < 0 && opcode is >= 19 and <= 39)
                insertion = index;
            index += wordCount;
        }
        aliased.ExceptWith(existing);
        if (aliased.Count == 0)
            return code;
        if (insertion < 0)
            throw InvalidSpirv("SPIR-V has no type section for alias instrumentation.");
        var result = new uint[checked(words.Length + aliased.Count * 3)];
        words[..insertion].CopyTo(result);
        int cursor = insertion;
        foreach (uint target in aliased.Order())
        {
            result[cursor++] = (3u << 16) | 71u;
            result[cursor++] = target;
            result[cursor++] = 20;
        }
        words[insertion..].CopyTo(result.AsSpan(cursor));
        return MemoryMarshal.AsBytes(result.AsSpan()).ToArray();
    }

    private static VulkanSpirvBindingTarget[] CollectActiveSpirvTargets(
        ReadOnlySpan<ResolvedSpirvBinding> bindings,
        ReadOnlySpan<VulkanSpirvBindingTarget> mappedTargets)
    {
        var ordered = new List<(
            VulkanSpirvBindingTarget Target,
            uint PhysicalBinding,
            uint SourceBinding,
            uint SourceId)>(bindings.Length);
        foreach (ref readonly ResolvedSpirvBinding binding in bindings)
        {
            int index = ordered.Count;
            VulkanSpirvBindingTarget target = mappedTargets[index];
            ordered.Add((
                target,
                target.Binding,
                binding.LogicalBinding,
                binding.Target));
        }
        ordered.Sort(static (left, right) =>
        {
            int set = left.Target.Set.CompareTo(right.Target.Set);
            if (set != 0) return set;
            int physical = left.PhysicalBinding.CompareTo(right.PhysicalBinding);
            if (physical != 0) return physical;
            int source = left.SourceBinding.CompareTo(right.SourceBinding);
            return source != 0 ? source : left.SourceId.CompareTo(right.SourceId);
        });
        var result = new List<VulkanSpirvBindingTarget>();
        var seen = new HashSet<VulkanSpirvBindingTarget>();
        foreach (var binding in ordered)
            if (seen.Add(binding.Target))
                result.Add(binding.Target);
        return result.ToArray();
    }

    private static ResolvedSpirvBinding[] ResolveSpirvBindings(
        ReadOnlySpan<uint> words,
        SpirvBindingFacts facts)
    {
        var result = new ResolvedSpirvBinding[facts.Bindings.Count];
        for (int index = 0; index < facts.Bindings.Count; index++)
        {
            SpirvBindingDecoration binding = facts.Bindings[index];
            int variable = facts.RequireId(binding.Target);
            DescriptorRegisterClass registerClass = ResolveSpirvRegisterClass(
                variable,
                facts.Kinds,
                facts.ReferencedTypes,
                facts.ImageSampledOperands,
                facts.VariableTypes,
                facts.VariableStorageClasses,
                facts.Decorations);
            result[index] = new ResolvedSpirvBinding(
                binding.ValueWord,
                facts.DescriptorSetValueWords[variable],
                binding.Target,
                facts.DescriptorSets[variable],
                words[binding.ValueWord],
                registerClass,
                facts.Names.GetValueOrDefault(binding.Target));
        }
        return result;
    }

    private static VulkanSpirvBindingTarget[] ApplySpirvBindingTargets(
        Span<uint> words,
        ResolvedSpirvBinding[] bindings,
        ReadOnlySpan<VulkanSpirvBindingTarget> targets)
    {
        Dictionary<(uint Set, DescriptorRegisterClass RegisterClass), VulkanSpirvBindingTarget[]>
            targetGroups =
            targets.ToArray()
                .GroupBy(static target => (
                    target.LogicalSet,
                    DecodeDescriptorRegisterClass(target.Binding)))
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .Distinct()
                        .OrderBy(static target => target.Binding)
                        .ToArray());
        var mapped = new Dictionary<int, VulkanSpirvBindingTarget>();
        foreach (IGrouping<(uint Set, DescriptorRegisterClass RegisterClass), ResolvedSpirvBinding> group
                 in bindings.GroupBy(static binding => (
                     binding.Set,
                     binding.RegisterClass)))
        {
            ApplySpirvBindingGroup(words, group, targetGroups, mapped);
        }
        var result = new VulkanSpirvBindingTarget[bindings.Length];
        for (int index = 0; index < bindings.Length; index++)
        {
            if (!mapped.TryGetValue(bindings[index].ValueWord, out result[index]))
                throw InvalidSpirv("A SPIR-V descriptor binding was not mapped to Slang reflection.");
        }
        return result;
    }

    private static void ApplySpirvBindingGroup(
        Span<uint> words,
        IGrouping<(uint Set, DescriptorRegisterClass RegisterClass), ResolvedSpirvBinding> group,
        IReadOnlyDictionary<
            (uint Set, DescriptorRegisterClass RegisterClass),
            VulkanSpirvBindingTarget[]> targetGroups,
        Dictionary<int, VulkanSpirvBindingTarget> mapped)
    {
        IGrouping<uint, ResolvedSpirvBinding>[] sourceGroups = group
            .GroupBy(static binding => binding.LogicalBinding)
            .OrderBy(static values => values.Key)
            .ToArray();
        if (!targetGroups.TryGetValue(
                group.Key,
                out VulkanSpirvBindingTarget[]? targets))
        {
            throw InvalidSpirv(
                $"SPIR-V descriptor set {group.Key.Set}, register class " +
                $"{group.Key.RegisterClass} has no matching Slang reflection binding.");
        }
        var available = new List<VulkanSpirvBindingTarget>(targets);
        var pending = new List<IGrouping<uint, ResolvedSpirvBinding>>();
        foreach (IGrouping<uint, ResolvedSpirvBinding> sourceGroup in sourceGroups)
        {
            VulkanSpirvBindingTarget[] named = available
                .Where(target => sourceGroup.Any(source => NamesMatch(source.Name, target.Name)))
                .ToArray();
            if (named.Select(static target => target.Binding).Distinct().Count() != 1)
            {
                if (sourceGroup.Any(static source => !string.IsNullOrWhiteSpace(source.Name)) &&
                    available.Any(static target => !string.IsNullOrWhiteSpace(target.Name)))
                {
                    throw InvalidSpirv(
                        "A named SPIR-V descriptor could not be matched uniquely to Slang " +
                        $"reflection: source=[{string.Join(", ", sourceGroup.Select(
                            static source => source.Name ?? $"%{source.Target}"))}], " +
                        $"targets=[{string.Join(", ", available.Select(
                            static target => target.Name ?? $"{target.Set}:{target.Binding}"))}].");
                }
                pending.Add(sourceGroup);
                continue;
            }
            VulkanSpirvBindingTarget target = named[0];
            AssignSpirvTarget(words, sourceGroup, target, mapped);
            available.RemoveAll(value => value.Binding == target.Binding);
        }

        VulkanSpirvBindingTarget[] remaining = available
            .GroupBy(static target => target.Binding)
            .Select(static values => values.First())
            .OrderBy(static target => target.Binding)
            .ToArray();
        if (pending.Count == remaining.Length)
        {
            for (int index = 0; index < pending.Count; index++)
                AssignSpirvTarget(words, pending[index], remaining[index], mapped);
            return;
        }
        foreach (IGrouping<uint, ResolvedSpirvBinding> sourceGroup in pending)
        {
            ResolvedSpirvBinding source = sourceGroup.First();
            uint direct = EncodeDescriptorBinding(
                source.LogicalBinding,
                source.RegisterClass);
            VulkanSpirvBindingTarget[] matches = remaining
                .Where(target => target.Binding == direct)
                .ToArray();
            if (matches.Length != 1)
            {
                throw InvalidSpirv(
                    $"SPIR-V descriptor set {group.Key.Set}, register class " +
                    $"{group.Key.RegisterClass} exposes {sourceGroups.Length} bindings, " +
                    $"while Slang reflection exposes {targets.Length} bindings: " +
                    string.Join(", ", group.Select(static value =>
                        $"{value.Name ?? $"%{value.Target}"}@{value.LogicalBinding}")) + ".");
            }
            AssignSpirvTarget(words, sourceGroup, matches[0], mapped);
        }
    }

    private static void AssignSpirvTarget(
        Span<uint> words,
        IEnumerable<ResolvedSpirvBinding> sources,
        VulkanSpirvBindingTarget target,
        Dictionary<int, VulkanSpirvBindingTarget> mapped)
    {
        foreach (ResolvedSpirvBinding source in sources)
        {
            if (source.SetValueWord < 0)
                throw InvalidSpirv($"SPIR-V descriptor variable %{source.Target} has no DescriptorSet decoration.");
            words[source.ValueWord] = target.Binding;
            words[source.SetValueWord] = target.Set;
            mapped.Add(source.ValueWord, target);
        }
    }

    private static bool NamesMatch(string? source, string? target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return false;
        return string.Equals(source, target, StringComparison.Ordinal) ||
            source.EndsWith($".{target}", StringComparison.Ordinal);
    }

    internal static uint NormalizeReflectedDescriptorBinding(
        uint logicalBinding,
        SlangBindingType source,
        SlangParameterCategory category)
    {
        DescriptorRegisterClass registerClass = category switch
        {
            SlangParameterCategory.UnorderedAccess => DescriptorRegisterClass.UnorderedAccess,
            SlangParameterCategory.ShaderResource => DescriptorRegisterClass.ShaderResource,
            SlangParameterCategory.SamplerState => DescriptorRegisterClass.Sampler,
            SlangParameterCategory.ConstantBuffer => DescriptorRegisterClass.ConstantBuffer,
            _ => ResolveReflectedRegisterClass(source),
        };
        return EncodeDescriptorBinding(logicalBinding, registerClass);
    }

    internal static uint NormalizeOrdinaryDescriptorBinding(uint logicalBinding) =>
        EncodeDescriptorBinding(logicalBinding, DescriptorRegisterClass.ConstantBuffer);

    private static DescriptorRegisterClass ResolveReflectedRegisterClass(SlangBindingType source)
    {
        bool writable = (source & SlangBindingType.MutableFlag) != 0;
        return (source & SlangBindingType.BaseMask) switch
        {
            SlangBindingType.Sampler => DescriptorRegisterClass.Sampler,
            SlangBindingType.ConstantBuffer => DescriptorRegisterClass.ConstantBuffer,
            SlangBindingType.Texture or SlangBindingType.TypedBuffer or SlangBindingType.RawBuffer =>
                writable
                    ? DescriptorRegisterClass.UnorderedAccess
                    : DescriptorRegisterClass.ShaderResource,
            SlangBindingType.CombinedTextureSampler or
            SlangBindingType.InputRenderTarget or
            SlangBindingType.RayTracingAccelerationStructure =>
                DescriptorRegisterClass.ShaderResource,
            _ => throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang descriptor type {source} has no Vulkan register-class mapping."),
        };
    }

    private static DescriptorRegisterClass ResolveSpirvRegisterClass(
        int variable,
        SpirvTypeKind[] kinds,
        uint[] referencedTypes,
        byte[] imageSampledOperands,
        uint[] variableTypes,
        uint[] variableStorageClasses,
        SpirvDecorationFlags[] decorations)
    {
        uint pointerId = variableTypes[variable];
        if (pointerId == 0 || pointerId >= kinds.Length ||
            kinds[pointerId] != SpirvTypeKind.Pointer)
            throw InvalidSpirv($"SPIR-V descriptor variable %{variable} has no pointer type.");
        uint typeId = referencedTypes[pointerId];
        typeId = UnwrapDescriptorArray(typeId, kinds, referencedTypes);
        SpirvTypeKind kind = kinds[typeId];
        uint storageClass = variableStorageClasses[variable];
        return storageClass switch
        {
            0 => kind switch // UniformConstant
            {
                SpirvTypeKind.Sampler => DescriptorRegisterClass.Sampler,
                SpirvTypeKind.Image => imageSampledOperands[typeId] == 2
                    ? DescriptorRegisterClass.UnorderedAccess
                    : DescriptorRegisterClass.ShaderResource,
                SpirvTypeKind.SampledImage or SpirvTypeKind.AccelerationStructure =>
                    DescriptorRegisterClass.ShaderResource,
                _ => throw InvalidSpirv(
                    $"SPIR-V UniformConstant variable %{variable} has unsupported type {kind}."),
            },
            2 => HasDecoration(
                    typeId,
                    SpirvDecorationFlags.BufferBlock,
                    kinds,
                    referencedTypes,
                    decorations)
                ? ResolveStorageBufferClass(variable, typeId, kinds, referencedTypes, decorations)
                : DescriptorRegisterClass.ConstantBuffer, // Uniform
            12 => ResolveStorageBufferClass(
                variable,
                typeId,
                kinds,
                referencedTypes,
                decorations), // StorageBuffer
            _ => throw InvalidSpirv(
                $"SPIR-V descriptor variable %{variable} uses unsupported storage class {storageClass}."),
        };
    }

    private static DescriptorRegisterClass ResolveStorageBufferClass(
        int variable,
        uint typeId,
        SpirvTypeKind[] kinds,
        uint[] referencedTypes,
        SpirvDecorationFlags[] decorations)
    {
        SpirvDecorationFlags variableFlags = decorations[variable];
        bool nonWritable = (variableFlags & SpirvDecorationFlags.NonWritable) != 0 ||
            HasDecoration(typeId, SpirvDecorationFlags.NonWritable, kinds, referencedTypes, decorations);
        bool nonReadable = (variableFlags & SpirvDecorationFlags.NonReadable) != 0 ||
            HasDecoration(typeId, SpirvDecorationFlags.NonReadable, kinds, referencedTypes, decorations);
        return nonWritable && !nonReadable
            ? DescriptorRegisterClass.ShaderResource
            : DescriptorRegisterClass.UnorderedAccess;
    }

    private static uint UnwrapDescriptorArray(
        uint typeId,
        SpirvTypeKind[] kinds,
        uint[] referencedTypes)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (typeId == 0 || typeId >= kinds.Length)
                throw InvalidSpirv($"SPIR-V descriptor references invalid type ID {typeId}.");
            if (kinds[typeId] is not (SpirvTypeKind.Array or SpirvTypeKind.RuntimeArray))
                return typeId;
            typeId = referencedTypes[typeId];
        }
        throw InvalidSpirv("SPIR-V descriptor array nesting exceeds 32 levels.");
    }

    private static bool HasDecoration(
        uint typeId,
        SpirvDecorationFlags required,
        SpirvTypeKind[] kinds,
        uint[] referencedTypes,
        SpirvDecorationFlags[] decorations)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (typeId == 0 || typeId >= kinds.Length)
                return false;
            if ((decorations[typeId] & required) != 0)
                return true;
            if (kinds[typeId] is not (
                SpirvTypeKind.Pointer or SpirvTypeKind.Array or
                SpirvTypeKind.RuntimeArray or SpirvTypeKind.SampledImage))
                return false;
            typeId = referencedTypes[typeId];
        }
        return false;
    }

    private static uint EncodeDescriptorBinding(
        uint logicalBinding,
        DescriptorRegisterClass registerClass)
    {
        if (logicalBinding >= DescriptorRegisterClassStride)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Vulkan logical descriptor binding {logicalBinding} exceeds the " +
                $"per-register-class limit {DescriptorRegisterClassStride - 1}.");
        }
        return checked(logicalBinding + (uint)registerClass * DescriptorRegisterClassStride);
    }

    private static DescriptorRegisterClass DecodeDescriptorRegisterClass(uint binding)
    {
        uint value = binding / DescriptorRegisterClassStride;
        if (value > (uint)DescriptorRegisterClass.ConstantBuffer)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Vulkan descriptor binding {binding} is outside the canonical register-class ABI.");
        }
        return (DescriptorRegisterClass)value;
    }

    private static SpirvDecorationFlags ToDecorationFlag(uint decoration) => decoration switch
    {
        2 => SpirvDecorationFlags.Block,
        3 => SpirvDecorationFlags.BufferBlock,
        24 => SpirvDecorationFlags.NonWritable,
        25 => SpirvDecorationFlags.NonReadable,
        _ => SpirvDecorationFlags.None,
    };

    private static string DecodeSpirvString(ReadOnlySpan<uint> value)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(value);
        int terminator = bytes.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(
            terminator < 0 ? bytes : bytes[..terminator]);
    }

    private static GraphicsException InvalidSpirv(string message) => new(
        GraphicsError.ShaderCompilation,
        message);
}
