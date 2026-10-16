using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using Texture = SomeEngine.Resources.Texture;

internal sealed partial class ClusterBlendScene(ClusterBlendAssets assets, string effect) : IDisposable
{
    private readonly List<Material> _materials = [];
    private readonly List<(Entity Entity, TransformQvvs Local)> _objects = [];

    internal string Effect => effect;
    internal string Workload => effect switch
    {
        "glass" => "32 intersecting glass panels",
        "smoke" => "8 smoke clouds with 16 cards each",
        "foliage" => "64 leaf clusters with 3 crossed cards each",
        _ => effect,
    };

    public void Initialize(SceneEdit edit, TestScene scene)
    {
        Dispose();
        switch (effect)
        {
            case "glass": AddGlass(edit); break;
            case "smoke": AddSmoke(edit); break;
            case "foliage": AddFoliage(edit); break;
            default: throw new ArgumentOutOfRangeException(nameof(effect));
        }
        Update(edit, scene, 0);
    }

    public void Update(SceneEdit edit, TestScene scene, int frame)
    {
        Matrix4x4.Invert(edit.CameraView, out Matrix4x4 camera);
        Quaternion rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationY(MathF.PI) * camera);
        TransformQvvs root = new(camera.Translation, rotation);
        foreach (var item in _objects)
            edit.SetEntityTransform(item.Entity, TransformQvvs.Combine(root, item.Local));
    }

    private void AddGlass(SceneEdit edit)
    {
        StandardMaterial[] glass = [Standard(new(.35f, .65f, 1, .12f)), Standard(new(1, .5f, .25f, .12f)),
            Standard(new(.4f, 1, .6f, .12f)), Standard(new(.8f, .45f, 1, .12f))];
        for (int group = 0; group < 4; group++)
        for (int layer = 0; layer < 8; layer++)
        {
            Vector3 position = new((group % 2 - .5f) * 1.35f, (group / 2 - .5f) * .9f, 2.1f + layer * .15f);
            Quaternion tilt = Quaternion.CreateFromYawPitchRoll((layer % 2 == 0 ? 1 : -1) * .35f, (group - 1.5f) * .08f, 0);
            Add(edit, assets.Panel, glass[layer % glass.Length], new(position, tilt, .65f));
        }
    }

    private void AddSmoke(SceneEdit edit)
    {
        UnlitMaterial smoke = Unlit(new(.65f, .7f, .8f, .04f), assets.Smoke);
        for (int cloud = 0; cloud < 8; cloud++)
        for (int layer = 0; layer < 16; layer++)
        {
            float angle = layer * 2.4f;
            Vector3 position = new((cloud % 4 - 1.5f) * .65f + MathF.Cos(angle) * .08f,
                (cloud / 4 - .5f) * .7f + MathF.Sin(angle) * .08f, 2.3f + layer * .055f);
            Add(edit, assets.Panel, smoke, new(position, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle), .47f));
        }
    }

    private void AddFoliage(SceneEdit edit)
    {
        UnlitMaterial leaf = Unlit(new(.8f, 1, .75f, .6f), assets.Leaf);
        for (int patch = 0; patch < 64; patch++)
        for (int card = 0; card < 3; card++)
        {
            Vector3 position = new((patch % 8 - 3.5f) * .25f, (patch / 8 - 3.5f) * .17f, 2.5f + patch % 3 * .12f);
            TransformQvvs local = new(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (card - 1) * MathF.PI / 3), .18f)
                { Stretch = new(1, 1.5f, 1) };
            Add(edit, assets.Panel, leaf, local);
        }
    }

    private Entity Add(SceneEdit edit, Mesh mesh, Material material, TransformQvvs local)
    {
        Entity entity = edit.AddEntity(mesh, [material], local);
        _objects.Add((entity, local));
        return entity;
    }

    private StandardMaterial Standard(Vector4 color)
    {
        StandardMaterial source = assets.Standard;
        var material = new StandardMaterial
        {
            Shader = source.Shader, AlbedoMap = source.AlbedoMap, NormalMap = source.NormalMap, ARMMap = source.ARMMap,
            MaterialSampler = source.MaterialSampler, BaseColorTint = color, MetallicFactor = .1f,
            Roughness = .2f, EmissiveFactor = new Vector3(color.X, color.Y, color.Z) * .04f,
            State = source.State with { Surface = SurfaceMode.Translucent, TwoSided = true },
        };
        _materials.Add(material);
        return material;
    }

    private UnlitMaterial Unlit(Vector4 color, Texture texture)
    {
        UnlitMaterial source = assets.Unlit;
        var material = new UnlitMaterial
        {
            Shader = source.Shader, AlbedoMap = texture, MaterialSampler = source.MaterialSampler, BaseColorTint = color,
            State = source.State with { Surface = SurfaceMode.Translucent, TwoSided = true },
        };
        _materials.Add(material);
        return material;
    }

    public void Dispose()
    {
        foreach (Material material in _materials) material.Dispose();
        _materials.Clear();
        _objects.Clear();
    }
}
