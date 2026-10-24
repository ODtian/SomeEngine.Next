namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class ViewportEqualityTests
{

    [Fact]
    public void ViewportsCompareSignedZeroAndNanValuesByValue()
    {
        float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        float firstNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00001));
        float secondNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC01234));

        Assert.True(new Viewport(0.0f, 0, 1, 1).Equals(new Viewport(negativeZero, 0, 1, 1)));
        Assert.True(new Viewport(firstNaN, 0, 1, 1).Equals(new Viewport(secondNaN, 0, 1, 1)));
        Assert.False(new Viewport(1, 0, 1, 1).Equals(new Viewport(2, 0, 1, 1)));
    }
}
