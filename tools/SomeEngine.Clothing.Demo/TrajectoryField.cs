using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public readonly record struct TrajectoryBinding(int Surface, float Around, float Distance, Vector3 Offset,
    float Across = 0, float SupportRadius = .012f, float ReferenceGapSquared = 0, float ReferenceSupportGapSquared = 0);
public readonly record struct TrajectorySample(Vector3 Position, float SimulationWeight);
public readonly record struct TrajectoryPoint(Vector3 Position, BoneInfluence[] BoneWeights, float SimulationWeight,
    int BodyTriangle, Vector3 BodyCoordinates);

public sealed class TrajectoryField
{
    public const int Columns = 256;
    public const int Rows = 448;
    public const int SurfaceCount = 2;
    public const float Bottom = 0;
    private const float AnkleHeight = .15f;
    private const int FootRows = 192;
    public const float BodyClearance = .001f;
    public Vector3[] Positions { get; } = new Vector3[Columns * Rows * SurfaceCount];
    public int[] Triangles { get; }
    public int[] TransportEdges { get; }
    public int[] SectionEdges { get; }
    public TrajectoryPoint[] Points { get; } = new TrajectoryPoint[Columns * Rows * SurfaceCount];
    public DualQuaternion[] Transforms { get; } = new DualQuaternion[Columns * Rows * SurfaceCount];
    public Matrix4x4[] Matrices { get; } = new Matrix4x4[Columns * Rows * SurfaceCount];
    public float TravelDistance { get; }
    private readonly Vector3[] skinTranslations = new Vector3[Columns * Rows * SurfaceCount];
    private readonly float[] footLengths = new float[SurfaceCount];
    private readonly float[] lengths = new float[SurfaceCount];
    private readonly CharacterAsset character;
    private readonly int crotchRow;
    private readonly float crotchHeight;

