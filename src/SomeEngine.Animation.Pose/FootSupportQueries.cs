using System.Numerics;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

/// <summary>Unit world up and probe distances above and below each requested foot position.</summary>
public readonly record struct FootSupportSettings(Vector3 Up, float Above, float Below);

/// <summary>A world-space departure-to-landing segment and the swept foot's radius.</summary>
public readonly record struct FootStride(Vector3 Start, Vector3 Landing, float Radius);

/// <summary>One stride fraction with the foot sphere's first-contact center and the supporting triangle.</summary>
public readonly record struct FootTerrainSample(float Fraction, bool HasSupport, Vector3 Center, CollisionHit Support);

public readonly record struct FootSupportRequest(Vector3 Position, FootContactKind Kind, double Time = 0)
{
    /// <summary>Uses the selected toe pivot or heel position, transformed from model to world coordinates.</summary>
    public static FootSupportRequest FromPrediction(in FootPredictionSample sample, Matrix4x4 modelToWorld)
    {
        Vector3 point = sample.Contact.Kind == FootContactKind.ToePivot ? sample.Trajectory.Toe : sample.Trajectory.Heel;
        return new(Vector3.Transform(point, modelToWorld), sample.Contact.Kind, sample.Prediction.Time);
    }
}

/// <summary>A contact point and normal in world and support-local coordinates, retaining the source contact time and kind.</summary>
public readonly record struct FootContactTarget(double Time, FootContactKind Kind, bool HasSupport,
    CollisionHit Support, Vector3 LocalPoint, Vector3 LocalNormal)
{
    public static FootContactTarget FromHit(in FootSupportRequest request, in CollisionHit hit)
    {
        Matrix4x4.Invert(hit.Transform, out Matrix4x4 inverse);
        Vector3 localPoint = Vector3.Transform(hit.Point, inverse);
        Vector3 localNormal = Vector3.Normalize(Vector3.TransformNormal(hit.Normal, Matrix4x4.Transpose(hit.Transform)));
        return new(request.Time, request.Kind, true, hit, localPoint, localNormal);
    }
}

/// <summary>Reusable scratch for batched foot queries; capacity covers the largest batch supplied by the caller.</summary>
public sealed class FootSupportQueries(ICollisionQueries scene, FootSupportSettings settings, int capacity)
{
    private readonly CollisionRay[] _rays = new CollisionRay[capacity];
    private readonly CollisionSphereSweep[] _sweeps = new CollisionSphereSweep[capacity];
    private readonly CollisionHit[] _hits = new CollisionHit[capacity];
    private readonly bool[] _found = new bool[capacity];

    public void Sample(ReadOnlySpan<FootSupportRequest> requests, Span<FootContactTarget> targets)
    {
        for (int index = 0; index < requests.Length; index++)
            _rays[index] = new(requests[index].Position + settings.Up * settings.Above,
                -settings.Up, settings.Above + settings.Below);
        scene.Raycast(_rays.AsSpan(0, requests.Length), _hits.AsSpan(0, requests.Length), _found.AsSpan(0, requests.Length));
        for (int index = 0; index < requests.Length; index++)
            targets[index] = _found[index] ? FootContactTarget.FromHit(in requests[index], in _hits[index])
                : new(requests[index].Time, requests[index].Kind, false, default, default, default);
    }

    /// <summary>Samples the complete stride, including its endpoints, with downward swept foot spheres.</summary>
    public void SampleEnvelope(in FootStride stride, Span<FootTerrainSample> samples)
    {
        for (int index = 0; index < samples.Length; index++)
        {
            float fraction = (float)index / (samples.Length - 1);
            Vector3 position = Vector3.Lerp(stride.Start, stride.Landing, fraction);
            _sweeps[index] = new(position + settings.Up * settings.Above, stride.Radius,
                -settings.Up, settings.Above + settings.Below);
        }
        SweepEnvelope(samples);
    }

    /// <summary>Samples an ordered world-space foot path; fractions follow the supplied sample order.</summary>
    public void SampleEnvelope(ReadOnlySpan<Vector3> path, float radius, Span<FootTerrainSample> samples)
    {
        for (int index = 0; index < samples.Length; index++)
            _sweeps[index] = new(path[index] + settings.Up * settings.Above, radius,
                -settings.Up, settings.Above + settings.Below);
        SweepEnvelope(samples);
    }

    private void SweepEnvelope(Span<FootTerrainSample> samples)
    {
        scene.SweepSphere(_sweeps.AsSpan(0, samples.Length), _hits.AsSpan(0, samples.Length), _found.AsSpan(0, samples.Length));
        for (int index = 0; index < samples.Length; index++)
        {
            float fraction = (float)index / (samples.Length - 1);
            Vector3 center = _found[index]
                ? _sweeps[index].Center + _sweeps[index].Direction * _hits[index].Distance
                : _sweeps[index].Center - settings.Up * settings.Above;
            samples[index] = new(fraction, _found[index], center, _hits[index]);
        }
    }

    /// <summary>Reconstructs the target from its retained local anchor and the support's current transform and revision.</summary>
    public bool TryRefresh(in FootContactTarget target, out FootContactTarget current)
    {
        if (!target.HasSupport || !scene.TryGetTransform(target.Support.Entity, out Matrix4x4 transform, out ulong version))
        {
            current = default;
            return false;
        }
        Matrix4x4.Invert(transform, out Matrix4x4 inverse);
        Vector3 point = Vector3.Transform(target.LocalPoint, transform);
        Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(target.LocalNormal, Matrix4x4.Transpose(inverse)));
        current = target with { Support = new(target.Support.Entity, point, normal, 0, transform, version) };
        return true;
    }
}
