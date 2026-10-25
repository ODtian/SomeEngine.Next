using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

internal sealed class ContactSupportPolygon
{
    internal readonly List<SupportVertex> Points = [];
    private readonly List<SupportVertex> _sorted = [], _hull = [];
    private int _hullCount = -1;

    internal void Clear() { Points.Clear(); _sorted.Clear(); _hull.Clear(); _hullCount = -1; }

    internal bool TryWeights(Vector2 point, float tolerance, Span<LongRangeSupportLoad> weights, out int count)
    {
        count = 0;
        if (Points.Count == 0) return false;
        BuildHull();
        if (_hull.Count <= 2)
        {
            SupportVertex first = _hull[0], last = _hull[^1];
            Vector2 line = last.Point - first.Point;
            float squared = line.LengthSquared();
            if (squared == 0)
            {
                if (Vector2.DistanceSquared(point, first.Point) > tolerance * tolerance) return false;
                weights[0] = new(first.Contact, 1); count = 1; return true;
            }
            float fraction = Vector2.Dot(point - first.Point, line) / squared;
            if (fraction < -tolerance || fraction > 1 + tolerance) return false;
            fraction = Math.Clamp(fraction, 0, 1);
            if (Vector2.DistanceSquared(first.Point + line * fraction, point) > tolerance * tolerance) return false;
            weights[0] = new(first.Contact, 1 - fraction); weights[1] = new(last.Contact, fraction); count = 2; return true;
        }
        for (int index = 1; index < _hull.Count - 1; index++)
        {
            SupportVertex first = _hull[0], second = _hull[index], third = _hull[index + 1];
            double area = Orientation(first.Point, second.Point, third.Point);
            double b = Orientation(first.Point, point, third.Point) / area;
            double c = Orientation(first.Point, second.Point, point) / area;
            double a = 1 - b - c;
            if (Math.Min(a, Math.Min(b, c)) < -tolerance) continue;
            a = Math.Max(0, a); b = Math.Max(0, b); c = Math.Max(0, c);
            double total = a + b + c;
            weights[0] = new(first.Contact, (float)(a / total));
            weights[1] = new(second.Contact, (float)(b / total));
            weights[2] = new(third.Contact, (float)(c / total));
            count = 3;
            return true;
        }
        return false;
    }

    private void BuildHull()
    {
        if (_hullCount == Points.Count) return;
        _hullCount = Points.Count;
        _sorted.Clear(); _sorted.AddRange(Points);
        _sorted.Sort(static (first, last) =>
        {
            int x = first.Point.X.CompareTo(last.Point.X);
            if (x != 0) return x;
            int y = first.Point.Y.CompareTo(last.Point.Y);
            return y != 0 ? y : EntityOrder.Compare(first.Contact, last.Contact);
        });
        int unique = 0;
        for (int index = 0; index < _sorted.Count; index++)
            if (unique == 0 || _sorted[index].Point != _sorted[unique - 1].Point) _sorted[unique++] = _sorted[index];
        _sorted.RemoveRange(unique, _sorted.Count - unique);
        _hull.Clear();
        if (_sorted.Count <= 2) { _hull.AddRange(_sorted); return; }
        foreach (SupportVertex vertex in _sorted)
        {
            while (_hull.Count >= 2 && Orientation(_hull[^2].Point, _hull[^1].Point, vertex.Point) <= 0) _hull.RemoveAt(_hull.Count - 1);
            _hull.Add(vertex);
        }
        int lowerCount = _hull.Count;
        for (int index = _sorted.Count - 2; index >= 0; index--)
        {
            SupportVertex vertex = _sorted[index];
            while (_hull.Count > lowerCount && Orientation(_hull[^2].Point, _hull[^1].Point, vertex.Point) <= 0) _hull.RemoveAt(_hull.Count - 1);
            _hull.Add(vertex);
        }
        _hull.RemoveAt(_hull.Count - 1);
    }

    private static double Orientation(Vector2 a, Vector2 b, Vector2 c) =>
        ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
}

internal readonly record struct SupportVertex(Vector2 Point, Entity Contact);