    public TrajectoryField(CharacterAsset character)
    {
        this.character = character;
        CharacterSurface body = character.Body;
        BodyMidline midline = new(character.Clothing);
        crotchHeight = character.CrotchHeight + BodyClearance;
        crotchRow = FootRows + (int)MathF.Round((crotchHeight - AnkleHeight)
            / (character.WaistHeight + .015f - AnkleHeight) * (Rows - 1 - FootRows));
        FootSections[] feet = [new(character.Body, 1), new(character.Body, -1)];
        SurfaceTriangles bodyTriangles = new(body.Positions, body.Triangles);
        int[][] lowerBodyFaces = CharacterAsset.FindLargestSurface(body.Faces
            .Where(face => face.All(vertex => body.Positions[vertex].Y < character.WaistHeight + .065f)).ToArray());
        int[] lowerBodyTriangles = CharacterAsset.Triangulate(lowerBodyFaces);
        SurfaceTriangles lowerBody = new(body.Positions, lowerBodyTriangles);
        SurfaceTriangles helperSurface = new(character.Clothing.Positions, character.Clothing.Triangles);
        Vector3[] positions = character.Clothing.Positions;
        int[][] bodyFaces = CharacterAsset.FindLargestSurface(character.Clothing.Faces
            .Where(face => face.All(index => positions[index].Y < character.WaistHeight + .065f)).ToArray());
        int[] guideTriangles = CharacterAsset.Triangulate(bodyFaces);
        int[][] guideFaces = Enumerable.Range(0, guideTriangles.Length / 3)
            .Select(triangle => guideTriangles.Skip(triangle * 3).Take(3).ToArray()).ToArray();
        Vector3 InterpolateGuideNormal(SurfacePoint point)
        {
            Vector3 normal = Vector3.Zero;
            for (int corner = 0; corner < 3; corner++)
                normal += character.Clothing.Normals[character.Clothing.Triangles[point.Triangle * 3 + corner]] * point.Coordinates[corner];
            return Vector3.Normalize(normal);
        }
        TrajectoryPoint CreatePoint(Vector3 position, float simulation)
        {
            SurfacePoint point = bodyTriangles.Nearest(position);
            Dictionary<int, float> weights = [];
            for (int corner = 0; corner < 3; corner++)
            foreach (BoneInfluence influence in body.BoneWeights[body.Triangles[point.Triangle * 3 + corner]])
                weights[influence.Bone] = weights.GetValueOrDefault(influence.Bone) + influence.Weight * point.Coordinates[corner];
            BoneInfluence[] influences = weights.OrderByDescending(value => value.Value)
                .Select(value => new BoneInfluence(value.Key, value.Value)).ToArray();
            float total = influences.Sum(value => value.Weight);
            for (int i = 0; i < influences.Length; i++) influences[i] = influences[i] with { Weight = influences[i].Weight / total };
            return new(position, influences, simulation, point.Triangle, point.Coordinates);
        }
        TrajectoryPoint BindPoint(SurfacePoint point, Vector3 direction, SurfaceTriangles contour, float side, float simulation = 0)
        {
            float DistanceSquared(Vector3 position)
            {
                float distance = bodyTriangles.Nearest(position).DistanceSquared;
                return ReferenceEquals(contour, bodyTriangles) ? distance : MathF.Min(distance, contour.Nearest(position).DistanceSquared);
            }
            float lower = 0, upper = BodyClearance, clearanceSquared = BodyClearance * BodyClearance;
            while (DistanceSquared(point.Position + direction * upper) < clearanceSquared) upper *= 2;
            for (int step = 0; step < 12; step++)
            {
                float distance = (lower + upper) * .5f;
                if (DistanceSquared(point.Position + direction * distance) < clearanceSquared) lower = distance;
                else upper = distance;
            }
            Vector3 position = point.Position + direction * upper;
            position.X = MathF.Abs(position.X) * side;
            return CreatePoint(position, simulation);
        }
        TrajectoryPoint FitPoint(Vector3 reference, Vector3 direction, SurfaceTriangles surface, float side)
        {
            SurfacePoint point = surface.IntersectSegment(reference + direction * .08f, reference - direction * .08f);
            if (point.Triangle < 0) point = surface.Nearest(reference);
            float gap = BodyMotion.Smooth(.010f, .026f, Vector3.Dot(reference - point.Position, direction));
            point = bodyTriangles.Nearest(point.Position);
            Vector3 a = body.Positions[body.Triangles[point.Triangle * 3]];
            Vector3 b = body.Positions[body.Triangles[point.Triangle * 3 + 1]];
            Vector3 c = body.Positions[body.Triangles[point.Triangle * 3 + 2]];
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), direction) < 0) direction = -direction;
            return BindPoint(point, direction, bodyTriangles, side, reference.Y < .2f ? 0 : gap);
        }
        for (int surface = 0; surface < 2; surface++)
        {
            float side = surface == 0 ? 1 : -1;
            int[] sideTriangles = Enumerable.Range(0, lowerBodyTriangles.Length / 3)
                .Where(triangle => Enumerable.Range(0, 3).All(corner => body.Positions[lowerBodyTriangles[triangle * 3 + corner]].X * side >= -.00001f))
                .SelectMany(triangle => lowerBodyTriangles.Skip(triangle * 3).Take(3)).ToArray();
            SurfaceTriangles bodySide = new(body.Positions, sideTriangles);
            float toeZ = body.Positions.Where(point => point.X * side > 0 && point.Y < .12f).Max(point => point.Z);
            FootSections.ToeCap toeCap = feet[surface].CreateToeCap(toeZ);
            float ankleZ = body.Positions.Where(point => point.X * side > 0 && point.Y > .14f && point.Y < .16f).Average(point => point.Z);
            footLengths[surface] = toeZ - ankleZ - .10f + .10f * MathF.PI * .5f;
            lengths[surface] = footLengths[surface] + character.WaistHeight + .015f - AnkleHeight;
            float[] coordinates = positions.Select(point => point.Y - AnkleHeight + footLengths[surface]).ToArray();
            Vector3[][] rings = new Vector3[Rows][];
            for (int row = 0; row < Rows; row++)
            {
                float distance = RowDistance(surface, row);
                rings[row] = row <= FootRows ? CreateFootRing(distance / footLengths[surface], side, feet[surface], toeCap, toeZ, ankleZ) : CreateRing(guideFaces, coordinates, distance, side);
            }
            float frontRoot = midline.FindDistance(rings[crotchRow][0]);
            float backRoot = midline.FindDistance(rings[crotchRow][Columns / 2]);
            float frontTravel = midline.MeasureTravel(frontRoot, midline.FindDistance(rings[^1][0]));
            float backTravel = midline.MeasureTravel(backRoot, midline.FindDistance(rings[^1][Columns / 2]));
            float crotchTravel = midline.MeasureTravel(backRoot, frontRoot);
            for (int row = crotchRow; row < Rows; row++)
            {
                float along = (float)(row - crotchRow) / (Rows - 1 - crotchRow);
                Vector3 frontShift = midline.Sample(frontRoot + frontTravel * along) - rings[row][0];
                Vector3 backShift = midline.Sample(backRoot + backTravel * along) - rings[row][Columns / 2];
                for (int column = 0; column <= Columns / 2; column++)
                {
                    float around = (float)column / (Columns / 2);
                    rings[row][column] += frontShift * (1 - around) * (1 - around) + backShift * around * around;
                }
            }
            for (int column = Columns / 2 + 1; column < Columns; column++)
                rings[crotchRow][column] = midline.Sample(backRoot + crotchTravel * (column - Columns / 2) / (Columns / 2));
            int thighRow = crotchRow - (int)MathF.Round(.08f / (crotchHeight - AnkleHeight) * (crotchRow - FootRows));
            Vector3[] thigh = rings[thighRow];
            int hipRow = crotchRow + (int)MathF.Round(.08f / (character.WaistHeight + .015f - crotchHeight) * (Rows - 1 - crotchRow));
            for (int column = 0; column <= Columns / 2; column++)
            {
                Vector3 start = thigh[column], crotch = rings[crotchRow][column], hip = rings[hipRow][column];
                Vector3 startTangent = (rings[thighRow + 1][column] - rings[thighRow - 1][column]) * .5f;
                Vector3 joinTangent = (hip - start) / (hipRow - thighRow);
                Vector3 endTangent = (rings[hipRow + 1][column] - rings[hipRow - 1][column]) * .5f;
                for (int row = thighRow + 1; row < crotchRow; row++)
                    rings[row][column] = InterpolateCurve(start, crotch, startTangent * (crotchRow - thighRow),
                        joinTangent * (crotchRow - thighRow), (float)(row - thighRow) / (crotchRow - thighRow));
                for (int row = crotchRow + 1; row < hipRow; row++)
                    rings[row][column] = InterpolateCurve(crotch, hip, joinTangent * (hipRow - crotchRow),
                        endTangent * (hipRow - crotchRow), (float)(row - crotchRow) / (hipRow - crotchRow));
            }
            for (int row = thighRow + 1; row < crotchRow; row++)
            {
                float along = (float)(row - thighRow) / (crotchRow - thighRow);
                for (int column = Columns / 2 + 1; column < Columns; column++)
                    rings[row][column] = Vector3.Lerp(thigh[column], rings[crotchRow][column], along);
            }
            for (int row = 0; row < Rows; row++)
            {
                Vector3 middle = rings[row].Aggregate(Vector3.Zero, (sum, point) => sum + point) / Columns;
                for (int column = 0; column < Columns; column++)
                {
                    int index = Index(surface, column, row);
                    bool seam = row >= crotchRow && (column == 0 || column == Columns / 2)
                        || row == crotchRow && column > Columns / 2;
                    if (surface == 1 && seam)
                    {
                        Points[index] = Points[Index(0, column, row)];
                        continue;
                    }
                    if (seam)
                    {
                        SurfacePoint attachment = midline.Nearest(rings[row][column]);
                        Vector3 normal = InterpolateGuideNormal(attachment);
                        normal.X = 0;
                        Points[index] = FitPoint(attachment.Position, Vector3.Normalize(normal), lowerBody, 0);
                        continue;
                    }
                    if (row == 0)
                    {
                        SurfacePoint tip = toeCap.Surface.Nearest(toeCap.Tip);
                        Points[index] = column == 0 ? BindPoint(tip, toeCap.Normal(tip), toeCap.Surface, side) : Points[Index(surface, 0, 0)];
                        continue;
                    }
                    if (row > 0 && RowDistance(surface, row) < FootSections.ToeLength)
                    {
                        SurfacePoint attachment = toeCap.Surface.Nearest(rings[row][column]);
                        Points[index] = BindPoint(attachment, toeCap.Normal(attachment), toeCap.Surface, side);
                        continue;
                    }
                    if (row > FootRows)
                    {
                        SurfacePoint attachment = helperSurface.Nearest(rings[row][column]);
                        Points[index] = FitPoint(attachment.Position, InterpolateGuideNormal(attachment), bodySide, side);
                        continue;
                    }
                    Vector3 footDirection = Vector3.Normalize(rings[row][column] - middle);
                    Points[index] = FitPoint(rings[row][column], footDirection, bodySide, side);
                }
            }
        }
        List<int> triangles = [], transportEdges = [], sectionEdges = [];
        int Vertex(int surface, int column, int row)
        {
            column %= Columns;
            if (row == 0) column = 0;
            if (row >= crotchRow && (column == 0 || column == Columns / 2 || row == crotchRow && column > Columns / 2)) surface = 0;
            return Index(surface, column, row);
        }
        HashSet<(int, int)> edges = [];
        void AddEdge(List<int> target, int a, int b)
        {
            if (a == b || !edges.Add(a < b ? (a, b) : (b, a))) return;
            target.Add(a); target.Add(b);
        }
        for (int surface = 0; surface < SurfaceCount; surface++)
        for (int row = 0; row < Rows - 1; row++)
        for (int column = 0; column < (row >= crotchRow ? Columns / 2 : Columns); column++)
        {
            int a = Vertex(surface, column, row), b = Vertex(surface, column + 1, row);
            int c = Vertex(surface, column + 1, row + 1), d = Vertex(surface, column, row + 1);
            if (surface == 0)
            {
                if (a != b) triangles.AddRange([a, b, c]);
                triangles.AddRange([a, c, d]);
            }
            else
            {
                if (a != b) triangles.AddRange([a, c, b]);
                triangles.AddRange([a, d, c]);
            }
            AddEdge(transportEdges, a, d);
            AddEdge(transportEdges, b, c);
            AddEdge(sectionEdges, a, b);
            AddEdge(sectionEdges, d, c);
        }
        Triangles = triangles.ToArray();
        TransportEdges = transportEdges.ToArray();
        SectionEdges = sectionEdges.ToArray();
        for (int i = 0; i < Points.Length; i++)
        {
            Positions[i] = Points[i].Position;
            Transforms[i] = DualQuaternion.FromRotationTranslation(Quaternion.Identity, Points[i].Position);
            Matrices[i] = Matrix4x4.CreateTranslation(Points[i].Position);
        }
        TravelDistance = lengths.Max() + .04f;
    }

    private static Vector3 InterpolateCurve(Vector3 start, Vector3 end, Vector3 startTangent, Vector3 endTangent, float along)
    {
        float square = along * along, cube = square * along;
        return start * (2 * cube - 3 * square + 1) + startTangent * (cube - 2 * square + along)
            + end * (-2 * cube + 3 * square) + endTangent * (cube - square);
    }

    private static Vector3[] CreateFootRing(float along, float side, FootSections foot, FootSections.ToeCap toeCap, float toeZ, float ankleZ)
    {
        const float radius = .10f;
        float bendZ = ankleZ + radius, straight = toeZ - bendZ;
        float travel = along * (straight + radius * MathF.PI * .5f);
        if (travel < FootSections.ToeLength)
        {
            if (travel == 0) return Enumerable.Repeat(toeCap.Tip, Columns).ToArray();
            return FootSections.CreateRing(toeCap.Positions, toeCap.Triangles, new(0, .05f, toeZ - travel), Vector3.UnitY, side, Columns);
        }
        float angle = MathF.Max(0, travel - straight) / radius;
        Vector3 center = travel <= straight ? new(0, AnkleHeight - radius, toeZ - travel)
            : new(0, AnkleHeight - radius * MathF.Cos(angle), bendZ - radius * MathF.Sin(angle));
        Vector3 front = new(0, MathF.Cos(angle), MathF.Sin(angle));
        return foot.CreateRing(center, front, side, Columns);
    }

    private Vector3[] CreateRing(int[][] faces, float[] coordinates, float distance, float side)
    {
        Vector3[] positions = character.Clothing.Positions;
        Dictionary<(int, int), int> intersections = [];
        List<Vector3> points = [];
        List<List<int>> neighbours = [];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            int[] face = faces[faceIndex];
            List<int> crossings = [];
            for (int edge = 0; edge < face.Length; edge++)
            {
                int a = face[edge], b = face[(edge + 1) % face.Length];
                float da = coordinates[a] - distance, db = coordinates[b] - distance;
                if ((da < 0) == (db < 0)) continue;
                var key = da == 0 ? (a, a) : db == 0 ? (b, b) : a < b ? (a, b) : (b, a);
                if (!intersections.TryGetValue(key, out int vertex))
                {
                    vertex = points.Count;
                    intersections.Add(key, vertex);
                    points.Add(Vector3.Lerp(positions[a], positions[b], da / (da - db)));
                    neighbours.Add([]);
                }
                crossings.Add(vertex);
            }
            crossings = crossings.Distinct().ToList();
            if (crossings.Count < 2) continue;
            int first = crossings[0], last = crossings[1];
            float firstSide = points[first].X * side, lastSide = points[last].X * side;
            if (firstSide <= 0 && lastSide <= 0) continue;
            if ((firstSide < 0) != (lastSide < 0))
            {
                Vector3 crossing = Vector3.Lerp(points[first], points[last], firstSide / (firstSide - lastSide));
                crossing.X = 0;
                int endpoint = points.Count;
                points.Add(crossing); neighbours.Add([]);
                if (firstSide < 0) first = endpoint; else last = endpoint;
            }
            if (!neighbours[first].Contains(last)) neighbours[first].Add(last);
            if (!neighbours[last].Contains(first)) neighbours[last].Add(first);
        }
        List<Vector3> contour = [];
        bool closed = false;
        float largestArea = 0;
        HashSet<int> visited = [];
        for (int first = 0; first < points.Count; first++)
        {
            if (neighbours[first].Count == 0 || !visited.Add(first)) continue;
            List<int> component = [first];
            for (int i = 0; i < component.Count; i++)
            foreach (int adjacent in neighbours[component[i]])
                if (visited.Add(adjacent)) component.Add(adjacent);
            int start = component.FirstOrDefault(vertex => neighbours[vertex].Count == 1, -1);
            bool loop = start < 0;
            if (loop) start = first;
            List<Vector3> section = [];
            int current = start, previous = -1;
            do
            {
                section.Add(points[current]);
                List<int> links = neighbours[current];
                if (links.Count == 1 && links[0] == previous) break;
                int next = links[0] == previous ? links[1] : links[0];
                previous = current; current = next;
            } while (current != start);
            float area = 0;
            for (int i = 0; i < section.Count; i++)
            {
                Vector3 a = section[i], b = section[(i + 1) % section.Count];
                area += a.X * b.Z - a.Z * b.X;
            }
            if (MathF.Abs(area) <= largestArea) continue;
            largestArea = MathF.Abs(area); contour = section; closed = loop;
        }
        Vector2 origin = closed
            ? new((contour.Min(point => point.X) + contour.Max(point => point.X)) * .5f,
                (contour.Min(point => point.Z) + contour.Max(point => point.Z)) * .5f)
            : new(0, (contour[0].Z + contour[^1].Z) * .5f);
        float front = 0, outer = MathF.PI, inner = MathF.PI;
        if (closed)
        {
            float merge = BodyMotion.Smooth(crotchHeight - .12f, crotchHeight,
                distance - footLengths[side > 0 ? 0 : 1] + AnkleHeight);
            front = -MathF.PI * .5f * merge;
            outer += inner * merge;
            inner *= 1 - merge;
        }
        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
        Vector3[] ring = new Vector3[Columns];
        float areaSign = 0;
        for (int point = 0; point < contour.Count; point++)
        {
            Vector3 a = contour[point], b = contour[(point + 1) % contour.Count];
            areaSign += a.X * b.Z - a.Z * b.X;
        }
        if (closed && areaSign * side > 0) contour.Reverse();
        float[] contourDistances = new float[contour.Count + (closed ? 1 : 0)];
        for (int point = 1; point < contour.Count; point++)
            contourDistances[point] = contourDistances[point - 1] + Vector3.Distance(contour[point - 1], contour[point]);
        if (closed) contourDistances[^1] = contourDistances[^2] + Vector3.Distance(contour[^1], contour[0]);
        Vector3 AlongContour(float distance)
        {
            int vertex = Array.BinarySearch(contourDistances, distance);
            if (vertex >= 0) return contour[vertex % contour.Count];
            int segment = ~vertex;
            return Vector3.Lerp(contour[segment - 1], contour[segment % contour.Count],
                (distance - contourDistances[segment - 1]) / (contourDistances[segment] - contourDistances[segment - 1]));
        }
        (Vector3 Position, float Distance) AtAngle(float angle)
        {
            Vector2 direction = new(MathF.Sin(angle) * side, MathF.Cos(angle));
            float radius = float.PositiveInfinity, pathDistance = 0;
            for (int edge = 0; edge < contour.Count; edge++)
            {
                Vector3 first = contour[edge], last = contour[(edge + 1) % contour.Count];
                Vector2 a = new Vector2(first.X, first.Z) - origin, b = new Vector2(last.X, last.Z) - origin;
                Vector2 difference = b - a;
                float denominator = Cross(direction, difference);
                if (MathF.Abs(denominator) < 1e-8f) continue;
                float ray = Cross(a, difference) / denominator, across = Cross(a, direction) / denominator;
                if (ray < 0 || across < 0 || across > 1 || ray >= radius) continue;
                radius = ray;
                pathDistance = float.Lerp(contourDistances[edge], contourDistances[edge + 1], across);
            }
            Vector2 point = origin + direction * radius;
            return (new(point.X, contour[0].Y, point.Y), pathDistance);
        }
        float firstDistance = 0, outerDistance = 0;
        if (closed)
        {
            firstDistance = AtAngle(front).Distance;
            float lastDistance = AtAngle(front + outer).Distance;
            outerDistance = (lastDistance - firstDistance + contourDistances[^1]) % contourDistances[^1];
            if (inner == 0) outerDistance = contourDistances[^1];
        }
        for (int column = 0; column < Columns; column++)
        {
            float around = (float)column / Columns * 2;
            if (!closed && column <= Columns / 2)
            {
                float along = (contour[0].Z > contour[^1].Z ? around : 1 - around) * contourDistances[^1];
                ring[column] = AlongContour(along);
                continue;
            }
            if (!closed && column > Columns / 2)
            {
                Vector3 first = contour[0].Z > contour[^1].Z ? contour[^1] : contour[0];
                Vector3 last = contour[0].Z > contour[^1].Z ? contour[0] : contour[^1];
                ring[column] = Vector3.Lerp(first, last, around - 1);
                continue;
            }
            float angle = around <= 1 ? front + outer * around : front + outer + inner * (around - 1);
            float distanceOnContour = firstDistance + (around <= 1 ? outerDistance * around
                : outerDistance + (contourDistances[^1] - outerDistance) * (around - 1));
            float blend = BodyMotion.Smooth(crotchHeight - .12f, crotchHeight,
                distance - footLengths[side > 0 ? 0 : 1] + AnkleHeight);
            ring[column] = Vector3.Lerp(AtAngle(angle).Position, AlongContour(distanceOnContour % contourDistances[^1]), blend);
        }
        return ring;
    }

    public void Update(BodyMotion body, BodyMesh geometry, bool dualQuaternion = true)
    {
        for (int i = 0; i < Points.Length; i++)
        {
            TrajectoryPoint point = Points[i];
            Vector3 reference = Vector3.Zero, attachment = Vector3.Zero;
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = character.Body.Triangles[point.BodyTriangle * 3 + corner];
                reference += character.Body.Positions[vertex] * point.BodyCoordinates[corner];
                attachment += geometry.Positions[vertex] * point.BodyCoordinates[corner];
            }
            Vector3 offset = point.Position - reference;
            if (dualQuaternion)
            {
                DualQuaternion transform = body.Blend(point.BoneWeights);
                Vector3 position = attachment + Vector3.Transform(offset, transform.Rotation);
                Transforms[i] = DualQuaternion.FromRotationTranslation(transform.Rotation, position) * transform.Real.Length();
            }
            Matrix4x4 skinMatrix = body.BlendMatrix(point.BoneWeights);
            Vector3 matrixPosition = attachment + Vector3.TransformNormal(offset, skinMatrix);
            skinTranslations[i] = matrixPosition - Vector3.TransformNormal(point.Position, skinMatrix);
            skinMatrix.Translation = matrixPosition;
            Matrices[i] = skinMatrix;
            Positions[i] = dualQuaternion ? Transforms[i].Transform(Vector3.Zero) : skinMatrix.Translation;
        }
    }

    private void Weights(TrajectoryBinding binding, float distance, out int a, out int b, out int c, out int d, out Vector4 weights)
    {
        float horizontal = (binding.Around - MathF.Floor(binding.Around)) * Columns;
        float footLength = footLengths[binding.Surface];
        float crotchDistance = DistanceAtHeight(crotchHeight);
        float vertical = distance < footLength ? MathF.Acos(1 - distance / footLength) / (MathF.PI * .5f) * FootRows
            : distance < crotchDistance ? FootRows + (distance - footLength) / (crotchDistance - footLength) * (crotchRow - FootRows)
            : crotchRow + (distance - crotchDistance) / (lengths[binding.Surface] - crotchDistance) * (Rows - 1 - crotchRow);
        int column = (int)horizontal, row = Math.Min((int)vertical, Rows - 2);
        float across = horizontal - column, along = vertical - row;
        a = Index(binding.Surface, column, row); b = Index(binding.Surface, (column + 1) % Columns, row);
        c = a + Columns; d = b + Columns;
        weights = new((1 - across) * (1 - along), across * (1 - along), (1 - across) * along, across * along);
    }

    public TrajectorySample Sample(TrajectoryBinding binding, float travel, bool dualQuaternion)
    {
        float distance = binding.Distance - travel;
        bool released = distance < Bottom || distance > lengths[binding.Surface];
        binding = binding with { Distance = Math.Clamp(distance, Bottom, lengths[binding.Surface]) };
        if (binding.Across == 0)
        {
            TrajectorySample sample = SampleSide(binding, dualQuaternion);
            return sample with { SimulationWeight = released ? 1 : MathF.Max(sample.SimulationWeight,
                Math.Clamp(binding.ReferenceGapSquared / (binding.SupportRadius * binding.SupportRadius), 0, 1)) };
        }
        Weights(binding, binding.Distance, out int a, out int b, out int c, out int d, out Vector4 weights);
        Vector3 firstReference = ReferencePosition(a, b, c, d, weights);
        float firstWeight = SimulationWeight(a, b, c, d, weights);
        DualQuaternion firstTransform = dualQuaternion ? BlendTransform(a, b, c, d, weights) : default;
        Matrix4x4 firstMatrix = BlendMatrix(a, b, c, d, weights);
        Vector3 firstTranslation = BlendSkinTranslation(a, b, c, d, weights);
        var other = binding with { Surface = 1 - binding.Surface };
        Weights(other, other.Distance, out a, out b, out c, out d, out weights);
        Vector3 secondReference = ReferencePosition(a, b, c, d, weights);
        float secondWeight = SimulationWeight(a, b, c, d, weights);
        float across = binding.Across;
        Vector3 reference = Vector3.Lerp(firstReference, secondReference, across) + binding.Offset;
        Matrix4x4 secondMatrix = BlendMatrix(a, b, c, d, weights);
        Matrix4x4 matrix = firstMatrix * (1 - across) + secondMatrix * across;
        Vector3 firstMean = Vector3.TransformNormal(reference, firstMatrix) + firstTranslation;
        Vector3 secondMean = Vector3.TransformNormal(reference, secondMatrix) + BlendSkinTranslation(a, b, c, d, weights);
        Vector3 position;
        if (dualQuaternion)
        {
            DualQuaternion secondTransform = BlendTransform(a, b, c, d, weights);
            if (Quaternion.Dot(firstTransform.Real, secondTransform.Real) < 0) secondTransform *= -1;
            position = (firstTransform * (1 - across) + secondTransform * across).Transform(binding.Offset);
        }
        else position = Vector3.Transform(binding.Offset, matrix);
        float spreadSquared = across * (1 - across) * Vector3.DistanceSquared(firstMean, secondMean);
        float referenceGapSquared = MathF.Min(Vector3.DistanceSquared(reference, firstReference), Vector3.DistanceSquared(reference, secondReference));
        float gapChange = MathF.Max(0, referenceGapSquared - binding.ReferenceSupportGapSquared);
        float release = Math.Clamp((gapChange + spreadSquared + binding.ReferenceGapSquared)
            / (binding.SupportRadius * binding.SupportRadius), 0, 1);
        return new(position, released ? 1 : MathF.Max(float.Lerp(firstWeight, secondWeight, across), release));
    }

    public TrajectoryBinding BindAcross(TrajectoryBinding binding, float across)
    {
        Weights(binding, binding.Distance, out int a, out int b, out int c, out int d, out Vector4 weights);
        Vector3 first = ReferencePosition(a, b, c, d, weights);
        Vector3 point = first + binding.Offset;
        var other = binding with { Surface = 1 - binding.Surface };
        Weights(other, other.Distance, out a, out b, out c, out d, out weights);
        Vector3 second = ReferencePosition(a, b, c, d, weights);
        return binding with { Across = across, Offset = point - Vector3.Lerp(first, second, across) };
    }

    public TrajectoryBinding BindSupport(TrajectoryBinding binding, float referenceGap = 0)
    {
        Weights(binding, binding.Distance, out int a, out int b, out int c, out int d, out Vector4 weights);
        Vector3 first = ReferencePosition(a, b, c, d, weights);
        var other = binding with { Surface = 1 - binding.Surface };
        Weights(other, other.Distance, out a, out b, out c, out d, out weights);
        Vector3 second = ReferencePosition(a, b, c, d, weights);
        Vector3 point = Vector3.Lerp(first, second, binding.Across) + binding.Offset;
        return binding with { ReferenceGapSquared = referenceGap * referenceGap,
            ReferenceSupportGapSquared = MathF.Min(Vector3.DistanceSquared(point, first), Vector3.DistanceSquared(point, second)) };
    }

    private float SimulationWeight(int a, int b, int c, int d, Vector4 weights)
        => Points[a].SimulationWeight * weights.X + Points[b].SimulationWeight * weights.Y
            + Points[c].SimulationWeight * weights.Z + Points[d].SimulationWeight * weights.W;

    private Vector3 BlendSkinTranslation(int a, int b, int c, int d, Vector4 weights)
        => skinTranslations[a] * weights.X + skinTranslations[b] * weights.Y + skinTranslations[c] * weights.Z + skinTranslations[d] * weights.W;

    private Matrix4x4 BlendMatrix(int a, int b, int c, int d, Vector4 weights)
        => Matrices[a] * weights.X + Matrices[b] * weights.Y + Matrices[c] * weights.Z + Matrices[d] * weights.W;

    private TrajectorySample SampleSide(TrajectoryBinding binding, bool dualQuaternion)
    {
        Weights(binding, binding.Distance, out int a, out int b, out int c, out int d, out Vector4 weights);
        float weight = SimulationWeight(a, b, c, d, weights);
        Vector3 position;
        if (dualQuaternion) position = BlendTransform(a, b, c, d, weights).Transform(binding.Offset);
        else
        {
            Matrix4x4 transform = BlendMatrix(a, b, c, d, weights);
            position = Vector3.Transform(binding.Offset, transform);
        }
        return new(position, weight);
    }

    public float FootDistance(int surface) => footLengths[surface];
    public float DistanceAtHeight(float height) => height - AnkleHeight + footLengths[0];
    private Vector3 ReferencePosition(int a, int b, int c, int d, Vector4 weights)
        => Points[a].Position * weights.X + Points[b].Position * weights.Y + Points[c].Position * weights.Z + Points[d].Position * weights.W;
    private DualQuaternion BlendTransform(int a, int b, int c, int d, Vector4 weights)
    {
        DualQuaternion first = Transforms[a], second = Transforms[b], third = Transforms[c], fourth = Transforms[d];
        if (Quaternion.Dot(first.Real, second.Real) < 0) second *= -1;
        if (Quaternion.Dot(first.Real, third.Real) < 0) third *= -1;
        if (Quaternion.Dot(first.Real, fourth.Real) < 0) fourth *= -1;
        return first * weights.X + second * weights.Y + third * weights.Z + fourth * weights.W;
    }
    public float RowDistance(int surface, int row) => row <= FootRows ? footLengths[surface] * (1 - MathF.Cos((float)row / FootRows * MathF.PI * .5f))
        : row <= crotchRow ? DistanceAtHeight(crotchHeight)
            - (crotchHeight - AnkleHeight) * (crotchRow - row) / (crotchRow - FootRows)
        : DistanceAtHeight(crotchHeight)
            + (character.WaistHeight + .015f - crotchHeight) * (row - crotchRow) / (Rows - 1 - crotchRow);
    public static int Index(int surface, int column, int row) => (surface * Rows + row) * Columns + column;
}
