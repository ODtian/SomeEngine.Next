using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public sealed class ClothingSimulation
{
    public ClothingMesh Mesh { get; }
    public Vector3[] Positions { get; }
    public Vector3[] Velocities { get; }
    public Vector3[] Targets { get; }
    public float[] SimulationWeights { get; }
    public float[] Stretch { get; }
    public float Progress { get; private set; }
    public int SimulatedVertices { get; private set; }
    public int ReleasedVertices { get; private set; }
    public float MaximumStretch { get; private set; }
    private readonly Vector3[] previousPositions;
    private readonly Vector3[] previousTargets;
    private readonly Vector3[] attachmentCorrections;
    private readonly Vector3[] attachmentTargets;
    private readonly float[] attachmentScales;
    private readonly float[] inverseMasses;
    private readonly float[] edgeLengths;
    private readonly float[] bendLengths;
    private readonly float[] edgeForces;
    private readonly float[] bendForces;
    private readonly float[] edgeScales;
    private readonly float[] bendScales;
    private readonly int[] simulatedVertices;
    private readonly int[] activeEdges;
    private readonly int[] activeBends;
    private readonly ClothingContact[] contacts;
    private readonly int[] bodyContactVertices;
    private readonly bool[] bodyContactRequired;
    private int bodyContactCount;
    private readonly int[] activeTriangles;
    private int activeTriangleCount;
    private readonly List<FootTriangle> footTriangles = [];
    private readonly record struct FootTriangle(int A, int B, int C, FootHull Foot, int Plane);
    private readonly List<BodyTriangle> bodyTriangles = [];
    private readonly List<SurfaceCrossing> bodyCrossings = [];
    private readonly record struct BodyTriangle(int A, int B, int C, SurfaceContact Contact);
    private readonly int[] fittedFootTriangles;
    private int fittedFootTriangleCount;
    private int activeEdgeCount;
    private int activeBendCount;
    private bool weightsChanged = true;

    public ClothingSimulation(ClothingMesh mesh)
    {
        Mesh = mesh;
        Positions = mesh.Vertices.ToArray();
        Velocities = new Vector3[Positions.Length];
        Targets = mesh.Vertices.ToArray();
        SimulationWeights = new float[Positions.Length];
        Stretch = new float[Positions.Length];
        previousPositions = new Vector3[Positions.Length];
        previousTargets = mesh.Vertices.ToArray();
        attachmentCorrections = new Vector3[Positions.Length];
        attachmentTargets = new Vector3[Positions.Length];
        attachmentScales = new float[Positions.Length];
        inverseMasses = new float[Positions.Length];
        edgeLengths = mesh.Edges.Select(edge => Vector3.Distance(Positions[edge.A], Positions[edge.B])).ToArray();
        bendLengths = mesh.Bends.Select(edge => Vector3.Distance(Positions[edge.A], Positions[edge.B])).ToArray();
        edgeForces = new float[edgeLengths.Length];
        bendForces = new float[bendLengths.Length];
        edgeScales = new float[edgeLengths.Length];
        bendScales = new float[bendLengths.Length];
        simulatedVertices = new int[Positions.Length];
        activeEdges = new int[edgeLengths.Length];
        activeBends = new int[bendLengths.Length];
        contacts = new ClothingContact[Positions.Length];
        bodyContactVertices = new int[Positions.Length];
        bodyContactRequired = new bool[Positions.Length];
        activeTriangles = new int[mesh.Triangles.Length / 3];
        fittedFootTriangles = new int[mesh.Triangles.Length / 3];
    }

    public void Reset()
    {
        Mesh.Vertices.CopyTo(Positions, 0);
        Mesh.Vertices.CopyTo(Targets, 0);
        Mesh.Vertices.CopyTo(previousTargets, 0);
        Array.Clear(Velocities);
        Progress = 0;
        weightsChanged = true;
    }

    public void Place(TrajectoryField trajectory, bool dualQuaternion)
    {
        Reset();
        for (int i = 0; i < Positions.Length; i++)
        {
            TrajectorySample sample = trajectory.Sample(Mesh.Bindings[i], 0, dualQuaternion);
            Positions[i] = Targets[i] = previousTargets[i] = sample.Position;
        }
    }

    public void SampleTargets(TrajectoryField trajectory, float requestedProgress, float step, bool dualQuaternion)
    {
        float previousProgress = Progress;
        Progress += Math.Clamp(requestedProgress - Progress, -step * .23f, step * .23f);
        float travel = Progress * trajectory.TravelDistance;
        if (weightsChanged || previousProgress != Progress)
        {
            fittedFootTriangleCount = 0;
            for (int triangle = 0; triangle < Mesh.Triangles.Length; triangle += 3)
            {
                bool onFoot = false;
                for (int corner = 0; corner < 3; corner++)
                {
                    TrajectoryBinding binding = Mesh.Bindings[Mesh.Triangles[triangle + corner]];
                    float distance = binding.Distance - travel;
                    onFoot |= distance >= 0 && distance <= trajectory.FootDistance(binding.Surface);
                }
                if (onFoot) fittedFootTriangles[fittedFootTriangleCount++] = triangle;
            }
        }
        bool updateWeights = weightsChanged || previousProgress != Progress;
        for (int i = 0; i < Positions.Length; i++)
        {
            float previousWeight = SimulationWeights[i];
            previousTargets[i] = Targets[i];
            TrajectorySample sample = trajectory.Sample(Mesh.Bindings[i], travel, dualQuaternion);
            Targets[i] = sample.Position;
            float weight = sample.SimulationWeight < .003f ? 0 : sample.SimulationWeight;
            if (previousWeight == 0 && weight > 0) Velocities[i] = (Targets[i] - previousTargets[i]) / step;
            updateWeights |= (SimulationWeights[i] > 0) != (weight > 0);
            SimulationWeights[i] = weight;
            inverseMasses[i] = weight > 0 ? Mesh.InverseMasses[i] : 0;
        }
        if (!updateWeights) return;
        weightsChanged = false;
        SimulatedVertices = 0;
        ReleasedVertices = 0;
        for (int i = 0; i < Positions.Length; i++)
        {
            if (SimulationWeights[i] > 0) simulatedVertices[SimulatedVertices++] = i;
            if (Mesh.Bindings[i].Distance - travel < TrajectoryField.Bottom) ReleasedVertices++;
        }
        activeTriangleCount = 0;
        Array.Clear(bodyContactRequired);
        for (int triangle = 0; triangle < Mesh.Triangles.Length; triangle += 3)
        {
            if (SimulationWeights[Mesh.Triangles[triangle]] + SimulationWeights[Mesh.Triangles[triangle + 1]]
                + SimulationWeights[Mesh.Triangles[triangle + 2]] > 0)
            {
                activeTriangles[activeTriangleCount++] = triangle;
                for (int corner = 0; corner < 3; corner++) bodyContactRequired[Mesh.Triangles[triangle + corner]] = true;
            }
        }
        activeEdgeCount = SelectActiveEdges(Mesh.Edges, activeEdges);
        activeBendCount = SelectActiveEdges(Mesh.Bends, activeBends);
    }

    private int SelectActiveEdges((int A, int B)[] edges, int[] active)
    {
        int count = 0;
        for (int edge = 0; edge < edges.Length; edge++)
            if (SimulationWeights[edges[edge].A] + SimulationWeights[edges[edge].B] > 0) active[count++] = edge;
        return count;
    }

    public void Step(BodyMesh body, float seconds, float step, int iterations, float wind, float frameBlend)
    {
        float inverseStepSquared = 1 / (step * step);
        Array.Clear(edgeForces);
        Array.Clear(bendForces);
        Array.Clear(attachmentCorrections);
        CalculateDistanceScales(Mesh.Edges, edgeScales, activeEdges.AsSpan(0, activeEdgeCount), .0000004f * inverseStepSquared);
        CalculateDistanceScales(Mesh.Bends, bendScales, activeBends.AsSpan(0, activeBendCount), .0006f * inverseStepSquared);
        for (int i = 0; i < Positions.Length; i++)
        {
            previousPositions[i] = Positions[i];
            attachmentTargets[i] = Vector3.Lerp(previousTargets[i], Targets[i], frameBlend);
            float weight = SimulationWeights[i];
            attachmentScales[i] = 1 - weight;
            if (SimulationWeights[i] == 0)
            {
                Positions[i] = attachmentTargets[i];
                continue;
            }
            Vector3 acceleration = new(wind * (1 + .3f * MathF.Sin(seconds * 4 + i * .17f)), -9.81f,
                wind * .3f * MathF.Sin(seconds * 3));
            Positions[i] += Velocities[i] * step + acceleration * (step * step);
        }
        FindBodyContacts(body);
        FindBodyTriangles(body);
        FindFootTriangles(body);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            bool reverse = iteration % 2 != 0;
            SolveDistances(Mesh.Edges, edgeLengths, edgeForces, edgeScales, activeEdges, activeEdgeCount, .0000004f * inverseStepSquared, reverse);
            SolveDistances(Mesh.Bends, bendLengths, bendForces, bendScales, activeBends, activeBendCount, .0006f * inverseStepSquared, reverse);
            SolveTargets(simulatedVertices.AsSpan(0, SimulatedVertices));
            if (iteration == iterations - 1)
            {
                FindBodyContacts(body);
                FindBodyTriangles(body);
            }
            SolveContacts();
            SolveBodyTriangles();
            SolveFeet();
        }
        FindBodyTriangles(body);
        FindFootTriangles(body);
        for (int pass = 0; pass < 4; pass++) { SolveBodyTriangles(); SolveFeet(); }
        float damping = MathF.Exp(-3.2f * step);
        for (int i = 0; i < Positions.Length; i++)
        {
            Vector3 velocity = (Positions[i] - previousPositions[i]) / step;
            Velocities[i] = SimulationWeights[i] == 0 ? velocity : contacts[i].ProjectVelocity(Positions[i], velocity) * damping;
        }
    }

    private void CalculateDistanceScales((int A, int B)[] edges, float[] scales, ReadOnlySpan<int> active, float compliance)
    {
        foreach (int edge in active)
        {
            (int a, int b) = edges[edge];
            scales[edge] = 1 / (inverseMasses[a] + inverseMasses[b] + compliance);
        }
    }

    private void SolveTargets(ReadOnlySpan<int> vertices)
    {
        foreach (int i in vertices)
        {
            float scale = attachmentScales[i];
            if (scale > 0)
            {
                Vector3 correction = (attachmentTargets[i] - Positions[i]) * scale - attachmentCorrections[i] * (1 - scale);
                attachmentCorrections[i] += correction;
                Positions[i] += correction;
            }
        }
    }

    private void SolveContacts()
    {
        foreach (int i in bodyContactVertices.AsSpan(0, bodyContactCount))
            Positions[i] = contacts[i].Body.Project(Positions[i]);
        foreach (int i in simulatedVertices.AsSpan(0, SimulatedVertices))
            Positions[i].Y = MathF.Max(ClothingContact.GroundHeight, Positions[i].Y);
    }

    private void FindBodyContacts(BodyMesh body)
    {
        bodyContactCount = 0;
        for (int i = 0; i < Positions.Length; i++)
        {
            if (!bodyContactRequired[i]) continue;
            contacts[i] = body.FindContact(previousPositions[i], Positions[i], contacts[i].Body.Normal == Vector3.Zero ? -1 : contacts[i].Body.Triangle);
            if (SimulationWeights[i] > 0 && contacts[i].Body.Normal != Vector3.Zero) bodyContactVertices[bodyContactCount++] = i;
        }
    }

    private void FindBodyTriangles(BodyMesh body)
    {
        bodyTriangles.Clear();
        foreach (int triangle in activeTriangles.AsSpan(0, activeTriangleCount))
        {
            int a = Mesh.Triangles[triangle], b = Mesh.Triangles[triangle + 1], c = Mesh.Triangles[triangle + 2];
            if (contacts[a].Body.Normal == Vector3.Zero && contacts[b].Body.Normal == Vector3.Zero
                && contacts[c].Body.Normal == Vector3.Zero) continue;
            bodyCrossings.Clear();
            body.Surface.FindCrossings(Positions[a], Positions[b], Positions[c], bodyCrossings);
            SurfaceContact best = default;
            float separation = float.NegativeInfinity;
            foreach (SurfaceCrossing crossing in bodyCrossings)
                SelectPlane(body.FindContact(crossing.Triangle, crossing.MeshCoordinates).Body);
            if (best.Normal == Vector3.Zero && bodyCrossings.Count > 0)
            {
                SelectPlane(contacts[a].Body); SelectPlane(contacts[b].Body); SelectPlane(contacts[c].Body);
            }
            if (best.Normal != Vector3.Zero) bodyTriangles.Add(new(a, b, c, best));

            void SelectPlane(SurfaceContact contact)
            {
                if (contact.Normal == Vector3.Zero) return;
                float sa = contact.Separation(Positions[a]), sb = contact.Separation(Positions[b]), sc = contact.Separation(Positions[c]);
                if ((SimulationWeights[a] == 0 && sa < 0) || (SimulationWeights[b] == 0 && sb < 0)
                    || (SimulationWeights[c] == 0 && sc < 0)) return;
                float gap = MathF.Min(sa, MathF.Min(sb, sc));
                if (gap > separation) { separation = gap; best = contact; }
            }
        }
    }

    private void SolveBodyTriangles()
    {
        foreach (BodyTriangle triangle in bodyTriangles)
        {
            if (SimulationWeights[triangle.A] > 0) Positions[triangle.A] = triangle.Contact.Project(Positions[triangle.A]);
            if (SimulationWeights[triangle.B] > 0) Positions[triangle.B] = triangle.Contact.Project(Positions[triangle.B]);
            if (SimulationWeights[triangle.C] > 0) Positions[triangle.C] = triangle.Contact.Project(Positions[triangle.C]);
        }
    }

    private void FindFootTriangles(BodyMesh body)
    {
        footTriangles.Clear();
        foreach (int triangle in activeTriangles.AsSpan(0, activeTriangleCount))
        {
            int a = Mesh.Triangles[triangle], b = Mesh.Triangles[triangle + 1], c = Mesh.Triangles[triangle + 2];
            Vector3 minimum = Vector3.Min(Vector3.Min(previousPositions[a], Positions[a]),
                Vector3.Min(Vector3.Min(previousPositions[b], Positions[b]), Vector3.Min(previousPositions[c], Positions[c])));
            Vector3 maximum = Vector3.Max(Vector3.Max(previousPositions[a], Positions[a]),
                Vector3.Max(Vector3.Max(previousPositions[b], Positions[b]), Vector3.Max(previousPositions[c], Positions[c])));
            if (maximum.Y <= ClothingContact.GroundHeight) continue;
            foreach (FootHull foot in body.Feet)
                if (foot.Overlaps(minimum, maximum, .05f)) footTriangles.Add(new(a, b, c, foot, foot.ClosestTrianglePlane(Positions[a], Positions[b], Positions[c])));
        }
    }

    private void SolveFeet()
    {
        foreach (FootTriangle triangle in footTriangles)
            triangle.Foot.ProjectTriangle(ref Positions[triangle.A], ref Positions[triangle.B], ref Positions[triangle.C], triangle.Plane);
    }

    public void FitFeet(BodyMesh body)
    {
        foreach (int triangle in fittedFootTriangles.AsSpan(0, fittedFootTriangleCount))
        {
            int a = Mesh.Triangles[triangle], b = Mesh.Triangles[triangle + 1], c = Mesh.Triangles[triangle + 2];
            if (SimulationWeights[a] > 0 && SimulationWeights[b] > 0 && SimulationWeights[c] > 0) continue;
            foreach (FootHull foot in body.Feet) foot.FitTriangle(ref Positions[a], ref Positions[b], ref Positions[c]);
        }
        for (int i = 0; i < Positions.Length; i++) Positions[i].Y = MathF.Max(ClothingContact.GroundHeight, Positions[i].Y);
    }

    private void SolveDistances((int A, int B)[] edges, float[] lengths, float[] forces, float[] scales, int[] activeEdges, int count, float compliance, bool reverse)
    {
        for (int active = 0; active < count; active++)
        {
            int i = activeEdges[reverse ? count - 1 - active : active];
            (int a, int b) = edges[i];
            float wa = inverseMasses[a], wb = inverseMasses[b];
            Vector3 direction = Positions[b] - Positions[a];
            float length = direction.Length();
            if (length < .000001f) continue;
            float force = (length - lengths[i] - compliance * forces[i]) * scales[i];
            forces[i] += force;
            Vector3 correction = direction * (force / length);
            Positions[a] += wa * correction;
            Positions[b] -= wb * correction;
        }
    }

    public void MeasureStretch()
    {
        Array.Fill(Stretch, 1);
        MaximumStretch = 1;
        for (int edge = 0; edge < Mesh.Edges.Length; edge++)
        {
            (int a, int b) = Mesh.Edges[edge];
            float ratio = Vector3.Distance(Positions[a], Positions[b]) / edgeLengths[edge];
            Stretch[a] = MathF.Max(Stretch[a], ratio);
            Stretch[b] = MathF.Max(Stretch[b], ratio);
            MaximumStretch = MathF.Max(MaximumStretch, ratio);
        }
    }

}
