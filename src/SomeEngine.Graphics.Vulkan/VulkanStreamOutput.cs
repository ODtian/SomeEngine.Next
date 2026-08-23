using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private sealed class VulkanStreamOutputPlan(
        VulkanStreamOutputCapture[] captures,
        uint maximumOutputLocations)
    {
        internal VulkanStreamOutputCapture[] Captures { get; } = captures;
        internal uint MaximumOutputLocations { get; } = maximumOutputLocations;

        internal static VulkanStreamOutputPlan Create(
            VulkanDevice device,
            EntryPointReflection vertex,
            in StreamOutputState state)
        {
            VulkanExtendedFeatureSupport support = device.ExtendedFeatures;
            ValidateDescription(support, state);
            uint[] offsets = new uint[state.BufferStrides.Length];
            bool[] used = new bool[state.BufferStrides.Length];
            var captures = new List<VulkanStreamOutputCapture>();
            foreach (ref readonly StreamOutputElement element in state.Elements)
                AddElement(vertex, state, element, offsets, used, captures);
            ValidateStrides(support, state, offsets, used);
            return new VulkanStreamOutputPlan(
                captures.ToArray(),
                device.MaxVertexOutputComponents / 4);
        }

        private static void ValidateDescription(
            VulkanExtendedFeatureSupport support,
            in StreamOutputState state)
        {
            if (!support.TransformFeedback)
                throw new NotSupportedException(
                    "Graphics stream output requires VK_EXT_transform_feedback.");
            if (state.BufferStrides.Length == 0 ||
                state.BufferStrides.Length > support.MaximumTransformFeedbackBuffers)
                throw new ArgumentOutOfRangeException(nameof(state));
            if (state.RasterizedStreamIndex is uint rasterized && rasterized != 0)
            {
                throw new NotSupportedException(
                    "The core GraphicsPipelineDesc has no geometry stage and can only rasterize stream zero.");
            }
        }

        private static void AddElement(
            EntryPointReflection vertex,
            in StreamOutputState state,
            in StreamOutputElement element,
            uint[] offsets,
            bool[] used,
            List<VulkanStreamOutputCapture> captures)
        {
            if (element.Stream != 0 || element.ComponentCount == 0 ||
                element.ComponentCount > 4 ||
                element.StartComponent + element.ComponentCount > 4 ||
                element.OutputSlot >= state.BufferStrides.Length)
            {
                throw new ArgumentException(
                    "A Vulkan stream-output element is invalid.",
                    nameof(state));
            }
            uint byteCount = checked((uint)element.ComponentCount * sizeof(uint));
            uint outputOffset = offsets[element.OutputSlot];
            offsets[element.OutputSlot] = checked(outputOffset + byteCount);
            used[element.OutputSlot] = true;
            if (element.IsGap)
                return;
            if (element.Variable == VariableLayoutReflection.Null ||
                !ContainsLayout(vertex.ResultVarLayout, element.Variable))
            {
                throw new ArgumentException(
                    "A stream-output variable is not an output of the vertex entry point.",
                    nameof(state));
            }
            int builtIn = ResolveBuiltIn(element.Variable.SemanticName);
            uint location = builtIn < 0
                ? ResolveLocation(element.Variable, nameof(state))
                : 0;
            captures.Add(new VulkanStreamOutputCapture(
                location,
                builtIn,
                element.StartComponent,
                element.ComponentCount,
                element.OutputSlot,
                outputOffset,
                state.BufferStrides[element.OutputSlot]));
        }

        private static uint ResolveLocation(
            VariableLayoutReflection variable,
            string parameterName)
        {
            nuint reflectedLocation = variable.GetOffset(
                SlangParameterCategory.VaryingOutput);
            if (reflectedLocation == Slang.UnknownSize ||
                reflectedLocation == Slang.UnboundedSize ||
                reflectedLocation > uint.MaxValue)
            {
                throw new ArgumentException(
                    "A stream-output variable has no concrete varying location.",
                    parameterName);
            }
            return checked((uint)reflectedLocation);
        }

        private static void ValidateStrides(
            VulkanExtendedFeatureSupport support,
            in StreamOutputState state,
            uint[] offsets,
            bool[] used)
        {
            uint streamDataSize = 0;
            for (int slot = 0; slot < state.BufferStrides.Length; slot++)
            {
                uint stride = state.BufferStrides[slot];
                if ((stride & 3) != 0 ||
                    stride > support.MaximumTransformFeedbackBufferDataStride ||
                    used[slot] && (stride == 0 || offsets[slot] > stride) ||
                    offsets[slot] > support.MaximumTransformFeedbackBufferDataSize)
                    throw new ArgumentOutOfRangeException(nameof(state));
                if (used[slot])
                    streamDataSize = checked(streamDataSize + offsets[slot]);
            }
            if (streamDataSize > support.MaximumTransformFeedbackStreamDataSize)
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        private static bool ContainsLayout(
            VariableLayoutReflection root,
            VariableLayoutReflection candidate)
        {
            if (root == candidate)
                return true;
            TypeLayoutReflection type = root.TypeLayout;
            for (uint index = 0; index < type.FieldCount; index++)
                if (ContainsLayout(type.GetFieldByIndex(index), candidate))
                    return true;
            return false;
        }

        private static int ResolveBuiltIn(string semantic) =>
            semantic.ToUpperInvariant() switch
            {
                "SV_POSITION" => 0,
                "PSIZE" or "SV_POINTSIZE" => 1,
                "SV_CLIPDISTANCE" => 3,
                "SV_CULLDISTANCE" => 4,
                "SV_RENDERTARGETARRAYINDEX" => 7,
                "SV_VIEWPORTARRAYINDEX" => 10,
                _ => -1,
            };
    }

    private readonly record struct VulkanStreamOutputCapture(
        uint Location,
        int BuiltIn,
        byte StartComponent,
        byte ComponentCount,
        byte Buffer,
        uint Offset,
        uint Stride);

    private static byte[] ApplySpirvStreamOutput(
        ReadOnlySpan<byte> code,
        string entryName,
        VulkanStreamOutputPlan plan)
    {
        if (plan.Captures.Length == 0)
            return code.ToArray();
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
        SpirvStreamOutputFacts facts = SpirvStreamOutputFacts.Parse(words, entryName);
        var builder = new SpirvStreamOutputBuilder(
            words,
            facts,
            plan.Captures,
            plan.MaximumOutputLocations);
        return MemoryMarshal.AsBytes(builder.Build().AsSpan()).ToArray();
    }

    private sealed class SpirvStreamOutputFacts
    {
        private readonly Dictionary<uint, uint> _locations = [];
        private readonly Dictionary<uint, uint> _builtIns = [];
        private readonly Dictionary<uint, SpirvVariableType> _variables = [];
        private readonly Dictionary<uint, SpirvPointerType> _pointers = [];
        private readonly Dictionary<uint, SpirvVectorType> _vectors = [];
        private readonly Dictionary<uint, uint> _scalarWidths = [];

        private SpirvStreamOutputFacts() { }

        internal uint Bound { get; private set; }
        internal uint EntryPoint { get; private set; }
        internal int EntryPointInstruction { get; private set; }
        internal int CapabilityInsertion { get; private set; }
        internal int ExecutionModeInsertion { get; private set; }
        internal int AnnotationInsertion { get; private set; }
        internal int GlobalInsertion { get; private set; }
        internal uint NextLocation { get; private set; }
        internal bool HasTransformFeedbackCapability { get; private set; }
        internal bool HasXfbExecutionMode { get; private set; }
        internal IReadOnlyDictionary<uint, SpirvPointerType> Pointers => _pointers;
        internal IReadOnlyDictionary<uint, SpirvVectorType> Vectors => _vectors;

        internal static SpirvStreamOutputFacts Parse(
            ReadOnlySpan<uint> words,
            string entryName)
        {
            var result = new SpirvStreamOutputFacts
            {
                Bound = words[3],
                EntryPointInstruction = -1,
                CapabilityInsertion = 5,
                ExecutionModeInsertion = 5,
                AnnotationInsertion = -1,
                GlobalInsertion = -1,
            };
            uint maximumLocation = 0;
            bool hasLocation = false;
            for (int index = 5; index < words.Length;)
            {
                uint instruction = words[index];
                int wordCount = checked((int)(instruction >> 16));
                uint opcode = instruction & 0xffff;
                if (wordCount <= 0 || index > words.Length - wordCount)
                    throw InvalidSpirv("SPIR-V stream-output instrumentation found a malformed instruction.");
                switch (opcode)
                {
                    case 15 when wordCount >= 4:
                    {
                        string name = DecodeEntryPointName(words.Slice(index, wordCount));
                        if (string.Equals(name, entryName, StringComparison.Ordinal))
                        {
                            result.EntryPoint = words[index + 2];
                            result.EntryPointInstruction = index;
                        }
                        result.ExecutionModeInsertion = index + wordCount;
                        break;
                    }
                    case 16 when wordCount >= 3:
                        if (words[index + 1] == result.EntryPoint && words[index + 2] == 11)
                            result.HasXfbExecutionMode = true;
                        result.ExecutionModeInsertion = index + wordCount;
                        break;
                    case 17 when wordCount >= 2:
                        if (words[index + 1] == 53)
                            result.HasTransformFeedbackCapability = true;
                        result.CapabilityInsertion = index + wordCount;
                        break;
                    case 21 when wordCount >= 4:
                        result._scalarWidths[words[index + 1]] = words[index + 2];
                        break;
                    case 22 when wordCount >= 3:
                        result._scalarWidths[words[index + 1]] = words[index + 2];
                        break;
                    case 23 when wordCount >= 4:
                        result._vectors[words[index + 1]] = new SpirvVectorType(
                            words[index + 2],
                            words[index + 3]);
                        break;
                    case 32 when wordCount >= 4:
                        result._pointers[words[index + 1]] = new SpirvPointerType(
                            words[index + 2],
                            words[index + 3]);
                        break;
                    case 54:
                        result.GlobalInsertion = result.GlobalInsertion < 0
                            ? index
                            : result.GlobalInsertion;
                        break;
                    case 59 when wordCount >= 4:
                        result._variables[words[index + 2]] = new SpirvVariableType(
                            words[index + 1],
                            words[index + 3]);
                        break;
                    case 71 when wordCount >= 4:
                    {
                        uint target = words[index + 1];
                        uint decoration = words[index + 2];
                        if (decoration == 30)
                        {
                            uint location = words[index + 3];
                            result._locations[target] = location;
                            maximumLocation = Math.Max(maximumLocation, location);
                            hasLocation = true;
                        }
                        else if (decoration == 11)
                        {
                            result._builtIns[target] = words[index + 3];
                        }
                        break;
                    }
                }
                if (result.AnnotationInsertion < 0 && opcode is >= 19 and <= 39)
                    result.AnnotationInsertion = index;
                index += wordCount;
            }
            if (result.EntryPoint == 0 || result.EntryPointInstruction < 0)
                throw InvalidSpirv($"SPIR-V has no entry point named '{entryName}'.");
            if (result.AnnotationInsertion < 0 || result.GlobalInsertion < 0)
                throw InvalidSpirv("SPIR-V has no type or function section.");
            result.NextLocation = hasLocation ? checked(maximumLocation + 1) : 0;
            return result;
        }

        internal SpirvCaptureSource Resolve(VulkanStreamOutputCapture capture)
        {
            uint[] candidates = capture.BuiltIn >= 0
                ? _builtIns.Where(pair => pair.Value == checked((uint)capture.BuiltIn))
                    .Select(static pair => pair.Key)
                    .ToArray()
                : _locations.Where(pair => pair.Value == capture.Location)
                    .Select(static pair => pair.Key)
                    .ToArray();
            candidates = candidates.Where(id =>
                    _variables.TryGetValue(id, out SpirvVariableType variable) &&
                    variable.StorageClass == 3)
                .ToArray();
            if (candidates.Length != 1)
                throw InvalidSpirv("A stream-output semantic did not resolve to one SPIR-V output variable.");
            uint variableId = candidates[0];
            SpirvVariableType variableType = _variables[variableId];
            if (!_pointers.TryGetValue(variableType.PointerType, out SpirvPointerType pointer) ||
                pointer.StorageClass != 3)
                throw InvalidSpirv("A stream-output variable has no Output pointer type.");
            uint componentType;
            uint componentCount;
            if (_vectors.TryGetValue(pointer.ValueType, out SpirvVectorType vector))
            {
                componentType = vector.ComponentType;
                componentCount = vector.ComponentCount;
            }
            else
            {
                componentType = pointer.ValueType;
                componentCount = 1;
            }
            if (!_scalarWidths.TryGetValue(componentType, out uint width) || width != 32 ||
                capture.StartComponent + capture.ComponentCount > componentCount)
            {
                throw new NotSupportedException(
                    "Vulkan stream output currently requires 32-bit scalar or vector semantics.");
            }
            return new SpirvCaptureSource(
                variableId,
                pointer.ValueType,
                componentType,
                componentCount);
        }

        internal uint FindVectorType(uint componentType, uint count) =>
            _vectors.Where(pair =>
                    pair.Value.ComponentType == componentType &&
                    pair.Value.ComponentCount == count)
                .Select(static pair => pair.Key)
                .FirstOrDefault();

        internal uint FindOutputPointer(uint valueType) =>
            _pointers.Where(pair =>
                    pair.Value.StorageClass == 3 &&
                    pair.Value.ValueType == valueType)
                .Select(static pair => pair.Key)
                .FirstOrDefault();

        private static string DecodeEntryPointName(ReadOnlySpan<uint> instruction)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(instruction[3..]);
            int terminator = bytes.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(
                terminator < 0 ? bytes : bytes[..terminator]);
        }
    }

    private sealed class SpirvStreamOutputBuilder
    {
        private readonly ReadOnlyMemory<uint> _source;
        private readonly SpirvStreamOutputFacts _facts;
        private readonly List<uint> _typeAndVariables = [];
        private readonly List<uint> _decorations = [];
        private readonly Dictionary<(uint Component, uint Count), uint> _captureTypes = [];
        private readonly Dictionary<uint, uint> _outputPointers = [];
        private readonly SpirvCaptureBuild[] _captures;
        private uint _bound;

        internal SpirvStreamOutputBuilder(
            ReadOnlySpan<uint> source,
            SpirvStreamOutputFacts facts,
            ReadOnlySpan<VulkanStreamOutputCapture> captures,
            uint maximumOutputLocations)
        {
            _source = source.ToArray();
            _facts = facts;
            _bound = facts.Bound;
            _captures = new SpirvCaptureBuild[captures.Length];
            uint nextLocation = facts.NextLocation;
            if (nextLocation > maximumOutputLocations ||
                captures.Length > maximumOutputLocations - nextLocation)
            {
                throw new NotSupportedException(
                    "The Vulkan stream-output instrumentation exceeds the vertex output-location limit.");
            }
            for (int index = 0; index < captures.Length; index++)
            {
                VulkanStreamOutputCapture capture = captures[index];
                SpirvCaptureSource sourceValue = facts.Resolve(capture);
                uint captureType = ResolveCaptureType(sourceValue, capture.ComponentCount);
                uint pointerType = ResolveOutputPointer(captureType);
                uint variable = AllocateId();
                AddInstruction(_typeAndVariables, 59, pointerType, variable, 3);
                AddDecoration(variable, 30, nextLocation++);
                AddDecoration(variable, 35, capture.Offset);
                AddDecoration(variable, 36, capture.Buffer);
                AddDecoration(variable, 37, capture.Stride);
                _captures[index] = new SpirvCaptureBuild(
                    sourceValue,
                    captureType,
                    variable,
                    capture.StartComponent,
                    capture.ComponentCount);
            }
        }

        internal uint[] Build()
        {
            ReadOnlySpan<uint> source = _source.Span;
            var result = new List<uint>(checked(source.Length + 64 * _captures.Length));
            result.AddRange(source[..5].ToArray());
            bool inEntryFunction = false;
            for (int index = 5; index < source.Length;)
            {
                InjectSections(result, index);
                uint instruction = source[index];
                int wordCount = checked((int)(instruction >> 16));
                uint opcode = instruction & 0xffff;
                if (opcode == 54 && wordCount >= 3)
                    inEntryFunction = source[index + 2] == _facts.EntryPoint;
                if (inEntryFunction && opcode == 253)
                    WriteCaptureStores(result);
                if (index == _facts.EntryPointInstruction)
                    WriteEntryPoint(result, source.Slice(index, wordCount));
                else
                    result.AddRange(source.Slice(index, wordCount).ToArray());
                if (opcode == 56)
                    inEntryFunction = false;
                index += wordCount;
            }
            InjectSections(result, source.Length);
            result[3] = _bound;
            return result.ToArray();
        }

        private uint ResolveCaptureType(
            in SpirvCaptureSource source,
            uint componentCount)
        {
            if (componentCount == 1)
                return source.ComponentType;
            if (_captureTypes.TryGetValue(
                    (source.ComponentType, componentCount),
                    out uint cached))
                return cached;
            uint existing = _facts.FindVectorType(source.ComponentType, componentCount);
            if (existing != 0)
            {
                _captureTypes[(source.ComponentType, componentCount)] = existing;
                return existing;
            }
            uint created = AllocateId();
            AddInstruction(
                _typeAndVariables,
                23,
                created,
                source.ComponentType,
                componentCount);
            _captureTypes[(source.ComponentType, componentCount)] = created;
            return created;
        }

        private uint ResolveOutputPointer(uint captureType)
        {
            if (_outputPointers.TryGetValue(captureType, out uint cached))
                return cached;
            uint existing = _facts.FindOutputPointer(captureType);
            if (existing != 0)
            {
                _outputPointers[captureType] = existing;
                return existing;
            }
            uint created = AllocateId();
            AddInstruction(_typeAndVariables, 32, created, 3, captureType);
            _outputPointers[captureType] = created;
            return created;
        }

        private void AddDecoration(uint target, uint decoration, uint value) =>
            AddInstruction(_decorations, 71, target, decoration, value);

        private void InjectSections(List<uint> result, int sourceIndex)
        {
            if (sourceIndex == _facts.CapabilityInsertion &&
                !_facts.HasTransformFeedbackCapability)
                AddInstruction(result, 17, 53);
            if (sourceIndex == _facts.ExecutionModeInsertion &&
                !_facts.HasXfbExecutionMode)
                AddInstruction(result, 16, _facts.EntryPoint, 11);
            if (sourceIndex == _facts.AnnotationInsertion)
                result.AddRange(_decorations);
            if (sourceIndex == _facts.GlobalInsertion)
                result.AddRange(_typeAndVariables);
        }

        private void WriteEntryPoint(List<uint> result, ReadOnlySpan<uint> instruction)
        {
            int originalCount = instruction.Length;
            int count = checked(originalCount + _captures.Length);
            result.Add(checked((uint)(count << 16)) | 15u);
            result.AddRange(instruction[1..].ToArray());
            foreach (ref readonly SpirvCaptureBuild capture in _captures.AsSpan())
                result.Add(capture.Variable);
        }

        private void WriteCaptureStores(List<uint> result)
        {
            foreach (ref readonly SpirvCaptureBuild capture in _captures.AsSpan())
            {
                uint loaded = AllocateId();
                AddInstruction(
                    result,
                    61,
                    capture.Source.ValueType,
                    loaded,
                    capture.Source.Variable);
                uint value = loaded;
                bool completeVector = capture.StartComponent == 0 &&
                    capture.ComponentCount == capture.Source.ComponentCount;
                if (!completeVector)
                {
                    value = AllocateId();
                    if (capture.ComponentCount == 1)
                    {
                        AddInstruction(
                            result,
                            81,
                            capture.CaptureType,
                            value,
                            loaded,
                            capture.StartComponent);
                    }
                    else
                    {
                        uint[] operands = new uint[checked(4 + capture.ComponentCount)];
                        operands[0] = capture.CaptureType;
                        operands[1] = value;
                        operands[2] = loaded;
                        operands[3] = loaded;
                        for (int index = 0; index < capture.ComponentCount; index++)
                            operands[index + 4] = checked((uint)(capture.StartComponent + index));
                        AddInstruction(result, 79, operands);
                    }
                }
                AddInstruction(result, 62, capture.Variable, value);
            }
        }

        private uint AllocateId() => _bound++;

        private static void AddInstruction(
            List<uint> destination,
            uint opcode,
            params uint[] operands)
        {
            destination.Add(checked((uint)((operands.Length + 1) << 16)) | opcode);
            destination.AddRange(operands);
        }
    }

    private readonly record struct SpirvVariableType(
        uint PointerType,
        uint StorageClass);

    private readonly record struct SpirvPointerType(
        uint StorageClass,
        uint ValueType);

    private readonly record struct SpirvVectorType(
        uint ComponentType,
        uint ComponentCount);

    private readonly record struct SpirvCaptureSource(
        uint Variable,
        uint ValueType,
        uint ComponentType,
        uint ComponentCount);

    private readonly record struct SpirvCaptureBuild(
        SpirvCaptureSource Source,
        uint CaptureType,
        uint Variable,
        byte StartComponent,
        byte ComponentCount);
}
