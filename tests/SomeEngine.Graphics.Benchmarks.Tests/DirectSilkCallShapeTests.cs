using System.Reflection;
using System.Reflection.Emit;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class DirectSilkCallShapeTests
{
    [Fact]
    public void Direct_command_list_helpers_use_Silk_methods_and_never_emit_calli()
    {
        MethodInfo[] methods = typeof(DirectD3D12FastCalls).GetMethods(
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotEmpty(methods);
        foreach (MethodInfo method in methods)
        {
            Instruction[] instructions = Decode(method);
            Assert.DoesNotContain(
                instructions,
                static instruction => instruction.OpCode == OpCodes.Calli);
        }

        MethodInfo draw = methods.Single(static method => method.Name == "DrawInstanced");
        Assert.Contains(
            Decode(draw),
            static instruction =>
                instruction.Method?.DeclaringType == typeof(ID3D12GraphicsCommandList) &&
                instruction.Method.Name == nameof(ID3D12GraphicsCommandList.DrawInstanced));
    }

    private static Instruction[] Decode(MethodInfo method)
    {
        byte[] bytes = method.GetMethodBody()?.GetILAsByteArray() ??
            throw new InvalidOperationException("The direct Silk helper has no managed method body.");
        var result = new List<Instruction>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            OpCode opCode = ReadOpCode(bytes, ref offset);
            MethodBase? calledMethod = null;
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(bytes, offset);
                calledMethod = method.Module.ResolveMethod(token);
            }
            offset = checked(offset + OperandSize(opCode.OperandType, bytes, offset));
            result.Add(new Instruction(opCode, calledMethod));
        }
        return [.. result];
    }

    private static OpCode ReadOpCode(byte[] bytes, ref int offset)
    {
        byte first = bytes[offset++];
        if (first != 0xFE)
            return OneByteOpCodes[first];
        return TwoByteOpCodes[bytes[offset++]];
    }

    private static int OperandSize(OperandType type, byte[] bytes, int offset) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineI or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or
        OperandType.InlineField or
        OperandType.InlineI or
        OperandType.InlineMethod or
        OperandType.InlineSig or
        OperandType.InlineString or
        OperandType.InlineTok or
        OperandType.InlineType or
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => checked(4 + BitConverter.ToInt32(bytes, offset) * 4),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown IL operand type."),
    };

    private static readonly OpCode[] OneByteOpCodes = CreateOpCodeTable(twoByte: false);
    private static readonly OpCode[] TwoByteOpCodes = CreateOpCodeTable(twoByte: true);

    private static OpCode[] CreateOpCodeTable(bool twoByte)
    {
        var result = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
                continue;
            ushort value = unchecked((ushort)opCode.Value);
            if ((value > byte.MaxValue) != twoByte)
                continue;
            result[value & byte.MaxValue] = opCode;
        }
        return result;
    }

    private readonly record struct Instruction(OpCode OpCode, MethodBase? Method);
}
