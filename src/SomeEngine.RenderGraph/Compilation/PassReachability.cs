namespace SomeEngine.RenderGraph;

/// <summary>Transitive happens-before over live pass dependencies plus native same-queue FIFO.</summary>
internal sealed class PassReachability
{
    private readonly int[] _positions;
    private readonly bool[][] _ancestors;

    public PassReachability(int[] activePassOrdinals, int[][] dependencies, QueueType[] queues)
    {
        ActivePassOrdinals = activePassOrdinals;
        _positions = Enumerable.Repeat(-1, dependencies.Length).ToArray();
        for (int index = 0; index < activePassOrdinals.Length; index++) _positions[activePassOrdinals[index]] = index;
        _ancestors = Enumerable.Range(0, activePassOrdinals.Length)
            .Select(_ => new bool[activePassOrdinals.Length])
            .ToArray();
        int[] lastOnQueue = Enumerable.Repeat(-1, Enum.GetValues<QueueType>().Length).ToArray();
        for (int current = 0; current < activePassOrdinals.Length; current++)
        {
            int pass = activePassOrdinals[current];
            SortedSet<int> predecessors = new(dependencies[pass]);
            int queuePredecessor = lastOnQueue[(int)queues[pass]];
            if (queuePredecessor >= 0) predecessors.Add(queuePredecessor);
            foreach (int predecessorPass in predecessors)
            {
                int predecessor = Position(predecessorPass);
                if (predecessor < 0 || predecessor >= current)
                    throw new InvalidOperationException("Pass order contains a non-topological dependency.");
                _ancestors[current][predecessor] = true;
                for (int ancestor = 0; ancestor < predecessor; ancestor++)
                    if (_ancestors[predecessor][ancestor]) _ancestors[current][ancestor] = true;
            }
            lastOnQueue[(int)queues[pass]] = pass;
        }
    }

    public int[] ActivePassOrdinals { get; }

    public bool Before(int leftPass, int rightPass)
    {
        int left = Position(leftPass);
        int right = Position(rightPass);
        return left >= 0 && right >= 0 && _ancestors[right][left];
    }

    public int CompareUses(int[] leftUses, int[] rightUses)
    {
        if (AllBefore(leftUses, rightUses)) return -1;
        if (AllBefore(rightUses, leftUses)) return 1;
        return 0;
    }

    public int[] StartFrontier(int[] uses) => uses
        .Where(candidate => !uses.Any(other => other != candidate && Before(other, candidate)))
        .Order()
        .ToArray();

    public int[] EndFrontier(int[] uses) => uses
        .Where(candidate => !uses.Any(other => other != candidate && Before(candidate, other)))
        .Order()
        .ToArray();

    private bool AllBefore(int[] leftUses, int[] rightUses) =>
        leftUses.Length != 0 && rightUses.Length != 0 &&
        leftUses.All(left => rightUses.All(right => Before(left, right)));

    private int Position(int pass) => (uint)pass < (uint)_positions.Length ? _positions[pass] : -1;
}
