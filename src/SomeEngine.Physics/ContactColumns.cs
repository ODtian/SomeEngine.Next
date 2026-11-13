using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct ContactPair(Entity FirstGeometry, Entity SecondGeometry,
    uint FirstShape, uint SecondShape, int FirstPoint, int PointCount);

[Flags]
public enum ContactPointFlags : uint { None = 0, WarmStart = 1, Trigger = 2 }

/// <summary>Common contact values and the body-to-pair index produced by one executor.</summary>
public sealed class ContactColumns : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResource _resource;
    private readonly JobResourceAccess[] _reads;
    private ContactPair[] _pairs = [];
    private int[] _pairIndices = [], _firstFeatures = [], _secondFeatures = [], _pointIndices = [];
    private Vector3[] _firstLocalPoints = [], _secondLocalPoints = [], _worldPoints = [], _normals = [], _anchorDeltas = [];
    private float[] _separations = [];
    private ContactPrediction[] _predictions = [];
    private ContactPointFlags[] _flags = [];
    private int[] _bodyStarts = [], _bodyCounts = [], _firstBodyCounts = [];
    private ContactMaterial[] _materials = [];
    private ContactResponse[] _responses = [];
    private ContactBodyLink[] _bodyLinks = [];
    private readonly Dictionary<Entity, int> _bodyRows = [];
    private int[] _bodyPairCounts = [], _lastBodyPair = [], _bodyPairOffsets = [], _bodyPairs = [];
    private int _bodyCount, _bodyPairCount;
    private bool _disposed;

    public ContactColumns(Scheduler scheduler)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResource("Contact columns");
        _reads = [JobResourceAccess.Read(_resource)];
        Contacts = new(this);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    internal void ValidateRead() => _scheduler.ValidateResourceAccess(ReadAccess);
    [System.Diagnostics.Conditional("DEBUG")]
    internal void ValidateWrite() => _scheduler.ValidateResourceAccess(WriteAccess);

    public PhysicsContacts Contacts { get; }
    public JobResourceAccess ReadAccess => _reads[0];
    public JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    public PhysicsStepResult Step { get; private set; }
    public int PairCount { get; private set; }
    public int PointCount { get; private set; }
    public int BodyLinkCount { get; private set; }
    public Span<ContactPair> Pairs { get { ValidateWrite(); return _pairs.AsSpan(0, PairCount); } }
    public Span<int> PairIndices { get { ValidateWrite(); return _pairIndices.AsSpan(0, PointCount); } }
    public Span<int> FirstFeatures { get { ValidateWrite(); return _firstFeatures.AsSpan(0, PointCount); } }
    public Span<int> SecondFeatures { get { ValidateWrite(); return _secondFeatures.AsSpan(0, PointCount); } }
    public Span<int> PointIndices { get { ValidateWrite(); return _pointIndices.AsSpan(0, PointCount); } }
    public Span<Vector3> FirstLocalPoints { get { ValidateWrite(); return _firstLocalPoints.AsSpan(0, PointCount); } }
    public Span<Vector3> SecondLocalPoints { get { ValidateWrite(); return _secondLocalPoints.AsSpan(0, PointCount); } }
    public Span<Vector3> WorldPoints { get { ValidateWrite(); return _worldPoints.AsSpan(0, PointCount); } }
    public Span<Vector3> Normals { get { ValidateWrite(); return _normals.AsSpan(0, PointCount); } }
    public Span<Vector3> BodyAnchorDeltas { get { ValidateWrite(); return _anchorDeltas.AsSpan(0, PointCount); } }
    public Span<float> Separations { get { ValidateWrite(); return _separations.AsSpan(0, PointCount); } }
    public Span<ContactPrediction> Predictions { get { ValidateWrite(); return _predictions.AsSpan(0, PointCount); } }
    public Span<ContactPointFlags> Flags { get { ValidateWrite(); return _flags.AsSpan(0, PointCount); } }
    public Span<int> BodyStarts { get { ValidateWrite(); return _bodyStarts.AsSpan(0, PointCount); } }
    public Span<int> BodyCounts { get { ValidateWrite(); return _bodyCounts.AsSpan(0, PointCount); } }
    public Span<int> FirstBodyCounts { get { ValidateWrite(); return _firstBodyCounts.AsSpan(0, PointCount); } }
    public Span<ContactMaterial> Materials { get { ValidateWrite(); return _materials.AsSpan(0, PointCount); } }
    public Span<ContactResponse> Responses { get { ValidateWrite(); return _responses.AsSpan(0, PointCount); } }
    public Span<ContactBodyLink> BodyLinks { get { ValidateWrite(); return _bodyLinks.AsSpan(0, BodyLinkCount); } }

    public void Resize(int pairs, int points, int bodyLinks)
    {
        ValidateWrite();
        Grow(ref _pairs, pairs);
        Grow(ref _pairIndices, points); Grow(ref _firstFeatures, points); Grow(ref _secondFeatures, points); Grow(ref _pointIndices, points);
        Grow(ref _firstLocalPoints, points); Grow(ref _secondLocalPoints, points); Grow(ref _worldPoints, points);
        Grow(ref _normals, points); Grow(ref _anchorDeltas, points); Grow(ref _separations, points);
        Grow(ref _predictions, points); Grow(ref _flags, points);
        Grow(ref _bodyStarts, points); Grow(ref _bodyCounts, points); Grow(ref _firstBodyCounts, points);
        Grow(ref _materials, points); Grow(ref _responses, points); Grow(ref _bodyLinks, bodyLinks);
        PairCount = pairs; PointCount = points; BodyLinkCount = bodyLinks;
    }

    internal static void Grow<T>(ref T[] values, int count)
    {
        if (values.Length < count) Array.Resize(ref values, Math.Max(count, Math.Max(16, values.Length * 2)));
    }

    public ContactPoint GetPoint(int point)
    {
        ValidateRead();
        return new()
        {
            PairIndex = _pairIndices[point],
            FirstFeature = _firstFeatures[point],
            SecondFeature = _secondFeatures[point],
            PointIndex = _pointIndices[point],
            FirstLocalPoint = _firstLocalPoints[point],
            SecondLocalPoint = _secondLocalPoints[point],
            WorldPoint = _worldPoints[point],
            Normal = _normals[point],
            Separation = _separations[point],
            BodyAnchorDelta = _anchorDeltas[point],
            FirstBodyCount = _firstBodyCounts[point],
            WarmStart = (_flags[point] & ContactPointFlags.WarmStart) != 0,
            Prediction = _predictions[point],
        };
    }

    public void SetPoint(int point, in ContactPoint value)
    {
        ValidateWrite();
        _pairIndices[point] = value.PairIndex; _firstFeatures[point] = value.FirstFeature;
        _secondFeatures[point] = value.SecondFeature; _pointIndices[point] = value.PointIndex;
        _firstLocalPoints[point] = value.FirstLocalPoint; _secondLocalPoints[point] = value.SecondLocalPoint;
        _worldPoints[point] = value.WorldPoint; _normals[point] = value.Normal;
        _separations[point] = value.Separation; _anchorDeltas[point] = value.BodyAnchorDelta;
        _firstBodyCounts[point] = value.FirstBodyCount; _predictions[point] = value.Prediction;
        _flags[point] = value.WarmStart ? _flags[point] | ContactPointFlags.WarmStart : _flags[point] & ~ContactPointFlags.WarmStart;
    }

    public ReadOnlySpan<ContactBodyLink> BodyLinksOf(int point)
    { ValidateRead(); return _bodyLinks.AsSpan(_bodyStarts[point], _bodyCounts[point]); }
    public Range PointsOf(int pair) => _pairs[pair].FirstPoint..(_pairs[pair].FirstPoint + _pairs[pair].PointCount);
    internal ReadOnlySpan<ContactPair> ReadPairs => _pairs.AsSpan(0, PairCount);
    internal ReadOnlySpan<ContactMaterial> ReadMaterials => _materials.AsSpan(0, PointCount);
    internal ReadOnlySpan<ContactResponse> ReadResponses => _responses.AsSpan(0, PointCount);
    internal ReadOnlySpan<ContactPointFlags> ReadFlags => _flags.AsSpan(0, PointCount);
    internal StepAccess Access => new(Step.Completion, _reads);
    internal ReadOnlySpan<int> PairsOf(Entity body)
        => _bodyRows.TryGetValue(body, out int row) ? _bodyPairs.AsSpan(_bodyPairOffsets[row], _bodyPairCounts[row]) : [];

    /// <summary>Builds the inverse index from these common links, including detection-only results.</summary>
    public void RebuildBodyPairs()
    {
        ValidateWrite();
        _bodyRows.Clear(); _bodyCount = 0; _bodyPairCount = 0;
        for (int pair = 0; pair < PairCount; pair++)
        {
            ContactPair value = _pairs[pair];
            for (int point = value.FirstPoint; point < value.FirstPoint + value.PointCount; point++)
                foreach (ContactBodyLink link in BodyLinksOf(point))
                {
                    if (!_bodyRows.TryGetValue(link.Body, out int body))
                    {
                        body = _bodyCount++; _bodyRows.Add(link.Body, body);
                        Grow(ref _bodyPairCounts, _bodyCount); Grow(ref _lastBodyPair, _bodyCount);
                        _bodyPairCounts[body] = 0; _lastBodyPair[body] = -1;
                    }
                    if (_lastBodyPair[body] == pair) continue;
                    _lastBodyPair[body] = pair; _bodyPairCounts[body]++; _bodyPairCount++;
                }
        }
        Grow(ref _bodyPairOffsets, _bodyCount + 1); Grow(ref _bodyPairs, _bodyPairCount);
        _bodyPairOffsets[0] = 0;
        for (int body = 0; body < _bodyCount; body++)
        {
            _bodyPairOffsets[body + 1] = _bodyPairOffsets[body] + _bodyPairCounts[body];
            _bodyPairCounts[body] = 0; _lastBodyPair[body] = -1;
        }
        for (int pair = 0; pair < PairCount; pair++)
        {
            ContactPair value = _pairs[pair];
            for (int point = value.FirstPoint; point < value.FirstPoint + value.PointCount; point++)
                foreach (ContactBodyLink link in BodyLinksOf(point))
                {
                    int body = _bodyRows[link.Body];
                    if (_lastBodyPair[body] == pair) continue;
                    _lastBodyPair[body] = pair;
                    _bodyPairs[_bodyPairOffsets[body] + _bodyPairCounts[body]++] = pair;
                }
        }
    }

    public void SetStep(in PhysicsStepResult step) => Step = step;
    public void Publish(in PhysicsStepResult step) => SetStep(in step);
    public void Dispose()
    {
        if (_disposed) return;
        _scheduler.ReleaseResource(_resource); _disposed = true;
        PairCount = PointCount = BodyLinkCount = 0; _bodyRows.Clear(); _bodyCount = _bodyPairCount = 0;
        _pairs = []; _pairIndices = []; _firstFeatures = []; _secondFeatures = []; _pointIndices = [];
        _firstLocalPoints = []; _secondLocalPoints = []; _worldPoints = []; _normals = []; _anchorDeltas = [];
        _separations = []; _predictions = []; _flags = []; _bodyStarts = []; _bodyCounts = []; _firstBodyCounts = [];
        _materials = []; _responses = []; _bodyLinks = []; _bodyPairCounts = []; _lastBodyPair = [];
        _bodyPairOffsets = []; _bodyPairs = [];
    }
}

