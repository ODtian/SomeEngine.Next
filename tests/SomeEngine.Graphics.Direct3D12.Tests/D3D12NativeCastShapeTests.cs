using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Silk.NET.Direct3D12;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class D3D12ObjectConversionTests
{
    [Fact]
    public void Representative_resource_conversion_always_performs_a_runtime_type_check()
    {
        Type castType = typeof(D3D12Backend).GetNestedType(
            "RequireD3D12",
            BindingFlags.NonPublic) ?? throw new MissingMemberException("D3D12Backend.RequireD3D12");
        MethodInfo method = castType.GetMethod(
            "Buffer",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(Buffer)],
            modifiers: null) ?? throw new MissingMethodException("D3D12Backend.RequireD3D12.Buffer");
        Instruction[] instructions = Decode(method);

        Assert.Contains(
            instructions,
            static instruction =>
                instruction.OpCode == OpCodes.Isinst ||
                instruction.OpCode == OpCodes.Castclass);
        Assert.DoesNotContain(instructions, static instruction => IsUnsafeAs(instruction.Method));
    }

    [Fact]
    public void Command_list_helpers_use_Silk_methods_and_never_emit_calli()
    {
        Type calls = typeof(D3D12Backend).Assembly.GetType(
            "SomeEngine.Graphics.Direct3D12.D3D12CommandListFastCalls",
            throwOnError: true)!;
        MethodInfo[] methods = calls.GetMethods(
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
                instruction.Method?.DeclaringType == typeof(ID3D12GraphicsCommandList10) &&
                instruction.Method.Name == nameof(ID3D12GraphicsCommandList10.DrawInstanced));
    }

    private static bool IsUnsafeAs(MethodBase? method) =>
        method?.DeclaringType == typeof(Unsafe) &&
        string.Equals(method.Name, nameof(Unsafe.As), StringComparison.Ordinal);

    private static Instruction[] Decode(MethodInfo method)
    {
        byte[] bytes = method.GetMethodBody()?.GetILAsByteArray() ??
            throw new InvalidOperationException("The native conversion has no managed method body.");
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
