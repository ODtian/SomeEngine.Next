using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.ECS.Fuzz.Tests;

internal enum FuzzStepMode
{
    Immediate,
    CommandBuffer,
}

internal enum FuzzCommandKind
{
    CreateEntity,
    DestroyEntity,
    AddAlpha,
    ReplaceAlpha,
    RemoveAlpha,
    AddBeta,
    ReplaceBeta,
    RemoveBeta,
    AddTag,
    RemoveTag,
    AddEnableable,
    ReplaceEnableable,
    RemoveEnableable,
    Enable,
    Disable,
    AddSparse,
    ReplaceSparse,
    RemoveSparse,
    AddShared,
    ReplaceShared,
    RemoveShared,
    AddIndexed,
    ReplaceIndexed,
    RemoveIndexed,
    AddBuffer,
    AppendBuffer,
    SetBufferFirst,
    RemoveBuffer,
    SetParent,
    Detach,
    CreateRelation,
    DestroyRelation,
}

internal readonly record struct FuzzCommand(
    FuzzCommandKind Kind,
    int EntityId,
    int Value = 0,
    int OtherEntityId = 0);

internal sealed record FuzzStep(FuzzStepMode Mode, FuzzCommand[] Commands);

internal sealed record EcsFuzzTrace(
    int SchemaVersion,
    string PrngAlgorithm,
    ulong Seed,
    FuzzStep[] Steps)
{
    internal const int CurrentSchemaVersion = 2;
    internal const string FixedPrngAlgorithm = "xorshift64star-v1";

    internal static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    internal static EcsFuzzTrace FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<EcsFuzzTrace>(json, JsonOptions)
            ?? throw new InvalidDataException("The ECS fuzz trace JSON contained no trace.");
    }

    internal static EcsFuzzTrace Create(ulong seed, params FuzzStep[] steps) =>
        new(CurrentSchemaVersion, FixedPrngAlgorithm, seed, steps);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>
/// Repository-owned PRNG with a frozen bit-level algorithm. It deliberately avoids System.Random
/// so a serialized seed keeps producing the same command trace across runtime releases.
/// </summary>
internal struct FixedPrng
{
    private const ulong ZeroSeedSubstitute = 0x9E3779B97F4A7C15UL;
    private const ulong OutputMultiplier = 2685821657736338717UL;
    private ulong _state;

    internal FixedPrng(ulong seed)
    {
        _state = seed == 0 ? ZeroSeedSubstitute : seed;
    }

    internal ulong NextUInt64()
    {
        ulong value = _state;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        _state = value;
        return value * OutputMultiplier;
    }

    internal int NextInt(int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveUpperBound));
        return (int)(NextUInt64() % (uint)exclusiveUpperBound);
    }

    internal bool Percent(int percentage)
    {
        if ((uint)percentage > 100u)
            throw new ArgumentOutOfRangeException(nameof(percentage));
        return NextInt(100) < percentage;
    }
}

internal static class EcsFuzzTraceGenerator
{
    internal const int MaximumLogicalEntities = 1_024;

    internal static EcsFuzzTrace Generate(ulong seed, int stepCount)
    {
        if (stepCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepCount));

        var generator = new StreamingGenerator(seed);
        var model = new ReferenceWorld();
        var steps = new FuzzStep[stepCount];

        for (int index = 0; index < steps.Length; index++)
        {
            FuzzStep step = generator.Next(index, model);
            steps[index] = step;
            ApplyIfSuccessful(model, step);
        }

