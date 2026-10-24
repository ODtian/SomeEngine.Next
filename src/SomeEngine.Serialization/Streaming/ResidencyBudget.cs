namespace SomeEngine.Serialization.Streaming;

public enum ResidencyClass : byte
{
    Compressed = 0,
    DecodedCpu = 1,
    UploadStaging = 2,
    Gpu = 3,
}

public sealed record ResidencyBudgets
{
    public long CompressedBytes { get; init; } = 256L * 1024 * 1024;
    public long DecodedCpuBytes { get; init; } = 512L * 1024 * 1024;
    public long UploadStagingBytes { get; init; } = 256L * 1024 * 1024;
    public long GpuBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public long For(ResidencyClass residencyClass) => residencyClass switch
    {
        ResidencyClass.Compressed => CompressedBytes,
        ResidencyClass.DecodedCpu => DecodedCpuBytes,
        ResidencyClass.UploadStaging => UploadStagingBytes,
        ResidencyClass.Gpu => GpuBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(residencyClass)),
    };
}

public sealed class ResidencyBudgetLedger
{
    private readonly object _budgetGate = new();
    private readonly long[] _budgets;
    private readonly long[] _used = new long[4];

    internal event Action? AvailabilityChanged;
    internal event Action<ResidencyClass, long>? ReclaimRequested;

    // Requests are made outside consumer locks. Each cache decides which of its own
    // contents can be released; the ledger remains the only budget accounting source.
    public void RequestReclaim(ResidencyClass residencyClass, long requiredBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (Available(residencyClass) < requiredBytes)
            ReclaimRequested?.Invoke(residencyClass, requiredBytes);
    }

    public ResidencyBudgetLedger(ResidencyBudgets? budgets = null)
    {
        ResidencyBudgets configured = budgets ?? new ResidencyBudgets();
        _budgets =
        [
            Validate(configured.CompressedBytes, nameof(configured.CompressedBytes)),
            Validate(configured.DecodedCpuBytes, nameof(configured.DecodedCpuBytes)),
            Validate(configured.UploadStagingBytes, nameof(configured.UploadStagingBytes)),
            Validate(configured.GpuBytes, nameof(configured.GpuBytes)),
        ];
    }

    public long Budget(ResidencyClass residencyClass) => _budgets[Index(residencyClass)];
    public long Used(ResidencyClass residencyClass) => Interlocked.Read(ref _used[Index(residencyClass)]);
    public long Available(ResidencyClass residencyClass) => Math.Max(0, Budget(residencyClass) - Used(residencyClass));

    public bool TryReserve(ResidencyClass residencyClass, long bytes, out ResidencyReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var prepared = new ResidencyReservation(residencyClass, bytes);
        int index = Index(residencyClass);
        if (!TryAdd(index, bytes))
        {
            reservation = null;
            return false;
        }

        prepared.Activate(this);
        reservation = prepared;
        return true;
    }

    internal bool TryReservePair(
        ResidencyClass firstClass,
        long firstBytes,
        ResidencyClass secondClass,
        long secondBytes,
        out ResidencyReservation? first,
        out ResidencyReservation? second)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(secondBytes);
        int firstIndex = Index(firstClass);
        int secondIndex = Index(secondClass);
        if (firstIndex == secondIndex)
            throw new ArgumentException("Paired residency reservations must use different classes.");

        var preparedFirst = new ResidencyReservation(firstClass, firstBytes);
        var preparedSecond = new ResidencyReservation(secondClass, secondBytes);

        lock (_budgetGate)
        {
            if (firstBytes > _budgets[firstIndex] - Interlocked.Read(ref _used[firstIndex])
                || secondBytes > _budgets[secondIndex] - Interlocked.Read(ref _used[secondIndex]))
            {
                first = null;
                second = null;
                return false;
            }
            Interlocked.Add(ref _used[firstIndex], firstBytes);
            Interlocked.Add(ref _used[secondIndex], secondBytes);
        }

        preparedFirst.Activate(this);
        preparedSecond.Activate(this);
        first = preparedFirst;
        second = preparedSecond;
        return true;
    }

    private bool TryAdd(int index, long bytes)
    {
        lock (_budgetGate)
        {
            long current = Interlocked.Read(ref _used[index]);
            if (bytes > _budgets[index] - current)
                return false;
            Interlocked.Add(ref _used[index], bytes);
            return true;
        }
    }

    public ResidencyReservation Reserve(ResidencyClass residencyClass, long bytes)
    {
        if (TryReserve(residencyClass, bytes, out ResidencyReservation? reservation))
            return reservation!;
        throw new InvalidOperationException(
            $"{residencyClass} residency budget exhausted: requested {bytes}, " +
            $"used {Used(residencyClass)}, budget {Budget(residencyClass)}.");
    }

    internal void Release(ResidencyClass residencyClass, long bytes)
    {
        long remaining = Interlocked.Add(ref _used[Index(residencyClass)], -bytes);
        if (remaining < 0)
            throw new InvalidOperationException($"{residencyClass} residency accounting became negative.");
        if (bytes != 0)
            NotifyAvailabilityChanged();
    }

    internal void NotifyAvailabilityChanged()
    {
        Action? observers = AvailabilityChanged;
        if (observers is null)
            return;

        try
        {
            foreach (Delegate observer in observers.GetInvocationList())
            {
                try { ((Action)observer)(); }
                catch
                {
                    // Availability is an observation after accounting has committed. A faulty
                    // observer must not turn a successful release into an apparent failure or
                    // prevent the remaining observers from being notified.
                }
            }
        }
        catch
        {
            // Notification bookkeeping is also best-effort; residency accounting is authoritative.
        }
    }

    private static long Validate(long value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name);
        return value;
    }

    private static int Index(ResidencyClass residencyClass) => residencyClass switch
    {
        ResidencyClass.Compressed => 0,
        ResidencyClass.DecodedCpu => 1,
        ResidencyClass.UploadStaging => 2,
        ResidencyClass.Gpu => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(residencyClass)),
    };
}

public sealed class ResidencyReservation : IDisposable
{
    private readonly object _gate = new();
    private ResidencyBudgetLedger? _ledger;
    private readonly ResidencyClass _residencyClass;
    private long _bytes;

    internal ResidencyReservation(
        ResidencyClass residencyClass,
        long bytes)
    {
        _residencyClass = residencyClass;
        _bytes = bytes;
    }

    public ResidencyClass ResidencyClass => _residencyClass;
    public long Bytes => Interlocked.Read(ref _bytes);

    // Moves already charged capacity to a final buffer without a release/reserve window.
    internal ResidencyReservation Take(long bytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ledger is null, this);
            if (bytes < 0 || bytes > _bytes) throw new ArgumentOutOfRangeException(nameof(bytes));
            var part = new ResidencyReservation(_residencyClass, bytes);
            part.Activate(_ledger);
            Interlocked.Add(ref _bytes, -bytes);
            return part;
        }
    }

    internal void Activate(ResidencyBudgetLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (Interlocked.CompareExchange(ref _ledger, ledger, null) is not null)
            throw new InvalidOperationException("Residency reservation is already active.");
    }

    public void Dispose()
    {
        ResidencyBudgetLedger? ledger;
        long bytes;
        lock (_gate)
        {
            ledger = _ledger;
            _ledger = null;
            bytes = _bytes;
        }
        ledger?.Release(_residencyClass, bytes);
    }
}
