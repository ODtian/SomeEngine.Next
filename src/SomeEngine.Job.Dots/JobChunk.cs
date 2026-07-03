namespace SomeEngine.Job;

public readonly struct JobChunk
{
    public JobChunk(int index, int start, int length)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Chunk index must be non-negative.");
        }

        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Chunk start must be non-negative.");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Chunk length must be non-negative.");
        }

        Index = index;
        Start = start;
        Length = length;
    }

    public int Index { get; }

    public int Start { get; }

    public int Length { get; }
}

