using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Bound instances grouped so each parent finishes before its children's own matrices.</summary>
internal sealed class SkeletonLevels
{
    private readonly List<SkeletonInstance> _current = [];
    private SkeletonInstance[] _targets = [];
    private (SkeletonInstance Instance, ulong Version)[] _bindings = [];
    private SkeletonLevel[] _levels = [];
    private Scheduler? _scheduler;

    internal void Add(SkeletonInstance skeleton) => _current.Add(skeleton);

    internal void Compose(Scheduler scheduler, double time)
    {
        bool changed = !ReferenceEquals(_scheduler, scheduler) || _current.Count != _targets.Length;
        if (!changed)
            for (int index = 0; index < _targets.Length; index++)
                changed |= !ReferenceEquals(_current[index], _targets[index]);
        foreach (var binding in _bindings) changed |= binding.Version != binding.Instance.BindingVersion;
        if (changed) Bind(scheduler);
        foreach (SkeletonLevel level in _levels)
            scheduler.ExecuteParallel(new ComposeModels(level.Instances, time), level.Instances.Length, 1,
                level.Accesses);
        _current.Clear();
    }

    private void Bind(Scheduler scheduler)
    {
        _targets = _current.ToArray();
        _scheduler = scheduler;
        var depths = new Dictionary<SkeletonInstance, int>();
        foreach (SkeletonInstance skeleton in _targets) Depth(skeleton);
        int levelCount = depths.Count == 0 ? 0 : depths.Values.Max() + 1;
        var groups = new List<SkeletonInstance>[levelCount];
        for (int level = 0; level < groups.Length; level++) groups[level] = [];
        foreach (var entry in depths) groups[entry.Value].Add(entry.Key);
        _levels = groups.Select(instances => new SkeletonLevel(scheduler, instances.ToArray())).ToArray();
        _bindings = depths.Keys.Select(static instance => (instance, instance.BindingVersion)).ToArray();

        int Depth(SkeletonInstance skeleton)
        {
            if (depths.TryGetValue(skeleton, out int depth)) return depth;
            depth = skeleton.ParentInstance is { } parent ? Depth(parent) + 1 : 0;
            depths.Add(skeleton, depth);
            return depth;
        }
    }

    internal void Clear()
    {
        _current.Clear();
        _targets = [];
        _bindings = [];
        _levels = [];
        _scheduler = null;
    }

    private sealed class SkeletonLevel
    {
        internal readonly SkeletonInstance[] Instances;
        internal readonly JobResourceAccess[] Accesses;

        internal SkeletonLevel(Scheduler scheduler, SkeletonInstance[] instances)
        {
            Instances = instances;
            var parents = new HashSet<SkeletonInstance>();
            foreach (SkeletonInstance instance in instances)
                if (instance.ParentInstance is { } parent) parents.Add(parent);
            Accesses = new JobResourceAccess[instances.Length + parents.Count];
            int next = 0;
            foreach (SkeletonInstance instance in instances)
                Accesses[next++] = JobResourceAccess.Write(scheduler.GetResourceToken(instance));
            foreach (SkeletonInstance parent in parents)
                Accesses[next++] = JobResourceAccess.Read(scheduler.GetResourceToken(parent));
        }
    }

    private readonly struct ComposeModels(SkeletonInstance[] instances, double time) : IJobParallelFor
    {
        public void Execute(int index) => instances[index].ComposeOwn(time);
    }
}
