using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal interface ICommandScenario : IDisposable
{
    void Cycle();
    void Grow();
    bool Check();
    bool CheckProtocol();
}

internal interface ICommandComparison<TEntity, THandle> : IDisposable where TEntity : struct where THandle : struct
{
    void Clear();
    THandle Create(int producer, int value);
    void Set(int producer, THandle entity, int value);
    void SetExisting(int producer, TEntity entity, int value);
    void AddExtra(int producer, THandle entity, int value);
    void RemoveExtra(int producer, THandle entity);
    void DestroyDeferred(int producer, THandle entity);
    void Playback();
    TEntity Resolve(THandle handle);
    bool Alive(TEntity entity);
    int Read(TEntity entity);
    bool HasExtra(TEntity entity);
    void Destroy(TEntity entity);
}

internal sealed class CommandScenario<TEntity, THandle, TApi> : ICommandScenario
    where TEntity : struct where THandle : struct where TApi : struct, ICommandComparison<TEntity, THandle>
{
    private readonly TApi _api;
    private readonly TEntity[] _entities;
    private readonly int _producers;
    private readonly THandle[] _kept = new THandle[125], _removed = new THandle[125];
    private THandle[] _growing = [];
    private readonly Action<int> _record;
    private readonly ParallelOptions _parallel = new() { MaxDegreeOfParallelism = 4 };
    private bool _valid = true;

    internal CommandScenario(TApi api, TEntity[] entities, int producers)
    {
        _api = api; _entities = entities; _producers = producers;
        _record = Record;
    }

    private void Record(int producer)
    {
        int first = producer * _kept.Length / _producers, end = (producer + 1) * _kept.Length / _producers;
        for (int i = first; i < end; i++)
        {
            THandle kept = _kept[i] = _api.Create(producer, i);
            _api.Set(producer, kept, i + 1);
            _api.AddExtra(producer, kept, i + 2);
            _api.RemoveExtra(producer, kept);
            _api.SetExisting(producer, _entities[i], i + 3);
            THandle removed = _removed[i] = _api.Create(producer, i + 4);
            _api.Set(producer, removed, i + 5);
            _api.DestroyDeferred(producer, removed);
        }
    }

    public void Cycle()
    {
        RecordCommands();
        _api.Playback();
        CompleteCommands();
    }

    internal void RecordCommands()
    {
        _api.Clear();
        if (_producers == 1) Record(0); else Parallel.For(0, _producers, _parallel, _record);
    }

    internal void PlaybackCommands() => _api.Playback();
    internal void ClearCommands() => _api.Clear();

    internal void CompleteCommands()
    {
        bool valid = true;
        for (int i = 0; i < _kept.Length; i++)
        {
            TEntity kept = _api.Resolve(_kept[i]), removed = _api.Resolve(_removed[i]);
            valid &= _api.Alive(kept) && !_api.Alive(removed) && !_api.HasExtra(kept)
                && _api.Read(kept) == i + 1 && _api.Read(_entities[i]) == i + 3;
        }
        _api.Clear();
        for (int i = 0; i < _kept.Length; i++)
        {
            TEntity kept = _api.Resolve(_kept[i]);
            valid &= _api.Alive(kept);
            _api.Destroy(kept);
        }
        _valid &= valid;
    }

    public void Grow()
    {
        if (_growing.Length == 0) _growing = new THandle[_entities.Length];
        _api.Clear();
        for (int i = 0; i < _growing.Length; i++) _growing[i] = _api.Create(i % _producers, i + 1);
        _api.Playback();
        for (int i = 0; i < _growing.Length; i++) _valid &= _api.Read(_api.Resolve(_growing[i])) == i + 1;
        _api.Clear();
        foreach (THandle handle in _growing) _api.Destroy(_api.Resolve(handle));
    }

    public bool CheckProtocol()
    {
        _api.Clear();
        THandle old = _api.Create(0, 1);
        _api.DestroyDeferred(0, old);
        _api.Playback();
        TEntity expired = _api.Resolve(old);
        _api.Clear();
        THandle prefix = _api.Create(0, 2);
        _api.SetExisting(0, expired, 3);
        _api.SetExisting(0, _entities[0], 4);
        int before = _api.Read(_entities[0]);
        bool faulted = false;
        try { _api.Playback(); } catch (Exception) { faulted = true; }
        TEntity created = _api.Resolve(prefix);
        bool valid = faulted && _api.Read(created) == 2 && _api.Read(_entities[0]) == before;
        _api.Clear();
        try { _api.Set(0, prefix, 5); valid = false; } catch (ArgumentException) { }
        _api.Destroy(created);
        _api.Clear();
        THandle retained = _api.Create(0, 6);
        _api.Playback();
        _api.Dispose();
        created = _api.Resolve(retained);
        valid &= _api.Read(created) == 6;
        _api.Destroy(created);
        return valid;
    }

    public bool Check() => _valid;
    public void Dispose() => _api.Dispose();
}
