namespace SomeEngine.Assets.Schema;

public sealed partial class RenderScene
{
    internal static async ValueTask<RenderScene> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SomeEngine.Serialization.Containers.BinaryDocument<RenderScene> document = await context
            .OpenAsync<RenderScene>()
            .ConfigureAwait(false);
        RenderScene asset = document.Root;
        Validate(asset);

        foreach (AssetGuid dependency in Dependencies(asset))
        {
            if (IsMeshDependency(asset, dependency))
                _ = await context.LoadDependencyAsync(new AssetId<Mesh>(dependency)).ConfigureAwait(false);
            else
                _ = await context.LoadDependencyAsync(new AssetId<Material>(dependency)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return asset;
    }

    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        Validate(this, path);
        return Dependencies(this);
    }

    private static IReadOnlyList<AssetGuid> Dependencies(RenderScene scene)
    {
        var values = new HashSet<AssetGuid>();
        foreach (SceneMeshInstance instance in scene.MeshInstances ?? [])
        {
            values.Add(RequireGuid(instance.MeshGuid, nameof(SceneMeshInstance.MeshGuid)));
            foreach (string material in instance.MaterialGuids ?? [])
                values.Add(RequireGuid(material, nameof(SceneMeshInstance.MaterialGuids)));
        }

        AssetGuid[] result = [.. values];
        Array.Sort(result, static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    private static bool IsMeshDependency(RenderScene scene, AssetGuid dependency)
    {
        foreach (SceneMeshInstance instance in scene.MeshInstances ?? [])
        {
            if (RequireGuid(instance.MeshGuid, nameof(SceneMeshInstance.MeshGuid)) == dependency)
                return true;
        }
        return false;
    }

    private static void Validate(RenderScene scene, string path = "")
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

    private static AssetGuid RequireGuid(string? value, string field)
    {
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(value, out AssetGuid guid) || guid.IsEmpty)
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
