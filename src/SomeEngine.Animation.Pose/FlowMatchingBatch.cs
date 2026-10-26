using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>One retained concrete matrix workspace for all characters using one ready model.</summary>
internal sealed class FlowMatchingBatch
{
    internal readonly FlowMatchingModel Model;
    internal readonly FlowMatchingState[] States;
    internal readonly JobResourceAccess[] Accesses;
    private readonly FlowMatchingState[] _active;
    private readonly int _stride;
    private float[] _first, _second;

    internal FlowMatchingBatch(Scheduler scheduler, FlowMatchingModel model, FlowMatchingState[] states)
    {
        Model = model;
        States = states;
        _active = new FlowMatchingState[states.Length];
        _stride = Math.Max(model.Data.Flow.Widths.Max(), model.Data.Decoder.Widths.Max());
        _first = new float[states.Length * _stride];
        _second = new float[states.Length * _stride];
        Accesses = [JobResourceAccess.Read(scheduler.GetResourceToken(model)),
            JobResourceAccess.Write(scheduler.GetResourceToken(this))];
    }

    internal bool HasSamples => States.Any(state => state.Active && state.Sample);

    internal void Evaluate()
    {
        int count = 0;
        foreach (FlowMatchingState state in States)
            if (state.Active && state.Sample)
            {
                _active[count++] = state;
                state.StartSample();
            }
        FlowMatchingData data = Model.Data;
        int latent = Model.LatentCount;
        for (int step = 0; step < Model.IntegrationSteps; step++)
        {
            for (int actor = 0; actor < count; actor++)
            {
                FlowMatchingState state = _active[actor];
                Span<float> input = _first.AsSpan(actor * _stride);
                state.Transported.CopyTo(input);
                state.Previous.CopyTo(input[latent..]);
                for (int value = 0; value < state.Control.Length; value++)
                    input[2 * latent + value] = (state.Control[value] - data.ControlOffset[value]) / data.ControlScale[value];
                input[2 * latent + state.Control.Length] = (float)step / Model.IntegrationSteps;
            }
            Network(data.Flow, count);
            for (int actor = 0; actor < count; actor++)
            {
                FlowMatchingState state = _active[actor];
                for (int value = 0; value < latent; value++)
                    state.Transported[value] += (1f / Model.IntegrationSteps) * data.LatentDeviation[value] * _first[actor * _stride + value];
            }
        }
        for (int actor = 0; actor < count; actor++)
        {
            FlowMatchingState state = _active[actor];
            for (int value = 0; value < latent; value++)
            {
                state.Previous[value] = Math.Clamp(state.Transported[value], data.LatentMinimum[value], data.LatentMaximum[value]);
                _first[actor * _stride + value] = state.Previous[value] * data.LatentScale[value] + data.LatentOffset[value];
            }
        }
        Network(data.Decoder, count);
        for (int actor = 0; actor < count; actor++)
        {
            FlowMatchingState state = _active[actor];
            _first.AsSpan(actor * _stride, state.Decoded.Length).CopyTo(state.Decoded);
            state.Decode();
        }
    }

    private void Network(FlowNetworkData network, int count)
    {
        for (int layer = 0; layer < network.Weights.Length; layer++)
        {
            int source = network.Widths[layer], target = network.Widths[layer + 1];
            float[] weights = network.Weights[layer], biases = network.Biases[layer];
            bool hidden = layer < network.Weights.Length - 1;
            for (int actor = 0; actor < count; actor++)
            {
                int start = actor * _stride;
                for (int output = 0; output < target; output++)
                {
                    float sum = biases[output];
                    int row = output * source;
                    for (int input = 0; input < source; input++) sum += weights[row + input] * _first[start + input];
                    _second[start + output] = hidden && sum < 0 ? MathF.Exp(sum) - 1 : sum;
                }
            }
            (_first, _second) = (_second, _first);
        }
    }
}

/// <summary>Installed state references grouped only when concrete flow connections change.</summary>
internal sealed class FlowMatchingGroups
{
    private static readonly ConditionalWeakTable<World, FlowMatchingGroups> Worlds = new();
    private readonly List<(Entity Animation, FlowMatchingState State)> _instances = [];
    private bool _changed;
    internal FlowMatchingBatch[] Batches = [];
    internal static FlowMatchingGroups For(World world) => Worlds.GetValue(world, static _ => new());
    internal void Add(Entity animation, FlowMatchingState state) { _instances.Add((animation, state)); _changed = true; }
    internal void Remove(Entity animation) { _instances.RemoveAll(instance => instance.Animation == animation); _changed = true; }
    internal void Bind(Scheduler scheduler)
    {
        if (!_changed) return;
        Batches = _instances.GroupBy(instance => instance.State.Model)
            .Select(group => new FlowMatchingBatch(scheduler, group.Key, group.Select(instance => instance.State).ToArray())).ToArray();
        _changed = false;
    }
}
