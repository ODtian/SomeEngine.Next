namespace SomeEngine.Elements;

/// <summary>Item indices from Start up to End, including the viewport buffer rows.</summary>
public readonly record struct VisibleRange(int Start, int End)
{
    public int Count => End - Start;
    public Enumerator GetEnumerator() => new(Start, End);

    public struct Enumerator
    {
        private int _next;
        private readonly int _end;

        internal Enumerator(int start, int end)
        {
            _next = start;
            _end = end;
            Current = default;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            if (_next >= _end)
                return false;
            Current = _next++;
            return true;
        }
    }
}
