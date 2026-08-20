namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class StateEqualityTests
{
    [Fact]
    public void BenchmarkDispatchStoresOnlyThePublicInterfaceReceiver()
    {
        Assert.True(typeof(InterfaceReceiverDispatch).IsValueType);
        Assert.Equal(
            typeof(IGraphicsBackend),
            typeof(InterfaceReceiverDispatch).GetField(
                "_receiver",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.FieldType);
    }

    [Fact]
    public void DirectSilkStateShadowUsesPublicNormalizedFloatEquality()
    {
        float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        float firstNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00001));
        float secondNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC01234));

        Assert.True(DirectSilkBenchmarkRunner.NormalizedFloatEquals(0.0f, negativeZero));
        Assert.True(DirectSilkBenchmarkRunner.NormalizedFloatEquals(firstNaN, secondNaN));
        Assert.False(DirectSilkBenchmarkRunner.NormalizedFloatEquals(1.0f, 2.0f));
    }
}
