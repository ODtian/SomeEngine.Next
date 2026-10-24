using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Lighting;

/// <summary>
/// Owns the immutable-for-rendering light values collected for one render-frame preparation.
/// Geometry and shading pipelines borrow this set; they do not own light lifetime or ECS queries.
/// </summary>
public sealed class RenderLightSet
{
    private readonly LightRows<RenderDirectionalLight> _directional = new();
    private readonly LightRows<RenderPointLight> _points = new();
    private readonly LightRows<RenderSpotLight> _spots = new();

    public IReadOnlyList<RenderDirectionalLight> Directional => _directional.Values;
    public IReadOnlyList<RenderPointLight> Points => _points.Values;
    public IReadOnlyList<RenderSpotLight> Spots => _spots.Values;
    public IReadOnlyList<RenderLightCookie?> DirectionalCookies => _directional.Cookies;
    public IReadOnlyList<RenderLightCookie?> PointCookies => _points.Cookies;
    public IReadOnlyList<RenderLightCookie?> SpotCookies => _spots.Cookies;

    public ulong Revision { get; private set; }

    public void Clear()
    {
        _directional.Clear();
        _points.Clear();
        _spots.Clear();
    }

    internal void CommitChanges() => Revision = checked(Revision + 1uL);

    public void CollectDirectional(scoped QueryCursor rows) => _directional.Append(rows);
    public void CollectPoint(scoped QueryCursor rows) => _points.Append(rows);
    public void CollectSpot(scoped QueryCursor rows) => _spots.Append(rows);
    public void AddDirectional(in RenderDirectionalLight light) => _directional.Add(light, null);
    public void AddDirectional(in RenderDirectionalLight light, in RenderLightCookie cookie) => _directional.Add(light, cookie);
    public void AddPoint(in RenderPointLight light) => _points.Add(light, null);
    public void AddPoint(in RenderPointLight light, in RenderLightCookie cookie) => _points.Add(light, cookie);
    public void AddSpot(in RenderSpotLight light) => _spots.Add(light, null);
    public void AddSpot(in RenderSpotLight light, in RenderLightCookie cookie) => _spots.Add(light, cookie);

    internal bool UpdateMembership(World world, Entity entity) =>
        _directional.UpdateMembership(world, entity) |
        _points.UpdateMembership(world, entity) |
        _spots.UpdateMembership(world, entity);

    internal bool UpdateDirectional(scoped QueryCursor rows) => _directional.Update(rows);
    internal bool UpdatePoints(scoped QueryCursor rows) => _points.Update(rows);
    internal bool UpdateSpots(scoped QueryCursor rows) => _spots.Update(rows);

    private sealed class LightRows<T> where T : struct, IComponent
    {
        internal readonly List<T> Values = [];
        internal readonly List<RenderLightCookie?> Cookies = [];
        private readonly List<Entity> _entities = [];
        private readonly Dictionary<Entity, int> _rows = [];

        internal void Clear()
        {
            Values.Clear();
            Cookies.Clear();
            _entities.Clear();
            _rows.Clear();
        }

        internal void Add(T value, RenderLightCookie? cookie)
        {
            Values.Add(value);
            Cookies.Add(cookie);
            _entities.Add(Entity.Null);
        }

        internal void Append(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
                Add(row.Read<T>(), row.TryRead<RenderLightCookie>(out var cookie) ? cookie : null);
        }

        internal bool UpdateMembership(World world, Entity entity)
        {
            if (world.IsAlive(entity) && world.Has<T>(entity))
                return Set(entity, world.Read<T>(entity), world.Has<RenderLightCookie>(entity)
                    ? world.Read<RenderLightCookie>(entity) : null);
            if (!_rows.Remove(entity, out int index)) return false;
            int last = Values.Count - 1;
            if (index != last)
            {
                Values[index] = Values[last];
                Cookies[index] = Cookies[last];
                _entities[index] = _entities[last];
                if (_entities[index] != Entity.Null) _rows[_entities[index]] = index;
            }
            Values.RemoveAt(last);
            Cookies.RemoveAt(last);
            _entities.RemoveAt(last);
            return true;
        }

        internal bool Update(scoped QueryCursor query)
        {
            bool changed = false;
            foreach (QueryChunkView chunk in query.Chunks)
            {
                bool hasCookies = chunk.Has<RenderLightCookie>();
                bool valuesChanged = chunk.HasChangedSinceLastSystemVersion<T>();
                bool cookiesChanged = hasCookies && chunk.HasChangedSinceLastSystemVersion<RenderLightCookie>();
                if (!valuesChanged && !cookiesChanged) continue;
                ReadOnlySpan<T> values = chunk.Read<T>();
                ReadOnlySpan<RenderLightCookie> cookies = hasCookies ? chunk.Read<RenderLightCookie>() : default;
                for (int row = 0; row < values.Length; row++)
                    if (valuesChanged && chunk.RowChangedSinceLastSystemVersion<T>(row) ||
                        cookiesChanged && chunk.RowChangedSinceLastSystemVersion<RenderLightCookie>(row))
                        changed |= Set(chunk.Entities[row], values[row], hasCookies ? cookies[row] : null);
            }
            return changed;
        }

        private bool Set(Entity entity, T value, RenderLightCookie? cookie)
        {
            if (_rows.TryGetValue(entity, out int row))
            {
                if (EqualityComparer<T>.Default.Equals(Values[row], value) && Cookies[row] == cookie) return false;
                Values[row] = value;
                Cookies[row] = cookie;
            }
            else
            {
                _rows.Add(entity, Values.Count);
                Add(value, cookie);
                _entities[^1] = entity;
            }
            return true;
        }
    }
}
