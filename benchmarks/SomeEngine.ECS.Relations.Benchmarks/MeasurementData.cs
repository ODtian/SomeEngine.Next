using System.Numerics;
using SomeEngine.ECS.Benchmarks.Relations.Bitmap;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Relations.Benchmarks;

public sealed class MeasurementData : IDisposable
{
    private readonly Body[] _initialBodies;
    private readonly int[] _characterNumbers;
    private readonly bool[] _hasBody;

    public MeasurementData(MeasurementCase input)
    {
        Input = input;
        Jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = input.WorkerCount, SafetyMode = JobSafetyMode.Fast });
        World = WorldJobs.CreateWorld(input.EntityCount + input.CharacterCount + 32, Jobs);
        Characters = new Entity[input.CharacterCount];
        for (int index = 0; index < Characters.Length; index++)
            Characters[index] = World.CreateEntity();
        Entities = new Entity[input.EntityCount];
        _initialBodies = new Body[Entities.Length];
        _characterNumbers = new int[Entities.Length];
        _hasBody = new bool[Entities.Length];
        for (int index = 0; index < Entities.Length; index++)
        {
            int motion = index * 100 / Entities.Length;
            MotionKind kind = motion < input.DynamicPercent ? MotionKind.Dynamic :
                motion < input.DynamicPercent + input.StaticPercent ? MotionKind.Static : MotionKind.Kinematic;
            Vector3 min = Position(input, index);
            var body = new Body { Kind = kind, Bounds = new(min, min + Vector3.One), CollisionCategories = Bits(input, index) };
            _initialBodies[index] = body;
            _hasBody[index] = true;
            _characterNumbers[index] = input.CharacterLayout == "clustered"
                ? index / input.HairPerCharacter : index % Characters.Length;
            Entities[index] = World.CreateEntity(body);
            if (index < Entities.Length * input.HairPercent / 100)
                World.Add(Entities[index], new Hair { Character = Characters[_characterNumbers[index]] });
            if (input.Class == "finite")
                World.Add(Entities[index], new CollisionLayer
                {
                    Categories = (CollisionCategory)body.CollisionCategories.Categories,
                    AllowedCategories = (CollisionCategory)body.CollisionCategories.AllowedCategories
                });
        }
        Members = World.Query(World.QueryDefinition().All<Body>().None<ExcludedBody>());
        int pairCapacity = input.HasSpace ? input.EntityCount * 64 :
            input.Class == "finite" ? input.CandidateCount : input.EntityCount * (input.EntityCount - 1) / 2;
        Output = input.Class == "threads" ? [] : new (Entity, Entity)[pairCapacity];
        ContactCounts = input.Class == "threads" ? [] : new int[input.EntityCount + input.CharacterCount + 32];
        Depths = new float[ContactCounts.Length];
        Candidates = input.Class == "finite" ? MakeCandidates(input) : [];
    }

    public MeasurementCase Input { get; }
    public Scheduler Jobs { get; }
    public World World { get; }
    public QueryHandle Members { get; }
    public Entity[] Entities { get; }
    public Entity[] Characters { get; }
    public (Entity, Entity)[] Output { get; }
    public (Entity, Entity)[] Candidates { get; }
    public int[] ContactCounts { get; }
    public float[] Depths { get; }

    public void WriteFrame(int frame)
    {
        World.AcquireSystemVersion();
        int phase = frame % 8;
        int positions = Entities.Length / 10;
        int masks = Entities.Length / 50;
        int keys = Entities.Length / 50;
        for (int index = 0; index < positions; index++)
        {
            Body value = _initialBodies[index];
            Vector3 move = new((phase - 3) * 0.025f, 0, 0);
            value.Bounds = new(value.Bounds.Min + move, value.Bounds.Max + move);
            if (index < masks)
            {
                value.CollisionCategories = Bits(Input, index + phase);
                value.Kind = (MotionKind)((index + phase) % 3);
            }
            World.Replace(Entities[index], value);
        }
        for (int index = 0; index < keys; index++)
            World.Replace(Entities[index], new Hair { Character = Characters[(_characterNumbers[index] + phase) % Characters.Length] });
        int members = Entities.Length / 100;
        for (int changed = 0; changed < members; changed++)
        {
            int index = Entities.Length - members + changed;
            Entity entity = Entities[index];
            if (changed % 3 == 0)
            {
                if (phase % 2 == 0)
                    World.AddTag<ExcludedBody>(entity);
                else
                    World.RemoveTag<ExcludedBody>(entity);
            }
            else if (changed % 3 == 1)
            {
                if (_hasBody[index])
                    World.Remove<Body>(entity);
                else
                    World.Add(entity, _initialBodies[index]);
                _hasBody[index] = !_hasBody[index];
            }
            else
            {
                World.DestroyEntity(entity);
                Entities[index] = World.CreateEntity(_initialBodies[index]);
            }
        }
    }

    public static CategoryBits Bits(MeasurementCase input, int index)
    {
        int shift = (index * 3 + 1) % 8;
        ulong mask = (1UL << input.MaskPopulation) - 1;
        ulong allowed = ((mask << shift) | (mask >> (8 - shift))) & 255;
        return new CategoryBits(1UL << (index % 8), allowed);
    }

    private static Vector3 Position(MeasurementCase input, int index)
    {
        return input.Density switch
        {
            "sparse" => new Vector3(index / 2 * 3, index % 2 * 0.2f, 0),
            "uniform" => new Vector3(index % 32, index / 32 % 32, index / 1024) * 0.8f,
            _ => new Vector3(index / 64 * 4, index % 4 * 0.03f, index / 4 % 16 * 0.03f)
        };
    }

    private (Entity, Entity)[] MakeCandidates(MeasurementCase input)
    {
        var pairs = new (Entity, Entity)[input.CandidateCount];
        uint random = (uint)input.Seed;
        for (int index = 0; index < pairs.Length; index++)
        {
            random = unchecked(random * 1664525 + 1013904223);
            int first = (int)((random >> 8) % Entities.Length);
            random = unchecked(random * 1664525 + 1013904223);
            int second = (int)((random >> 8) % Entities.Length);
            pairs[index] = (Entities[first], Entities[second]);
        }
        return pairs;
    }

    public void Dispose()
    {
        World.ReleaseQuery(Members);
        World.Dispose();
        Jobs.Dispose();
    }
}
