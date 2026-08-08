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
    private readonly long[] _budgets;
    private readonly long[] _used = new long[4];

    internal event Action? AvailabilityReleased;

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

        if (!TryAdd(firstIndex, firstBytes))
        {
            first = null;
            second = null;
            return false;
        }
        if (!TryAdd(secondIndex, secondBytes))
        {
            RollBack(firstIndex, firstBytes);
            first = null;
            second = null;
            return false;
        }

        preparedFirst.Activate(this);
        preparedSecond.Activate(this);
        first = preparedFirst;
        second = preparedSecond;
        return true;
    }

    private bool TryAdd(int index, long bytes)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _used[index]);
            long next;
            try
            {
                next = checked(current + bytes);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (next > _budgets[index])
                return false;

            if (Interlocked.CompareExchange(ref _used[index], next, current) == current)
                return true;
        }
    }

    private void RollBack(int index, long bytes)
    {
        long remaining = Interlocked.Add(ref _used[index], -bytes);
        if (remaining < 0)
            throw new InvalidOperationException("Residency accounting became negative during reservation rollback.");
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
            NotifyAvailabilityReleased();
    }

    private void NotifyAvailabilityReleased()
    {
        Action? observers = AvailabilityReleased;
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
    private ResidencyBudgetLedger? _ledger;
    private readonly ResidencyClass _residencyClass;
    private readonly long _bytes;

    internal ResidencyReservation(
        ResidencyClass residencyClass,
        long bytes)
    {
        _residencyClass = residencyClass;
        _bytes = bytes;
    }

    public ResidencyClass ResidencyClass => _residencyClass;
    public long Bytes => _bytes;

    internal void Activate(ResidencyBudgetLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (Interlocked.CompareExchange(ref _ledger, ledger, null) is not null)
            throw new InvalidOperationException("Residency reservation is already active.");
    }

    public void Dispose()
        => Interlocked.Exchange(ref _ledger, null)?.Release(_residencyClass, _bytes);
}
