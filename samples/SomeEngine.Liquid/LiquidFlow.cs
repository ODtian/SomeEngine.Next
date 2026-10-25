using System.Numerics;

namespace SomeEngine.Liquid;

/// <summary>Conservative moving volumes, slender free strands and viscous surface patches.</summary>
public sealed partial class LiquidFlow
{
    public const int Capacity = 64;
    public const float StepSeconds = 1f / 120;
    public const float TrayFloor = .055f, TrayHalfWidth = 2.025f, TrayHalfDepth = .975f;
    public const float ContactRadius = .003f;
    public int PointLimit { get; }
    public string Scene { get; }
    public float Extent { get; }
    public LiquidFluid Fluid { get; private set; }
    public LiquidEmission Emission { get; private set; }
    public LiquidMaterial Material { get; set; }
    public LiquidSource Source { get; set; }
    public LiquidWetting Wetting { get; set; } = new(20, 70);
    public LiquidEvaporation Evaporation { get; set; }
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);
    public LiquidSurface Surface { get; set; } = new(new(.95f, -.38f, .14f, .20f), new(3.1f, 5, .065f, 9), new(1.4f, 1.75f, .85f, .06f));
    public readonly LiquidObstacle[] Obstacles = new LiquidObstacle[2];
    public bool Pouring { get; set; } = true;
    public bool Stirring { get; set; }
    public float Viscosity { get => Material.Viscosity; set => Material = Material with { Viscosity = value }; }
    public float NozzleX { get => Source.Position.X; set => Source = Source with { Position = new(value, Source.Position.Y, Source.Position.Z) }; }
    public Vector3 Nozzle => Source.Position;
    public Vector3 NozzleVelocity => Source.Velocity;
    public int NozzlePoint => _sourcePoint;
    public LiquidInlet Inlet
    {
        get
        {
            float age = (float)(Seconds - _sourceStartedSeconds);
            Vector3 front = Source.Position + Source.Velocity * age + .5f * Gravity * age * age;
            return new(NozzleConnected ? _sourcePoint : -1, Source.Position, front);
        }
    }
    public float NozzleRadius => Source.SurfaceRadius > 0 ? Source.SurfaceRadius : MathF.Sqrt((float)(Source.VolumeRate * Discharge) / (MathF.PI * Source.Velocity.Length()));
    public bool NozzleConnected => _sourcePoint >= 0 && Emission != LiquidEmission.Drops && Discharge > 0;
    public float Discharge => (float)(IntegrateInletVolume(Seconds, Seconds + StepSeconds) / (StepSeconds * Source.VolumeRate));
    public int Count { get; private set; }
    public double Seconds { get; private set; }
    public double InjectedVolume { get; private set; }
    public double EvaporatedVolume { get; private set; }
    public double AbsorbedVolume { get; private set; }
    public ReadOnlySpan<LiquidPoint> Points => _points.AsSpan(0, Count);
    public double Volume { get { double volume = 0; foreach (ref readonly var point in Points) volume += point.Volume; return volume; } }
    public Vector3 Momentum { get { Vector3 momentum = Vector3.Zero; foreach (ref readonly var point in Points) momentum += point.Velocity * (float)(Material.Density * point.Volume); return momentum; } }
    private readonly LiquidPoint[] _points = new LiquidPoint[Capacity];
    private int _nextNumber, _sourcePoint = -1;
    private long _sourceSample = -1;
    private double _sourceStartedSeconds;

    public LiquidFlow(int pointLimit = Capacity, string scene = "pour", float extent = 1,
        LiquidFluid fluid = LiquidFluid.Water, LiquidEmission? emission = null)
    {
        PointLimit = pointLimit; Scene = scene; Extent = extent;
        Fluid = fluid; Material = LiquidMaterial.For(fluid);
        Emission = emission ?? (fluid == LiquidFluid.Semen ? LiquidEmission.Pulses : LiquidEmission.Stream);
        Source = CreateSource(); Reset();
    }

    private LiquidSource CreateSource() => new(
        Scene == "curved" ? new(-1.35f, 2.4f, -.10f) : new(-1.15f, 2.55f, 0),
        (Scene == "curved" ? new Vector3(2.6f, -.9f, .05f) : new Vector3(.15f, -2.6f, 0)) *
            (Emission == LiquidEmission.Drops ? .18f : Fluid == LiquidFluid.Semen ? .85f : 1),
        Fluid == LiquidFluid.Semen ? 2e-6 : Fluid == LiquidFluid.Urine ? 12e-6 : 15e-6,
        SampleSeconds: Emission == LiquidEmission.Drops ? .125f : .05f);

    public void SetFluid(LiquidFluid fluid)
    {
        Fluid = fluid; Material = LiquidMaterial.For(fluid);
        Emission = fluid == LiquidFluid.Semen ? LiquidEmission.Pulses : LiquidEmission.Stream;
        Source = CreateSource(); Reset();
    }

    public void SetEmission(LiquidEmission emission) { Emission = emission; Source = CreateSource(); Reset(); }

    public void Reset()
    {
        Count = _nextNumber = 0; Seconds = InjectedVolume = EvaporatedVolume = AbsorbedVolume = 0;
        _sourcePoint = -1; _sourceSample = -1;
        _sourceStartedSeconds = 0;
        Array.Clear(_points); UpdateObstacles();
    }

    /// <summary>Add material with its position, velocity and volume; the inlet uses this same operation.</summary>
    public int Inject(Vector3 position, Vector3 velocity, double volume)
    {
        if (Count == PointLimit) ReduceSampling();
        int index = Count++;
        _points[index] = new()
        {
            Number = ++_nextNumber, Previous = -1, Position = position, Velocity = velocity,
            Volume = volume, Radius = MathF.Cbrt((float)(volume * 3 / (4 * Math.PI))),
            Width = MathF.Cbrt((float)(volume * 3 / (4 * Math.PI))), Axis = Vector3.UnitX,
            Disturbance = Source.RadiusVariation, Impact = new(0, 0, 0, 10),
        };
        InjectedVolume += volume;
        return index;
    }

    /// <summary>Place an existing surface volume with its footprint and tangential velocity.</summary>
    public int Place(Vector3 position, double volume, Vector2 radii, Vector3 velocity = default)
    {
        int index = Inject(position, velocity, volume);
        ref LiquidPoint point = ref _points[index];
        float distance = Distance(position, out Vector3 normal, out LiquidContactSurface surface);
        point.Position += normal * (ContactRadius - distance);
        point.Normal = normal; point.Surface = surface;
        point.Axis = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < .9f ? Vector3.UnitY : Vector3.UnitX));
        point.Velocity -= normal * Vector3.Dot(point.Velocity, normal);
        point.Radius = radii.X; point.Width = radii.Y;
        return index;
    }

    public void Step(float seconds = StepSeconds)
    {
        UpdateObstacles(); MergeSurfaceLiquid(); ExchangeStrandMomentum(seconds);
        for (int index = 0; index < Count; index++)
        {
            ref LiquidPoint point = ref _points[index];
            point.Age += seconds; point.Impact.W += seconds;
            Advance(ref point, seconds);
        }
        AbsorbIncomingLiquid(); MergeSurfaceLiquid(); RemoveSurfaceLiquid(seconds);
        Emit(Seconds, Seconds + seconds); Seconds += seconds;
    }

    /// <summary>Steps distinct flows and returns after every flow has finished.</summary>
    public static void Step(ReadOnlyMemory<LiquidFlow> flows, float seconds = StepSeconds) =>
        throw new NotImplementedException();

    private double IntegrateInletVolume(double from, double to)
    {
        if (!Pouring || from >= Source.Duration) return 0;
        to = Math.Min(to, Source.Duration);
        if (Emission != LiquidEmission.Pulses) return Source.VolumeRate * (to - from);
        double volume = 0;
        while (from < to)
        {
            double periodStart = Math.Floor(from / Source.PulsePeriod) * Source.PulsePeriod;
            double end = Math.Min(to, periodStart + Source.PulsePeriod);
            double a = Math.Min(from - periodStart, Source.PulseSeconds);
            double b = Math.Min(end - periodStart, Source.PulseSeconds);
            volume += Source.VolumeRate * Source.PulseSeconds / Math.PI *
                (Math.Cos(Math.PI * a / Source.PulseSeconds) - Math.Cos(Math.PI * b / Source.PulseSeconds));
            from = end;
        }
        return volume;
    }

    private void Emit(double from, double to)
    {
        if (Source.SurfaceRadius > 0)
        {
            double supplied = IntegrateInletVolume(from, to);
            if (supplied > 0) SupplySurfaceLiquid(supplied);
            return;
        }
        while (from < to)
        {
            long sample = (long)Math.Floor(from / Source.SampleSeconds);
            double end = Math.Min(to, (sample + 1) * (double)Source.SampleSeconds);
            double volume = IntegrateInletVolume(from, end);
            if (volume > 0)
            {
                float age = (float)((to - from + to - end) * .5);
                Vector3 position = Source.Position + Source.Velocity * age + .5f * Gravity * age * age;
                Vector3 velocity = Source.Velocity + Gravity * age;
                if (_sourcePoint >= 0 && _sourceSample == sample && _points[_sourcePoint].Surface == LiquidContactSurface.None)
                {
                    ref LiquidPoint point = ref _points[_sourcePoint];
                    point.Age = float.Lerp(point.Age, age, (float)(volume / (point.Volume + volume)));
                    AddVolume(ref point, position, velocity, volume); InjectedVolume += volume;
                }
                else
                {
                    if (Count == PointLimit) ReduceSampling();
                    int previous = _sourcePoint, index = Inject(position, velocity, volume);
                    _points[index].Previous = Emission == LiquidEmission.Drops ? -1 : previous;
                    _points[index].Age = age; _sourcePoint = index;
                    _sourceStartedSeconds = Emission == LiquidEmission.Pulses ? Math.Max(from, Math.Floor(end / Source.PulsePeriod) * Source.PulsePeriod) : from;
                }
            }
            else CloseInlet();
            _sourceSample = sample; from = end;
        }
    }

    private void SupplySurfaceLiquid(double volume)
    {
        Distance(Source.Position, out Vector3 normal, out LiquidContactSurface surface);
        for (int index = 0; index < Count; index++)
        {
            ref LiquidPoint point = ref _points[index];
            if (point.Surface != surface) continue;
            Vector3 delta = Source.Position - point.Position;
            float along = Vector3.Dot(delta, point.Axis) / point.Radius;
            float across = Vector3.Dot(delta, Vector3.Cross(point.Normal, point.Axis)) / point.Width;
            if (along * along + across * across > 1) continue;
            Vector3 centre = point.Position;
            AddVolume(ref point, Source.Position, Source.Velocity - normal * Vector3.Dot(Source.Velocity, normal), volume);
            point.Position = centre;
            point.Velocity -= point.Normal * Vector3.Dot(point.Velocity, point.Normal);
            InjectedVolume += volume;
            return;
        }
        Place(Source.Position, volume, new(Source.SurfaceRadius), Source.Velocity);
    }

    private void CloseInlet()
    {
        // The final partial inlet cell joins its preceding cell when the prescribed flux ends.
        if (_sourcePoint >= 0)
        {
            int previous = _points[_sourcePoint].Previous;
            if (previous >= 0 && _points[_sourcePoint].Surface == LiquidContactSurface.None &&
                _points[previous].Surface == LiquidContactSurface.None)
                Merge(Math.Min(previous, _sourcePoint), Math.Max(previous, _sourcePoint));
        }
        _sourcePoint = -1;
    }

    private static void AddVolume(ref LiquidPoint point, Vector3 position, Vector3 velocity, double volume)
    {
        float fraction = (float)(volume / (point.Volume + volume));
        point.Position = Vector3.Lerp(point.Position, position, fraction);
        point.Velocity = Vector3.Lerp(point.Velocity, velocity, fraction);
        point.Volume += volume;
        if (point.Surface == LiquidContactSurface.None)
            point.Radius = point.Width = MathF.Cbrt((float)(point.Volume * 3 / (4 * Math.PI)));
    }

    private void ExchangeStrandMomentum(float seconds)
    {
        for (int index = 0; index < Count; index++)
        {
            ref LiquidPoint point = ref _points[index];
            // The receiving inlet cell is an open volume; its momentum comes from the prescribed flux.
            if (index == _sourcePoint) continue;
            if (point.Previous < 0) continue;
            ref LiquidPoint previous = ref _points[point.Previous];
            if (point.Surface != LiquidContactSurface.None && previous.Surface != LiquidContactSurface.None) continue;
            Vector3 delta = point.Position - previous.Position;
            float length = delta.Length();
            if (length < ContactRadius) continue;
            Vector3 direction = delta / length;
            double volume = .5 * (point.Volume + previous.Volume);
            float area = (float)(volume / length), radius = MathF.Sqrt(area / MathF.PI);
            if (length <= 2 * radius && point.Surface == previous.Surface)
            {
                int first = Math.Min(index, point.Previous), second = Math.Max(index, point.Previous);
                Merge(first, second); index--; continue;
            }
            if (point.Surface == LiquidContactSurface.None && previous.Surface == LiquidContactSurface.None)
            {
                float k2 = .5f / (radius * radius);
                float growth2 = Material.SurfaceTension / (8 * Material.Density * radius * radius * radius);
                float low = 0, high = MathF.Sqrt(growth2);
                if (growth2 > 0)
                {
                    for (int iteration = 0; iteration < 16; iteration++)
                    {
                        float growth = .5f * (low + high);
                        float relaxation = 1 + Material.RelaxationSeconds * growth;
                        float viscous = 3 * k2 / Material.Density;
                        float value = growth * growth + viscous * growth *
                            (Material.Viscosity + Material.Elasticity * Material.RelaxationSeconds / relaxation) - growth2;
                        if (value > 0) high = growth; else low = growth;
                    }
                }
                float amplitude = MathF.Log(point.Disturbance) + .5f * (low + high) * seconds;
                if (amplitude >= 0)
                { point.Previous = -1; point.Stress = point.Strain = point.Disturbance = 0; continue; }
                point.Disturbance = MathF.Exp(amplitude);
            }
            float mass = (float)(point.Volume * Material.Density), otherMass = (float)(previous.Volume * Material.Density);
            float reducedMass = mass * otherMass / (mass + otherMass);
            float extension = Vector3.Dot(point.Velocity - previous.Velocity, direction);
            float decay = MathF.Exp(-seconds / Material.RelaxationSeconds);
            float elasticDrag = 3 * Material.Elasticity * Material.RelaxationSeconds * (1 - decay) * area / length;
            float tension = MathF.PI * Material.SurfaceTension * radius + area * point.Stress * decay;
            float viscosity = 3 * Material.Viscosity * area / length;
            float nextExtension = (extension - seconds * tension / reducedMass) / (1 + seconds * (viscosity + elasticDrag) / reducedMass);
            point.Stress = point.Stress * decay + elasticDrag / area * nextExtension;
            point.Strain += nextExtension / length * seconds;
            float impulse = reducedMass * (extension - nextExtension);
            point.Velocity -= direction * (impulse / mass); previous.Velocity += direction * (impulse / otherMass);
        }
    }

    private void ResolveContact(ref LiquidPoint point)
    {
        float distance = Distance(point.Position, out Vector3 normal, out LiquidContactSurface surface);
        float incoming = Vector3.Dot(point.Velocity, normal);
        if (distance <= ContactRadius + 1e-5f)
        {
            bool landing = point.Surface == LiquidContactSurface.None;
            point.Position += normal * (ContactRadius - distance);
            if (landing && incoming > 0)
            {
                point.Normal = Vector3.Zero; ContainInTray(ref point); return;
            }
            point.Velocity -= normal * incoming;
            if (landing)
            {
                point.Radius = point.Width = MathF.Cbrt((float)(point.Volume * 3 / (4 * Math.PI)));
                point.RadialVelocity = point.WidthVelocity = MathF.Sqrt(3) * Math.Max(0, -incoming);
                point.Impact = new(Math.Max(0, -incoming), point.Radius, 0, 0);
            }
            point.Normal = normal; point.Surface = surface;
            point.Axis = landing ? Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < .9f ? Vector3.UnitY : Vector3.UnitX)) :
                Vector3.Normalize(point.Axis - normal * Vector3.Dot(point.Axis, normal));
        }
        else { point.Surface = LiquidContactSurface.None; point.Normal = Vector3.Zero; point.RadialVelocity = point.WidthVelocity = 0; }
        ContainInTray(ref point);
    }

    private static float ContactReach(in LiquidPoint point, Vector3 direction)
    {
        float along = point.Radius * Vector3.Dot(point.Axis, direction);
        float across = point.Width * Vector3.Dot(Vector3.Cross(point.Normal, point.Axis), direction);
        return MathF.Sqrt(along * along + across * across);
    }

    private void AbsorbIncomingLiquid()
    {
        for (int index = 0; index < Count; index++)
        {
            LiquidPoint incoming = _points[index];
            if (incoming.Surface == LiquidContactSurface.None || incoming.Impact.W != 0) continue;
            for (int other = 0; other < Count; other++)
            {
                ref LiquidPoint film = ref _points[other];
                if (film.Surface != incoming.Surface || film.Impact.W == 0) continue;
                Vector3 delta = incoming.Position - film.Position;
                float along = Vector3.Dot(delta, film.Axis) / film.Radius;
                float across = Vector3.Dot(delta, Vector3.Cross(film.Normal, film.Axis)) / film.Width;
                if (along * along + across * across > 1) continue;
                // Deposition inside a wetted footprint adds height and momentum. Its contact line
                // subsequently moves through the same capillary and hysteresis equations.
                Vector3 centre = film.Position;
                AddVolume(ref film, incoming.Position, incoming.Velocity, incoming.Volume);
                film.Position = centre;
                film.Velocity -= film.Normal * Vector3.Dot(film.Velocity, film.Normal);
                Remove(index--, other);
                break;
            }
        }
    }

    private void MergeSurfaceLiquid()
    {
        for (int index = 0; index < Count; index++)
        for (int other = index + 1; other < Count; other++)
        {
            ref LiquidPoint a = ref _points[index]; ref LiquidPoint b = ref _points[other];
            if (a.Surface == LiquidContactSurface.None || a.Surface != b.Surface || Vector3.Dot(a.Normal, b.Normal) < .95f) continue;
            Vector3 delta = b.Position - a.Position;
            float distance = delta.Length();
            if (distance > 0 && distance > ContactReach(a, delta / distance) + ContactReach(b, delta / distance)) continue;
            Merge(index, other--);
        }
    }

    private void ReduceSampling()
    {
        int first = 0, second = 1; float smallest = float.PositiveInfinity;
        for (int index = 0; index < Count; index++)
        for (int other = index + 1; other < Count; other++)
        {
            ref LiquidPoint a = ref _points[index]; ref LiquidPoint b = ref _points[other];
            if (a.Surface != b.Surface) continue;
            float error = (float)(a.Volume * b.Volume / (a.Volume + b.Volume)) *
                (Vector3.DistanceSquared(a.Position, b.Position) + Source.SampleSeconds * Source.SampleSeconds * Vector3.DistanceSquared(a.Velocity, b.Velocity));
            if (error < smallest) { smallest = error; first = index; second = other; }
        }
        Merge(first, second);
    }

    private void Merge(int first, int second)
    {
        ref LiquidPoint a = ref _points[first]; LiquidPoint b = _points[second];
        if (a.Previous == second) a.Previous = b.Previous;
        float fraction = (float)(b.Volume / (a.Volume + b.Volume));
        if (a.Surface != LiquidContactSurface.None && a.Surface == b.Surface) MergeFootprints(ref a, b, fraction);
        a.Age = float.Lerp(a.Age, b.Age, fraction);
        AddVolume(ref a, b.Position, b.Velocity, b.Volume);
        if (a.Surface != b.Surface) { a.Surface = LiquidContactSurface.None; a.Normal = Vector3.Zero; }
        if (a.Surface == LiquidContactSurface.None)
            a.Radius = a.Width = MathF.Cbrt((float)(a.Volume * 3 / (4 * Math.PI)));
        else
        {
            ContactDistance(a.Position, a.Surface, out float distance, out Vector3 normal);
            a.Position += normal * (ContactRadius - distance); a.Normal = normal;
            a.Axis = Vector3.Normalize(a.Axis - normal * Vector3.Dot(a.Axis, normal));
            a.Velocity -= normal * Vector3.Dot(a.Velocity, normal);
        }
        Remove(second, first);
    }

    private static void MergeFootprints(ref LiquidPoint a, in LiquidPoint b, float fraction)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Lerp(a.Normal, b.Normal, fraction));
        Vector3 axis = Vector3.Normalize(a.Axis - normal * Vector3.Dot(a.Axis, normal));
        Vector3 across = Vector3.Cross(normal, axis), delta = b.Position - a.Position;
        Vector3 aAcross = Vector3.Cross(a.Normal, a.Axis), bAcross = Vector3.Cross(b.Normal, b.Axis);
        double ax = Vector3.Dot(a.Axis, axis) * a.Radius, ay = Vector3.Dot(a.Axis, across) * a.Radius;
        double az = Vector3.Dot(aAcross, axis) * a.Width, aw = Vector3.Dot(aAcross, across) * a.Width;
        double bx = Vector3.Dot(b.Axis, axis) * b.Radius, by = Vector3.Dot(b.Axis, across) * b.Radius;
        double bz = Vector3.Dot(bAcross, axis) * b.Width, bw = Vector3.Dot(bAcross, across) * b.Width;
        double dx = Vector3.Dot(delta, axis), dy = Vector3.Dot(delta, across), separation = fraction * (1 - fraction);
        // Six times the volume-weighted second moment, including the separation of both centres.
        double xx = (1 - fraction) * (ax * ax + az * az) + fraction * (bx * bx + bz * bz) + 6 * separation * dx * dx;
        double yy = (1 - fraction) * (ay * ay + aw * aw) + fraction * (by * by + bw * bw) + 6 * separation * dy * dy;
        double xy = (1 - fraction) * (ax * ay + az * aw) + fraction * (bx * by + bz * bw) + 6 * separation * dx * dy;
        Vector3 relativeVelocity = b.Velocity - a.Velocity;
        double vx = Vector3.Dot(relativeVelocity, axis), vy = Vector3.Dot(relativeVelocity, across);
        double axRate = Vector3.Dot(a.Axis, axis) * a.RadialVelocity, ayRate = Vector3.Dot(a.Axis, across) * a.RadialVelocity;
        double azRate = Vector3.Dot(aAcross, axis) * a.WidthVelocity, awRate = Vector3.Dot(aAcross, across) * a.WidthVelocity;
        double bxRate = Vector3.Dot(b.Axis, axis) * b.RadialVelocity, byRate = Vector3.Dot(b.Axis, across) * b.RadialVelocity;
        double bzRate = Vector3.Dot(bAcross, axis) * b.WidthVelocity, bwRate = Vector3.Dot(bAcross, across) * b.WidthVelocity;
        double xxRate = 2 * ((1 - fraction) * (ax * axRate + az * azRate) + fraction * (bx * bxRate + bz * bzRate)) + 12 * separation * dx * vx;
        double yyRate = 2 * ((1 - fraction) * (ay * ayRate + aw * awRate) + fraction * (by * byRate + bw * bwRate)) + 12 * separation * dy * vy;
        double xyRate = (1 - fraction) * (axRate * ay + ax * ayRate + azRate * aw + az * awRate) +
            fraction * (bxRate * by + bx * byRate + bzRate * bw + bz * bwRate) + 6 * separation * (vx * dy + dx * vy);
        double angle = .5 * Math.Atan2(2 * xy, xx - yy), discriminant = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        a.Radius = (float)Math.Sqrt(.5 * (xx + yy + discriminant));
        a.Width = (float)Math.Sqrt(.5 * (xx + yy - discriminant));
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        a.RadialVelocity = (float)((xxRate * cosine * cosine + 2 * xyRate * sine * cosine + yyRate * sine * sine) / (2 * a.Radius));
        a.WidthVelocity = (float)((xxRate * sine * sine - 2 * xyRate * sine * cosine + yyRate * cosine * cosine) / (2 * a.Width));
        a.Axis = axis * (float)cosine + across * (float)sine; a.Normal = normal;
    }

    private void RemoveSurfaceLiquid(float seconds)
    {
        for (int index = 0; index < Count; index++)
        {
            ref LiquidPoint point = ref _points[index];
            if (point.Surface == LiquidContactSurface.None) continue;
            double evaporated = Math.Min(point.Volume, 4 * (double)Evaporation.Diffusivity * Evaporation.VaporDensityDifference * Math.Sqrt((double)point.Radius * point.Width) / Material.Density * seconds);
            double absorbed = Math.Min(point.Volume - evaporated, Wetting.AbsorptionSpeed * Math.PI * point.Radius * point.Width * seconds);
            EvaporatedVolume += evaporated; AbsorbedVolume += absorbed; point.Volume -= evaporated + absorbed;
            if (point.Volume == 0) Remove(index--, -1);
        }
    }

    private void Remove(int index, int replacement)
    {
        for (int other = 0; other < Count; other++)
        {
            int previous = _points[other].Previous;
            if (previous == index) previous = replacement;
            if (previous > index) previous--;
            _points[other].Previous = previous;
        }
        if (_sourcePoint == index) _sourcePoint = replacement;
        if (_sourcePoint > index) _sourcePoint--;
        Array.Copy(_points, index + 1, _points, index, Count - index - 1); _points[--Count] = default;
    }
}
