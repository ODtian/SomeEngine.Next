using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.Runtime;
using SomeEngine.Resources;

internal sealed partial class ClusterBlendScene
{
    private readonly List<(int Index, TransformQvvs Origin)> _decals = [];

    private void AddDecalLayers(SceneEdit edit)
    {
        _decals.Clear();
        StandardMaterial wall = Standard(new(.25f, .3f, .35f, 1));
        wall.State = wall.State with { Surface = SurfaceMode.Opaque };
        StandardMaterial glass = Standard(new(.5f, .75f, 1, .25f));
        var layers = new StandardMaterial[16];
        for (int layer = 0; layer < layers.Length; layer++)
        {
            layers[layer] = Standard(new(.25f + layer % 3 * .3f, .7f - layer % 4 * .13f, .2f + layer % 5 * .14f, .18f));
            layers[layer].AlbedoMap = assets.Smoke;
            layers[layer].NormalMap = assets.CoatingNormal;
            layers[layer].Roughness = .1f + layer * .05f;
        }
        for (int group = 0; group < 16; group++)
        {
            Vector2 center = new((group % 4 - 1.5f) * .62f, (group / 4 - 1.5f) * .4f);
            Add(edit, assets.Panel, wall, new(new Vector3(center, 3.5f), Quaternion.Identity, .28f) { Stretch = new(1, .7f, 1) });
            for (int plane = 0; plane < 3; plane++)
                Add(edit, assets.Panel, glass, new(new Vector3(center, 2.5f + plane * .22f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, (plane - 1) * .12f), .26f) { Stretch = new(1, .7f, 1) });
            for (int layer = 0; layer < layers.Length; layer++)
            {
                TransformQvvs volume = new(new Vector3(center, 3.0f),
                    Quaternion.CreateFromYawPitchRoll(MathF.PI, 0, (layer - 7.5f) * .025f), .22f) { Stretch = new(1, .72f, 3.2f) };
                AddDecalVolume(edit, layers[layer], volume, layer);
            }
        }
    }

    private void AddDecalSparse(SceneEdit edit)
    {
        _decals.Clear();
        StandardMaterial wall = Standard(new(.45f, .48f, .5f, 1));
        wall.State = wall.State with { Surface = SurfaceMode.Opaque };
        Add(edit, assets.Panel, wall, new(new Vector3(0, 0, 3.6f), Quaternion.Identity, 1.2f) { Stretch = new(1, .8f, 1) });
        Add(edit, assets.Panel, Standard(new(.5f, .75f, 1, .2f)),
            new(new Vector3(-.3f, 0, 2.8f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .15f), .65f));
        StandardMaterial hole = Standard(new(.12f, .06f, .025f, .85f));
        hole.AlbedoMap = assets.Smoke; hole.NormalMap = assets.CoatingNormal; hole.Roughness = .9f;
        for (int index = 0; index < 64; index++)
        {
            Vector3 position = new((index % 8 - 3.5f) * .26f, (index / 8 - 3.5f) * .18f, 3.6f);
            TransformQvvs volume = new(position, Quaternion.CreateFromYawPitchRoll(MathF.PI, 0, index * .7f), .065f)
                { Stretch = new(1, .9f, 2) };
            AddDecalVolume(edit, hole, volume, index);
        }
    }

    private void AddDecalVolume(SceneEdit edit, StandardMaterial material, TransformQvvs local, int order)
    {
        var entity = edit.AddMaterialDecal(material, local, order);
        _decals.Add((_objects.Count, local));
        _objects.Add((entity, local));
    }

    private void AnimateDecals(int frame)
    {
        if (effect is not ("decal-layers" or "decal-sparse")) return;
        float amplitude = effect == "decal-layers" ? .018f : .006f;
        foreach (var decal in _decals)
        {
            TransformQvvs transform = decal.Origin;
            transform.Position += new Vector3(MathF.Sin(frame / 60f + decal.Index * .2f) * amplitude, 0, 0);
            _objects[decal.Index] = (_objects[decal.Index].Entity, transform);
        }
    }
}
