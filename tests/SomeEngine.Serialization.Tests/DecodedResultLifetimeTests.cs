using System.Buffers;
using System.Text.Json;

namespace SomeEngine.Serialization.Tests;

public sealed class DecodedResultLifetimeTests
{
    [Fact]
    public void RejectedTextFieldDisposesTheConstructedObject()
    {
        AggregateException failure = Assert.Throws<AggregateException>(() =>
            TextSerializer.Deserialize<CheckedDecodedValue>("{\"Number\":-1}"));
        Assert.IsType<ArgumentOutOfRangeException>(failure.InnerExceptions[0]);
        var cleanup = Assert.IsType<DecodedCleanupException>(failure.InnerExceptions[1]);
        Assert.True(cleanup.Value.IsDisposed);
    }

    [Fact]
    public void UnknownTextFieldDisposesTheConstructedObject()
    {
        AggregateException failure = Assert.Throws<AggregateException>(() =>
            TextSerializer.Deserialize<CheckedDecodedValue>("{\"Number\":3,\"Unknown\":0}"));
        Assert.IsType<JsonException>(failure.InnerExceptions[0]);
        var cleanup = Assert.IsType<DecodedCleanupException>(failure.InnerExceptions[1]);
        Assert.True(cleanup.Value.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedDecodedFieldDisposesTheConstructedObject(bool sequence)
    {
        byte[] invalid = [255, 255, 255, 255];
        AggregateException failure = Assert.Throws<AggregateException>(() =>
        {
            if (sequence) BinarySerializer.Deserialize<CheckedDecodedValue>(new ReadOnlySequence<byte>(invalid));
            else BinarySerializer.Deserialize<CheckedDecodedValue>(invalid.AsSpan());
        });
        Assert.IsType<ArgumentOutOfRangeException>(failure.InnerExceptions[0]);
        var cleanup = Assert.IsType<DecodedCleanupException>(failure.InnerExceptions[1]);
        Assert.True(cleanup.Value.IsDisposed);

        using var value = BinarySerializer.Deserialize<CheckedDecodedValue>(new byte[] { 3, 0, 0, 0 }.AsSpan());
        value.ReportCleanup = false;
        Assert.Equal(3, value.Number);
        Assert.False(value.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconsumedInputDisposesTheResultThatCannotBeReturned(bool sequence)
    {
        byte[] bytes = [1, 0, 0, 0, 9];
        AggregateException failure = Assert.Throws<AggregateException>(() =>
        {
            if (sequence) BinarySerializer.Deserialize<DisposableDecodedValue>(new ReadOnlySequence<byte>(bytes));
            else BinarySerializer.Deserialize<DisposableDecodedValue>(bytes.AsSpan());
        });
        Assert.IsType<InvalidDataException>(failure.InnerExceptions[0]);
        var cleanup = Assert.IsType<DecodedCleanupException>(failure.InnerExceptions[1]);
        Assert.True(cleanup.Value.IsDisposed);

        using var result = BinarySerializer.Deserialize<DisposableDecodedValue>(bytes.AsSpan(0, 4));
        result.ReportCleanup = false;
        Assert.False(result.IsDisposed);
        Assert.Equal(1, result.Number);
    }
}

public interface IDecodedValue
{
    bool IsDisposed { get; }
}

public sealed class DecodedCleanupException(IDecodedValue value) : Exception
{
    public IDecodedValue Value { get; } = value;
}

[BinarySerializable]
public sealed partial class DisposableDecodedValue : IDisposable, IDecodedValue
{
    public int Number { get; set; }
    [BinaryIgnore] public bool IsDisposed { get; private set; }
    [BinaryIgnore] public bool ReportCleanup { get; set; } = true;
    public void Dispose()
    {
        IsDisposed = true;
        if (ReportCleanup)
            throw new DecodedCleanupException(this);
    }
}

[BinarySerializable, TextSerializable]
public sealed partial class CheckedDecodedValue : IDisposable, IDecodedValue
{
    private int _number;
    public int Number
    {
        get => _number;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); _number = value; }
    }
    [BinaryIgnore] public bool IsDisposed { get; private set; }
    [BinaryIgnore] public bool ReportCleanup { get; set; } = true;
    public void Dispose()
    {
        IsDisposed = true;
        if (ReportCleanup)
            throw new DecodedCleanupException(this);
    }
}
