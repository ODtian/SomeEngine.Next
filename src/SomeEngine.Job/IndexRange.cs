namespace SomeEngine.Job;

public readonly struct IndexRange
{
    private const string NegativeStartMessage = "Range start must be non-negative.";
    private const string NonPositiveLengthMessage = "Range length must be positive.";
    private const string EndOverflowMessage = "Range end must not overflow.";

    public IndexRange(long start, long length)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), NegativeStartMessage);
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), NonPositiveLengthMessage);
        }

        if (start > long.MaxValue - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), EndOverflowMessage);
        }

        Start = start;
        Length = length;
    }

    public long Start { get; }

    public long Length { get; }
}
