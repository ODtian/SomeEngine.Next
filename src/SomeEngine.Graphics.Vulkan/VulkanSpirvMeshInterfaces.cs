using System.Runtime.InteropServices;
using System.Text;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    // Slang currently drops vk::perprimitive on fragment parameters (#7019).
    // Repair only the selected mesh/fragment interface, using the actual mesh output
    // rate. Flat interpolation alone does not identify per-primitive attributes.
    internal static byte[] MatchSpirvMeshFragmentInputs(
        byte[] meshCode, string meshEntry, byte[] fragmentCode, string fragmentEntry)
    {
        MeshInterfaceModule mesh = MeshInterfaceModule.Parse(meshCode, meshEntry, 5365);
        if (!mesh.HasPerPrimitive) return fragmentCode;
        MeshInterfaceSite[] outputs = mesh.Sites(3, stripMeshArray: true);
        var primitiveOutputs = new Dictionary<(uint Location, uint Component), MeshInterfaceSite>();
        foreach (MeshInterfaceSite output in outputs)
            if (output.PerPrimitive && !primitiveOutputs.TryAdd((output.Location, output.Component), output))
                throw InvalidSpirv("Mesh outputs overlap at a per-primitive interface location.");
        if (primitiveOutputs.Count == 0) return fragmentCode;

        MeshInterfaceModule fragment = MeshInterfaceModule.Parse(fragmentCode, fragmentEntry, 4);
        var additions = new List<MeshInterfaceSite>();
        foreach (MeshInterfaceSite input in fragment.Sites(1, stripMeshArray: false))
        {
            if (input.PerPrimitive || !primitiveOutputs.TryGetValue((input.Location, input.Component), out var output))
                continue;
            if (input.Type != output.Type)
                throw InvalidSpirv($"Cannot repair mesh/fragment per-primitive location {input.Location}, component {input.Component}: unsupported unequal interface types.");
            additions.Add(input);
        }
        if (additions.Count == 0) return fragmentCode;
        return AddMeshPrimitiveDecorations(fragmentCode, additions);
    }

    private readonly record struct MeshInterfaceSite(
        uint Target, int Member, uint Location, uint Component, bool PerPrimitive, string Type);

    private sealed class MeshInterfaceDecoration
    {
        internal uint? Location;
        internal uint Component;
        internal bool BuiltIn, Block, PerPrimitive;
    }

    private sealed class MeshInterfaceModule
    {
        private readonly Dictionary<uint, (uint Opcode, uint[] Operands)> _types = [];
        private readonly Dictionary<uint, (uint Type, uint Storage)> _variables = [];
        private readonly Dictionary<uint, ulong> _constants = [];
        private readonly Dictionary<(uint Target, int Member), MeshInterfaceDecoration> _decorations = [];
        private readonly HashSet<uint> _interfaces = [];
        private bool _decorationGroups;
        internal bool HasPerPrimitive => _decorations.Values.Any(value => value.PerPrimitive);

        private MeshInterfaceDecoration Decoration(uint target, int member = -1) =>
            _decorations.GetValueOrDefault((target, member)) ?? new MeshInterfaceDecoration();

        internal static MeshInterfaceModule Parse(byte[] code, string entryName, uint executionModel)
        {
            if (code.Length < 20 || (code.Length & 3) != 0)
                throw InvalidSpirv("Mesh interface SPIR-V is truncated or not word aligned.");
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
            if (words[0] != 0x07230203) throw InvalidSpirv("Mesh interface SPIR-V has invalid magic.");
            var module = new MeshInterfaceModule();
            bool foundEntry = false;
            for (int index = 5; index < words.Length;)
            {
                int count = checked((int)(words[index] >> 16));
                uint opcode = words[index] & 0xffff;
                if (count == 0 || count > words.Length - index)
                    throw InvalidSpirv("Mesh interface SPIR-V has a malformed instruction.");
                ReadOnlySpan<uint> operands = words.Slice(index + 1, count - 1);
                if (opcode == 15 && count >= 4)
                {
                    int nameWords = SpirvInterfaceStringWords(operands[2..]);
                    if (operands[0] == executionModel && DecodeSpirvString(operands.Slice(2, nameWords)) == entryName)
                    {
                        if (foundEntry) throw InvalidSpirv("Mesh interface has duplicate entry names.");
                        foundEntry = true;
                        foreach (uint id in operands[(2 + nameWords)..]) module._interfaces.Add(id);
                    }
                }
                else if (opcode is >= 19 and <= 39 && count >= 2)
                    module._types[operands[0]] = (opcode, operands[1..].ToArray());
                else if (opcode == 43 && count is 4 or 5)
                    module._constants[operands[1]] = operands[2] | (count == 5 ? (ulong)operands[3] << 32 : 0ul);
                else if (opcode == 59 && count >= 4)
                    module._variables[operands[1]] = (operands[0], operands[2]);
                else if (opcode is 71 or 72 && count >= (opcode == 71 ? 3 : 4))
                {
                    int member = opcode == 72 ? checked((int)operands[1]) : -1;
                    int decorationIndex = opcode == 72 ? 2 : 1;
                    var key = (operands[0], member);
                    if (!module._decorations.TryGetValue(key, out MeshInterfaceDecoration? decoration))
                        module._decorations.Add(key, decoration = new());
                    uint value = operands[decorationIndex];
                    if (value is 30 or 31)
                    {
                        if (operands.Length <= decorationIndex + 1) throw InvalidSpirv("Mesh interface decoration has no value.");
                        if (value == 30) decoration.Location = operands[decorationIndex + 1];
                        else decoration.Component = operands[decorationIndex + 1];
                    }
                    else if (value == 11) decoration.BuiltIn = true;
                    else if (value == 2) decoration.Block = true;
                    else if (value == 5271) decoration.PerPrimitive = true;
                }
                else if (opcode is 73 or 74 or 75) module._decorationGroups = true;
                index += count;
            }
            if (!foundEntry) throw InvalidSpirv($"No matching mesh interface entry '{entryName}'.");
            return module;
        }

        internal MeshInterfaceSite[] Sites(uint storage, bool stripMeshArray)
        {
            if (_decorationGroups)
                throw InvalidSpirv("Mesh/fragment interface repair does not support decoration groups.");
            var sites = new List<MeshInterfaceSite>();
            foreach (uint id in _interfaces)
            {
                if (!_variables.TryGetValue(id, out var variable) || variable.Storage != storage) continue;
                var pointer = RequireType(variable.Type);
                if (pointer.Opcode != 32 || pointer.Operands.Length != 2)
                    throw InvalidSpirv("Mesh interface variable does not have a pointer type.");
                uint typeId = pointer.Operands[1];
                var type = RequireType(typeId);
                if (stripMeshArray)
                {
                    if (type.Opcode != 28 || type.Operands.Length != 2)
                        throw InvalidSpirv("Mesh output interface is not an outer array.");
                    typeId = type.Operands[0];
                    type = RequireType(typeId);
                }
                MeshInterfaceDecoration variableDecoration = Decoration(id);
                MeshInterfaceDecoration typeDecoration = Decoration(typeId);
                if (variableDecoration.BuiltIn) continue;
                bool primitive = variableDecoration.PerPrimitive || typeDecoration.PerPrimitive;
                if (type.Opcode == 30 && typeDecoration.Block)
                {
                    if (variableDecoration.Location is not null)
                        throw InvalidSpirv("Mesh interface repair does not support a block-level Location.");
                    for (int member = 0; member < type.Operands.Length; member++)
                    {
                        MeshInterfaceDecoration decoration = Decoration(typeId, member);
                        if (decoration.BuiltIn) continue;
                        if (decoration.Location is not uint location)
                            throw InvalidSpirv("Mesh interface block member has no explicit Location.");
                        sites.Add(new(typeId, member, location, decoration.Component,
                            primitive || decoration.PerPrimitive, TypeSignature(type.Operands[member], 0)));
                    }
                }
                else
                {
                    if (variableDecoration.Location is not uint location)
                        throw InvalidSpirv("Mesh interface variable has neither BuiltIn nor Location.");
                    sites.Add(new(id, -1, location, variableDecoration.Component,
                        primitive, TypeSignature(typeId, 0)));
                }
            }
            return sites.ToArray();
        }

        private (uint Opcode, uint[] Operands) RequireType(uint id) =>
            _types.TryGetValue(id, out var value) ? value : throw InvalidSpirv($"Mesh interface references unknown type {id}.");

        private string TypeSignature(uint id, int depth)
        {
            if (depth > 32) throw InvalidSpirv("Mesh interface type nesting is excessive.");
            var type = RequireType(id);
            uint[] operands = type.Operands;
            return type.Opcode switch
            {
                21 when operands.Length == 2 => $"i{operands[0]}:{operands[1]}",
                22 when operands.Length == 1 => $"f{operands[0]}",
                23 or 24 when operands.Length == 2 => $"{type.Opcode}:{operands[1]}:{TypeSignature(operands[0], depth + 1)}",
                28 when operands.Length == 2 && _constants.TryGetValue(operands[1], out ulong length) =>
                    $"a{length}:{TypeSignature(operands[0], depth + 1)}",
                30 => "s{" + string.Join(";", operands.Select(value => TypeSignature(value, depth + 1))) + "}",
                _ => throw InvalidSpirv($"Unsupported mesh/fragment interface type opcode {type.Opcode}."),
            };
        }
    }

    private static int SpirvInterfaceStringWords(ReadOnlySpan<uint> words)
    {
        for (int index = 0; index < words.Length; index++)
        {
            uint value = words[index];
            if ((value & 0xff) == 0 || (value & 0xff00) == 0 ||
                (value & 0xff0000) == 0 || (value & 0xff000000) == 0) return index + 1;
        }
        throw InvalidSpirv("Mesh interface entry name is not terminated.");
    }

    private static byte[] AddMeshPrimitiveDecorations(byte[] code, List<MeshInterfaceSite> additions)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
        bool capability = false, extension = false;
        int capabilityEnd = 5, extensionEnd = 5, annotationsEnd = -1;
        for (int index = 5; index < words.Length;)
        {
            int count = checked((int)(words[index] >> 16));
            uint opcode = words[index] & 0xffff;
            if (opcode == 17)
            {
                capability |= count == 2 && words[index + 1] == 5283;
                capabilityEnd = index + count;
                extensionEnd = capabilityEnd;
            }
            else if (opcode == 10)
            {
                extension |= DecodeSpirvString(words.Slice(index + 1, count - 1)) == "SPV_EXT_mesh_shader";
                extensionEnd = index + count;
            }
            if (annotationsEnd < 0 && opcode is >= 19 and <= 39) annotationsEnd = index;
            index += count;
        }
        if (annotationsEnd < 0) throw InvalidSpirv("Mesh fragment interface has no type section.");
        uint[] extensionWords = MemoryMarshal.Cast<byte, uint>(Encoding.UTF8.GetBytes("SPV_EXT_mesh_shader\0")).ToArray();
        var result = new List<uint>(words.Length + additions.Count * 4 + 8);
        for (int index = 0; index <= words.Length; index++)
        {
            if (index == capabilityEnd && !capability) { result.Add((2u << 16) | 17u); result.Add(5283); }
            if (index == extensionEnd && !extension)
            {
                result.Add((checked((uint)extensionWords.Length + 1) << 16) | 10u);
                result.AddRange(extensionWords);
            }
            if (index == annotationsEnd)
                foreach (MeshInterfaceSite addition in additions)
                {
                    result.Add((addition.Member < 0 ? 3u : 4u) << 16 | (addition.Member < 0 ? 71u : 72u));
                    result.Add(addition.Target);
                    if (addition.Member >= 0) result.Add(checked((uint)addition.Member));
                    result.Add(5271);
                }
            if (index < words.Length) result.Add(words[index]);
        }
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(result)).ToArray();
    }
}