/// <summary>Reads the common contact result supplied by its executor.</summary>
public sealed class PhysicsContacts
{
    private readonly ContactColumns _columns;
    public PhysicsContacts(ContactColumns columns) => _columns = columns;
    public StepAccess Access => _columns.Access;
    public PhysicsStepResult Step => _columns.Step;
    public ContactView OpenView() { _columns.ValidateRead(); return new(_columns); }
}

public readonly ref struct ContactView
{
    private readonly ContactColumns _columns;
    internal ContactView(ContactColumns columns) => _columns = columns;
    public ReadOnlySpan<ContactPair> Pairs => _columns.ReadPairs;
    public int PointCount => _columns.PointCount;
    public Range PointsOf(int pairIndex) => _columns.PointsOf(pairIndex);
    public ReadOnlySpan<int> PairsOf(Entity body) => _columns.PairsOf(body);
    public ContactPoint GetPoint(int pointIndex) => _columns.GetPoint(pointIndex);
    public ReadOnlySpan<ContactBodyLink> BodyLinksOf(int pointIndex) => _columns.BodyLinksOf(pointIndex);
    public ReadOnlySpan<ContactMaterial> Materials => _columns.ReadMaterials;
    public ReadOnlySpan<ContactResponse> Responses => _columns.ReadResponses;
    public ReadOnlySpan<ContactPointFlags> Flags => _columns.ReadFlags;
}
