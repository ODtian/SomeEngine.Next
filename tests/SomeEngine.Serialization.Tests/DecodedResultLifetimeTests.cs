using System.Buffers;

namespace SomeEngine.Serialization.Tests;

public sealed class DecodedResultLifetimeTests
{
    [Fact]
    public void RejectedTextDisposesTheConstructedObject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSerializer.Deserialize<CheckedDecodedValue>("{\"Number\":-1}"));
        Assert.True(CheckedDecodedValue.Last!.IsDisposed);
        Assert.Throws<System.Text.Json.JsonException>(() => TextSerializer.Deserialize<CheckedDecodedValue>("{\"Number\":3,\"Unknown\":0}"));
        Assert.True(CheckedDecodedValue.Last!.IsDisposed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedDecodedFieldDisposesTheConstructedObject(bool sequence)
    {
        byte[] invalid = [255, 255, 255, 255];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (sequence) BinarySerializer.Deserialize<CheckedDecodedValue>(new ReadOnlySequence<byte>(invalid));
            else BinarySerializer.Deserialize<CheckedDecodedValue>(invalid.AsSpan());
        });
        Assert.True(CheckedDecodedValue.Last!.IsDisposed);
        using var value = BinarySerializer.Deserialize<CheckedDecodedValue>(new byte[] { 3, 0, 0, 0 }.AsSpan());
        Assert.Equal(3, value.Number);
        Assert.False(value.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconsumedInputDisposesTheResultThatCannotBeReturned(bool sequence)
    {
        byte[] bytes = [1, 0, 0, 0, 9];
        Assert.Throws<InvalidDataException>(() =>
        {
            if (sequence) BinarySerializer.Deserialize<DisposableDecodedValue>(new ReadOnlySequence<byte>(bytes));
            else BinarySerializer.Deserialize<DisposableDecodedValue>(bytes.AsSpan());
        });
        Assert.True(DisposableDecodedValue.Last!.IsDisposed);
        using var result = BinarySerializer.Deserialize<DisposableDecodedValue>(bytes.AsSpan(0, 4));
        Assert.False(result.IsDisposed);
        Assert.Equal(1, result.Number);
    }
}

[BinarySerializable]
public sealed partial class DisposableDecodedValue : IDisposable
{
    public DisposableDecodedValue() => Last = this;
    public static DisposableDecodedValue? Last { get; private set; }
    public int Number { get; set; }
    [BinaryIgnore] public bool IsDisposed { get; private set; }
    public void Dispose() => IsDisposed = true;
}

[BinarySerializable, TextSerializable]
public sealed partial class CheckedDecodedValue : IDisposable
{
    private int _number;
    public CheckedDecodedValue() => Last = this;
    public static CheckedDecodedValue? Last { get; private set; }
    public int Number
    {
        get => _number;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); _number = value; }
    }
    [BinaryIgnore] public bool IsDisposed { get; private set; }
    public void Dispose() => IsDisposed = true;
}
