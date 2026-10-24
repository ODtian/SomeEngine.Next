using System.Numerics;

namespace SomeEngine.Animation.Pose;

public sealed partial class PosePath
{
    internal FootMotion? FindFootMotion()
    {
        if (_footMotion is not null) return _footMotion;
        if (_definition is LoopData loop) return loop.Part.FindFootMotion();
        if (_definition is SequenceData sequence)
            foreach (PosePath part in sequence.Parts)
                if (part.FindFootMotion() is { } feet) return feet;
        if (_definition is BlendData blend)
            foreach (PosePathInput input in blend.Inputs)
                if (input.Path.FindFootMotion() is { } feet) return feet;
        if (_definition is LayerData layer) return layer.Base.FindFootMotion() ?? layer.Source.FindFootMotion();
        return null;
    }

    internal bool HasFootTrajectory => _footMotion is not null ||
        _definition is LoopData loop && loop.Part.HasFootTrajectory ||
        _definition is SequenceData sequence && Array.TrueForAll(sequence.Parts, static part => part.HasFootTrajectory);

    internal FootTrajectory SampleFoot(int foot, float time)
    {
        if (_footMotion is not null) return _footMotion.Sample(foot, time);
        if (_definition is LoopData loop)
        {
            FootTrajectory phase = loop.Part.SampleFoot(foot, loop.Phase(time));
            Matrix4x4 movement = loop.Movement(time);
            return new(phase.Root * movement, Vector3.Transform(phase.Heel, movement), Vector3.Transform(phase.Toe, movement));
        }
        var sequence = (SequenceData)_definition!;
        int part = sequence.Find(time);
        return sequence.Parts[part].SampleFoot(foot, time - sequence.Starts[part]);
    }

    internal FootContactKind? FootIntent(int foot, float time, bool automatic)
    {
        if (_definition is LoopData loop) return loop.Part.FootIntent(foot, loop.Phase(time), automatic);
        if (_footMotion is not null)
        {
            if (automatic) return _footMotion.ContactAt(foot, time).Kind;
            time = Math.Min(time, Duration);
            FootContactKind? kind = null;
            foreach (FootContactMarker marker in _footMotion.Markers)
                if (marker.Foot == foot && time >= marker.Start &&
                    (time < marker.End || time == Duration && marker.End == Duration)) kind = marker.Kind;
            return kind;
        }
        if (_definition is SequenceData sequence)
        {
            int part = sequence.Find(time);
            return sequence.Parts[part].FootIntent(foot, time - sequence.Starts[part], automatic);
        }
        if (_definition is BlendData blend)
        {
            PosePath? source = null;
            float maximum = 0;
            foreach (PosePathInput input in blend.Inputs)
            {
                float weight = input.Weight.Sample(time);
                if (weight <= maximum) continue;
                maximum = weight;
                source = input.Path;
            }
            return source?.FootIntent(foot, time, automatic: false);
        }
        if (_definition is LayerData layer)
        {
            FootBones bones = FindFootMotion()!.Bones(foot);
            float mask = 0;
            for (int bone = bones.Heel; bone >= 0; bone = Skeleton.Parents[bone])
                mask = Math.Max(mask, layer.Mask.Weights[bone]);
            for (int bone = bones.Toe; bone >= 0; bone = Skeleton.Parents[bone])
                mask = Math.Max(mask, layer.Mask.Weights[bone]);
            PosePath source = layer.Weight.Sample(time) * mask > 0.5f ? layer.Source : layer.Base;
            return source.FootIntent(foot, time, automatic: false);
        }
        return null;
    }

    internal void CollectFootTimes(float from, float to, float offset, List<float> times)
    {
        if (_definition is LoopData loop)
        {
            float duration = loop.Part.Duration;
            int first = Math.Max(0, (int)MathF.Floor((from - offset) / duration));
            int last = (int)MathF.Floor((to - offset) / duration);
            for (int cycle = first; cycle <= last; cycle++)
                loop.Part.CollectFootTimes(from, to, offset + cycle * duration, times);
        }
        if (_footMotion is not null)
            foreach (RootMotionKey key in _footMotion.Root.Keys) AddTime(key.Time + offset, from, to, times);
        if (_definition is SequenceData sequence)
            for (int part = 0; part < sequence.Parts.Length; part++)
                sequence.Parts[part].CollectFootTimes(from, to, offset + sequence.Starts[part], times);
        if (_definition is BlendData blend)
            foreach (PosePathInput input in blend.Inputs)
            {
                input.Path.CollectFootTimes(from, to, offset, times);
                AddTime(input.Weight.Start + offset, from, to, times);
                AddTime(input.Weight.Start + input.Weight.Duration + offset, from, to, times);
            }
        if (_definition is LayerData layer)
        {
            layer.Base.CollectFootTimes(from, to, offset, times);
            layer.Source.CollectFootTimes(from, to, offset, times);
            AddTime(layer.Weight.Start + offset, from, to, times);
            AddTime(layer.Weight.Start + layer.Weight.Duration + offset, from, to, times);
        }
    }

    private static void AddTime(float time, float from, float to, List<float> times)
    {
        if (time > from && time < to) times.Add(time);
    }
}
