namespace SomeEngine.ECS;

public sealed partial class World
{
    [ThreadStatic] private static World? s_batchWorld;
    [ThreadStatic] private static ulong s_batchVersion;
    [ThreadStatic] private static bool s_batchNewVersion;
    private bool _usesBatchVersions;

    internal BatchVersionScope BeginBatchVersion(ulong version) => new(this, version);
    public BatchVersionScope OpenWriteBatch() => new(this, 0, newVersion: true);

    public readonly struct BatchVersionScope : IDisposable
    {
        private readonly World? _previous;
        private readonly ulong _version;
        private readonly bool _newVersion;
        private readonly bool _restore;
        internal BatchVersionScope(World world, ulong version, bool newVersion = false)
        {
            world._usesBatchVersions = true;
            _previous = s_batchWorld; _version = s_batchVersion; _newVersion = s_batchNewVersion;
            _restore = !ReferenceEquals(s_batchWorld, world) || version != 0 || newVersion;
            if (_restore) { s_batchWorld = world; s_batchVersion = version; s_batchNewVersion = newVersion; }
        }
        public void Dispose()
        { if (_restore) { s_batchWorld = _previous; s_batchVersion = _version; s_batchNewVersion = _newVersion; } }
    }

    /// <summary>
    /// The newest version whose writes are complete as seen from the calling thread. A consumer records this as what it
    /// has observed: inside a write batch or job whose version is already allocated, later writes still carry that
    /// version, so it is not complete yet.
    /// </summary>
    public ulong CompletedVersion
    {
        get
        {
            ulong completed = Volatile.Read(ref _version);
            if (ReferenceEquals(s_batchWorld, this) && s_batchVersion != 0) completed = Math.Min(completed, s_batchVersion - 1);
            if (Execution?.OpenWriteVersion is > 0 and var open) completed = Math.Min(completed, open - 1);
            return completed;
        }
    }

    private ulong BatchVersion()
    {
        if (!ReferenceEquals(s_batchWorld, this)) return NextVersionCore();
        if (s_batchVersion == 0) s_batchVersion = s_batchNewVersion ? AdvanceVersion() : NextVersionCore();
        return s_batchVersion;
    }
}