        return EcsFuzzTrace.Create(seed, steps);
    }

    internal sealed class StreamingGenerator
    {
        private FixedPrng _random;
        private int _nextEntityId = 1;

        internal StreamingGenerator(ulong seed)
        {
            _random = new FixedPrng(seed);
        }

        internal FuzzStep Next(int index, ReferenceWorld model)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (index == 0 || model.AliveCount == 0)
            {
                if (_nextEntityId > MaximumLogicalEntities)
                {
                    throw new InvalidOperationException(
                        "The bounded fuzz model exhausted all logical entity identities.");
                }
                return Immediate(new FuzzCommand(
                    FuzzCommandKind.CreateEntity,
                    _nextEntityId++));
            }
            if (index % 31 == 30)
                return GenerateFailingBatch(model, ref _random, ref _nextEntityId);
            if (_random.Percent(40))
            {
                return _random.Percent(24)
                    ? GenerateFailingBatch(model, ref _random, ref _nextEntityId)
                    : GenerateSuccessfulBatch(model, ref _random, ref _nextEntityId);
            }
            return GenerateImmediate(model, ref _random, ref _nextEntityId);
        }
    }

    private static FuzzStep GenerateImmediate(
        ReferenceWorld model,
        ref FixedPrng random,
        ref int nextEntityId)
    {
        int[] dead = model.EligibleIds(static entity => !entity.Alive);
        if (dead.Length != 0 && random.Percent(14))
        {
            int target = dead[random.NextInt(dead.Length)];
            FuzzCommandKind kind = random.NextInt(4) switch
            {
                0 => FuzzCommandKind.DestroyEntity,
                1 => FuzzCommandKind.AddAlpha,
                2 => FuzzCommandKind.ReplaceBeta,
                _ => FuzzCommandKind.RemoveTag,
            };
            return Immediate(new FuzzCommand(kind, target, NextValue(ref random)));
        }

        return Immediate(GenerateValidCommand(model, ref random, ref nextEntityId));
    }

    private static FuzzStep GenerateSuccessfulBatch(
        ReferenceWorld model,
        ref FixedPrng random,
        ref int nextEntityId)
    {
        using ReferenceWorld.Transaction preview = model.BeginTransaction();
        int count = 2 + random.NextInt(7);
        var commands = new FuzzCommand[count];
        for (int index = 0; index < commands.Length; index++)
        {
            FuzzCommand command = GenerateValidBatchCommand(model, ref random, ref nextEntityId);
            model.Apply(command);
            commands[index] = command;
        }
        return new FuzzStep(FuzzStepMode.CommandBuffer, commands);
    }

    private static FuzzStep GenerateFailingBatch(
        ReferenceWorld model,
        ref FixedPrng random,
        ref int nextEntityId)
    {
        using ReferenceWorld.Transaction preview = model.BeginTransaction();
        var commands = new List<FuzzCommand>();
        int prefixCount = random.NextInt(4);
        for (int index = 0; index < prefixCount; index++)
        {
            FuzzCommand prefix = GenerateValidBatchCommand(model, ref random, ref nextEntityId);
            model.Apply(prefix);
            commands.Add(prefix);
        }

        int[] live = model.EligibleIds(static entity => entity.Alive);
        if (live.Length == 0)
        {
            var create = new FuzzCommand(FuzzCommandKind.CreateEntity, nextEntityId++);
            model.Apply(create);
            commands.Add(create);
            live = model.EligibleIds(static entity => entity.Alive);
        }

        int staleTarget = live[random.NextInt(live.Length)];
        var destroy = new FuzzCommand(FuzzCommandKind.DestroyEntity, staleTarget);
        model.Apply(destroy);
        commands.Add(destroy);
        commands.Add(new FuzzCommand(
            FuzzCommandKind.ReplaceAlpha,
            staleTarget,
            NextValue(ref random)));
        return new FuzzStep(FuzzStepMode.CommandBuffer, commands.ToArray());
    }

    private static FuzzCommand GenerateValidCommand(
        ReferenceWorld model,
        ref FixedPrng random,
        ref int nextEntityId)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            switch (random.NextInt(24))
            {
                case 0:
                    if (TryPick(model, static value => value.Alive && value.Enableable is null, ref random, out int addEnableable))
                        return new FuzzCommand(FuzzCommandKind.AddEnableable, addEnableable, NextValue(ref random));
                    break;
                case 1:
                    if (TryPick(model, static value => value.Alive && value.Enableable is not null, ref random, out int replaceEnableable))
                        return new FuzzCommand(FuzzCommandKind.ReplaceEnableable, replaceEnableable, NextValue(ref random));
                    break;
                case 2:
                    if (TryPick(model, static value => value.Alive && value.Enableable is not null, ref random, out int removeEnableable))
                        return new FuzzCommand(FuzzCommandKind.RemoveEnableable, removeEnableable);
                    break;
                case 3:
                    if (TryPick(model, static value => value.Alive && value.Enableable is not null && !value.EnableableEnabled, ref random, out int enable))
                        return new FuzzCommand(FuzzCommandKind.Enable, enable);
                    break;
                case 4:
                    if (TryPick(model, static value => value.Alive && value.Enableable is not null && value.EnableableEnabled, ref random, out int disable))
                        return new FuzzCommand(FuzzCommandKind.Disable, disable);
                    break;
                case 5:
                    if (TryPick(model, static value => value.Alive && value.Sparse is null, ref random, out int addSparse))
                        return new FuzzCommand(FuzzCommandKind.AddSparse, addSparse, NextValue(ref random));
                    break;
                case 6:
                    if (TryPick(model, static value => value.Alive && value.Sparse is not null, ref random, out int replaceSparse))
                        return new FuzzCommand(FuzzCommandKind.ReplaceSparse, replaceSparse, NextValue(ref random));
                    break;
                case 7:
                    if (TryPick(model, static value => value.Alive && value.Sparse is not null, ref random, out int removeSparse))
                        return new FuzzCommand(FuzzCommandKind.RemoveSparse, removeSparse);
                    break;
                case 8:
                    if (TryPick(model, static value => value.Alive && value.Shared is null, ref random, out int addShared))
                        return new FuzzCommand(FuzzCommandKind.AddShared, addShared, NextValue(ref random));
                    break;
                case 9:
                    if (TryPick(model, static value => value.Alive && value.Shared is not null, ref random, out int replaceShared))
                        return new FuzzCommand(FuzzCommandKind.ReplaceShared, replaceShared, NextValue(ref random));
                    break;
                case 10:
                    if (TryPick(model, static value => value.Alive && value.Shared is not null, ref random, out int removeShared))
                        return new FuzzCommand(FuzzCommandKind.RemoveShared, removeShared);
                    break;
                case 11:
                    if (TryPick(model, static value => value.Alive && value.Buffer is null, ref random, out int addBuffer))
                        return new FuzzCommand(FuzzCommandKind.AddBuffer, addBuffer);
                    break;
                case 12:
                    if (TryPick(model, static value => value.Alive && value.Buffer is not null, ref random, out int appendBuffer))
                        return new FuzzCommand(FuzzCommandKind.AppendBuffer, appendBuffer, NextValue(ref random));
                    break;
                case 13:
                    if (TryPick(model, static value => value.Alive && value.Buffer is { Length: > 0 }, ref random, out int setBuffer))
                        return new FuzzCommand(FuzzCommandKind.SetBufferFirst, setBuffer, NextValue(ref random));
                    break;
                case 14:
                    if (TryPick(model, static value => value.Alive && value.Buffer is not null, ref random, out int removeBuffer))
                        return new FuzzCommand(FuzzCommandKind.RemoveBuffer, removeBuffer);
                    break;
                case 15:
                    if (TryPick(model, static value => value.Alive && value.Parent is not null, ref random, out int detach))
                        return new FuzzCommand(FuzzCommandKind.Detach, detach);
                    break;
                case 16:
                    if (TryGenerateSetParent(model, ref random, out FuzzCommand setParent))
                        return setParent;
                    break;
                case 17:
                    if (TryGenerateCreateRelation(model, ref random, out FuzzCommand createRelation))
                        return createRelation;
                    break;
                case 18:
                    if (model.Relations.Count != 0)
                    {
                        (int source, int target) = model.Relations[random.NextInt(model.Relations.Count)];
                        return new FuzzCommand(FuzzCommandKind.DestroyRelation, source, OtherEntityId: target);
                    }
                    break;
                default:
                    return GenerateValidBatchCommand(model, ref random, ref nextEntityId);
            }
        }

        return GenerateValidBatchCommand(model, ref random, ref nextEntityId);
    }

    private static FuzzCommand GenerateValidBatchCommand(
        ReferenceWorld model,
        ref FixedPrng random,
        ref int nextEntityId)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            switch (random.NextInt(18))
            {
                case 0:
                    if (nextEntityId <= MaximumLogicalEntities)
                        return new FuzzCommand(FuzzCommandKind.CreateEntity, nextEntityId++);
                    break;
                case 1:
                    if ((model.AliveCount > 1 || nextEntityId <= MaximumLogicalEntities) &&
                        TryPick(model, static value => value.Alive, ref random, out int destroy))
                        return new FuzzCommand(FuzzCommandKind.DestroyEntity, destroy);
                    break;
                case 2:
                    if (TryPick(model, static value => value.Alive && value.Alpha is null, ref random, out int addAlpha))
                        return new FuzzCommand(FuzzCommandKind.AddAlpha, addAlpha, NextValue(ref random));
                    break;
                case 3:
                    if (TryPick(model, static value => value.Alive && value.Alpha is not null, ref random, out int replaceAlpha))
                        return new FuzzCommand(FuzzCommandKind.ReplaceAlpha, replaceAlpha, NextValue(ref random));
                    break;
                case 4:
                    if (TryPick(model, static value => value.Alive && value.Alpha is not null, ref random, out int removeAlpha))
                        return new FuzzCommand(FuzzCommandKind.RemoveAlpha, removeAlpha);
                    break;
                case 5:
                    if (TryPick(model, static value => value.Alive && value.Beta is null, ref random, out int addBeta))
                        return new FuzzCommand(FuzzCommandKind.AddBeta, addBeta, NextValue(ref random));
                    break;
                case 6:
                    if (TryPick(model, static value => value.Alive && value.Beta is not null, ref random, out int replaceBeta))
                        return new FuzzCommand(FuzzCommandKind.ReplaceBeta, replaceBeta, NextValue(ref random));
                    break;
                case 7:
                    if (TryPick(model, static value => value.Alive && value.Beta is not null, ref random, out int removeBeta))
                        return new FuzzCommand(FuzzCommandKind.RemoveBeta, removeBeta);
                    break;
                case 8:
                    if (TryPick(model, static value => value.Alive && !value.HasTag, ref random, out int addTag))
                        return new FuzzCommand(FuzzCommandKind.AddTag, addTag);
                    break;
                case 9:
                    if (TryPick(model, static value => value.Alive && value.HasTag, ref random, out int removeTag))
                        return new FuzzCommand(FuzzCommandKind.RemoveTag, removeTag);
                    break;
                case 10:
                    if (TryPick(model, static value => value.Alive && value.Enableable is null, ref random, out int addEnableable))
                        return new FuzzCommand(FuzzCommandKind.AddEnableable, addEnableable, NextValue(ref random));
                    break;
                case 11:
                    if (TryPick(model, static value => value.Alive && value.Enableable is not null, ref random, out int replaceEnableable))
                        return new FuzzCommand(FuzzCommandKind.ReplaceEnableable, replaceEnableable, NextValue(ref random));
                    break;
                case 12:
                    if (TryPick(model, static value => value.Alive && value.Enableable is not null, ref random, out int removeEnableable))
                        return new FuzzCommand(FuzzCommandKind.RemoveEnableable, removeEnableable);
                    break;
                case 13:
                    if (TryPick(model, static value => value.Alive && value.Indexed is null, ref random, out int addIndexed))
                        return new FuzzCommand(FuzzCommandKind.AddIndexed, addIndexed, NextValue(ref random));
                    break;
                case 14:
                    if (TryPick(model, static value => value.Alive && value.Indexed is not null, ref random, out int replaceIndexed))
                        return new FuzzCommand(FuzzCommandKind.ReplaceIndexed, replaceIndexed, NextValue(ref random));
                    break;
                case 15:
                    if (TryPick(model, static value => value.Alive && value.Indexed is not null, ref random, out int removeIndexed))
                        return new FuzzCommand(FuzzCommandKind.RemoveIndexed, removeIndexed);
                    break;
                default:
                    break;
            }
        }

        if (nextEntityId <= MaximumLogicalEntities)
            return new FuzzCommand(FuzzCommandKind.CreateEntity, nextEntityId++);
        if (TryPick(model, static value => value.Alive && value.Alpha is null, ref random, out int fallbackAddAlpha))
            return new FuzzCommand(FuzzCommandKind.AddAlpha, fallbackAddAlpha, NextValue(ref random));
        if (TryPick(model, static value => value.Alive && value.Alpha is not null, ref random, out int fallbackReplaceAlpha))
            return new FuzzCommand(FuzzCommandKind.ReplaceAlpha, fallbackReplaceAlpha, NextValue(ref random));
        throw new InvalidOperationException("The bounded fuzz model has no valid fallback command.");
    }

    private static bool TryGenerateSetParent(
        ReferenceWorld model,
        ref FixedPrng random,
        out FuzzCommand command)
    {
        int[] live = model.EligibleIds(static entity => entity.Alive);
        for (int attempt = 0; attempt < 16 && live.Length >= 2; attempt++)
        {
            int child = live[random.NextInt(live.Length)];
            int parent = live[random.NextInt(live.Length)];
            if (child != parent && !model.WouldCreateHierarchyCycle(child, parent) &&
                model.Entity(child).Parent != parent)
            {
                command = new FuzzCommand(FuzzCommandKind.SetParent, child, OtherEntityId: parent);
                return true;
            }
        }
        command = default;
        return false;
    }

    private static bool TryGenerateCreateRelation(
        ReferenceWorld model,
        ref FixedPrng random,
        out FuzzCommand command)
    {
        int[] live = model.EligibleIds(static entity => entity.Alive);
        for (int attempt = 0; attempt < 16 && live.Length >= 2; attempt++)
        {
            int source = live[random.NextInt(live.Length)];
            int target = live[random.NextInt(live.Length)];
            if (source != target && !model.HasRelation(source, target))
            {
                command = new FuzzCommand(
                    FuzzCommandKind.CreateRelation,
                    source,
                    NextValue(ref random),
                    target);
                return true;
            }
        }
        command = default;
        return false;
    }

    private static bool TryPick(
        ReferenceWorld model,
        Func<ModelEntity, bool> predicate,
        ref FixedPrng random,
        out int entityId)
    {
        int[] eligible = model.EligibleIds(predicate);
        if (eligible.Length == 0)
        {
            entityId = 0;
            return false;
        }

        entityId = eligible[random.NextInt(eligible.Length)];
        return true;
    }

    private static void ApplyIfSuccessful(ReferenceWorld model, FuzzStep step)
    {
        using ReferenceWorld.Transaction transaction = model.BeginTransaction();
        try
        {
            foreach (FuzzCommand command in step.Commands)
                model.Apply(command);
            transaction.Commit();
        }
        catch (ModelOperationException)
        {
        }
    }

    private static FuzzStep Immediate(FuzzCommand command) =>
        new(FuzzStepMode.Immediate, [command]);

    private static int NextValue(ref FixedPrng random) => random.NextInt(2_000_001) - 1_000_000;
}
