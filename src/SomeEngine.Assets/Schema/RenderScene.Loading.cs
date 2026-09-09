namespace SomeEngine.Assets.Schema;

internal sealed partial class SceneData
{
    internal IReadOnlyList<Guid> GetDependencies(string path)
    {
        Validate(this, path);
        return Dependencies(this);
    }

    private static IReadOnlyList<Guid> Dependencies(SceneData scene)
    {
        var values = new HashSet<Guid>();
        foreach (SceneMeshInstance instance in scene.MeshInstances ?? [])
        {
            values.Add(RequireGuid(instance.MeshGuid, nameof(SceneMeshInstance.MeshGuid)));
            foreach (string material in instance.MaterialGuids ?? [])
                values.Add(RequireGuid(material, nameof(SceneMeshInstance.MaterialGuids)));
        }

        Guid[] result = [.. values];
        Array.Sort(result, static (left, right) => left.CompareTo(right));
        return result;
    }

    private static bool IsMeshDependency(SceneData scene, Guid dependency)
    {
        foreach (SceneMeshInstance instance in scene.MeshInstances ?? [])
        {
            if (RequireGuid(instance.MeshGuid, nameof(SceneMeshInstance.MeshGuid)) == dependency)
                return true;
        }
        return false;
    }

    private static void Validate(SceneData scene, string path = "")
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Camera is null)
            throw new InvalidDataException($"Render scene '{path}' has no camera.");
        RequireVector(scene.Camera.Position, nameof(SceneCamera.Position));
        RequireVector(scene.Camera.Target, nameof(SceneCamera.Target));
        RequireVector(scene.Camera.Up, nameof(SceneCamera.Up));
        if (!(scene.Camera.VerticalFieldOfView > 0.0f) ||
            !(scene.Camera.NearPlane > 0.0f) ||
            !(scene.Camera.FarPlane > scene.Camera.NearPlane))
        {
            throw new InvalidDataException($"Render scene '{path}' has an invalid camera projection.");
        }

        foreach (SceneMeshInstance instance in scene.MeshInstances ?? [])
        {
            _ = RequireGuid(instance.MeshGuid, nameof(SceneMeshInstance.MeshGuid));
            if (instance.MaterialGuids is not { Count: > 0 })
                throw new InvalidDataException($"Render scene '{path}' has a mesh instance without materials.");
            foreach (string material in instance.MaterialGuids)
                _ = RequireGuid(material, nameof(SceneMeshInstance.MaterialGuids));
            RequireVector(instance.Position, nameof(SceneMeshInstance.Position));
            RequireVector(instance.Scale, nameof(SceneMeshInstance.Scale));
            if (instance.Rotation is null)
                throw new InvalidDataException($"Render scene '{path}' has a mesh instance without rotation.");
        }
    }

    private static Guid RequireGuid(string? value, string field)
    {
        if (!global::System.Guid.TryParse(value, out Guid guid) || (guid == System.Guid.Empty))
            throw new InvalidDataException($"Render scene field '{field}' has invalid asset GUID '{value}'.");
        return guid;
    }

    private static void RequireVector(SceneVector3? value, string field)
    {
        if (value is null ||
            !float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z))
        {
            throw new InvalidDataException($"Render scene field '{field}' has an invalid vector.");
        }
    }
}
