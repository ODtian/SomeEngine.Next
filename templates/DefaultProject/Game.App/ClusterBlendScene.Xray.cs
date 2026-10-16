using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using SomeEngine.Runtime;

internal sealed partial class ClusterBlendScene
{
    private readonly List<(int Index, TransformQvvs Origin)> _xrayParts = [];

    private void AddXray(SceneEdit edit)
    {
        _xrayParts.Clear();
        var rules = new Xray(new XrayBlend(.8f), new XrayBlend(new Vector3(.7f, .9f, 1), .45f), 0, 8);
        Material[] materials = [Standard(new(.9f, .35f, .15f, 1)), Standard(new(.2f, .6f, 1, 1)),
            Standard(new(.6f, .9f, .35f, 1)), Standard(new(.8f, .5f, .9f, 1))];
        foreach (Material material in materials)
            material.State = material.State with { Surface = SurfaceMode.Opaque, TwoSided = false };
        for (int index = 0; index < 64; index++)
        {
            TransformQvvs transform = new(new Vector3((index % 8 - 3.5f) * .23f,
                (index / 8 - 3.5f) * .18f, 2.5f + index % 4 * .13f),
                Quaternion.CreateFromYawPitchRoll(index * .23f, index * .17f, 0), .1f);
            _xrayParts.Add((_objects.Count, transform));
            edit.SetXray(Add(edit, assets.Part, materials[index % materials.Length], transform), rules);
        }
        Material panel = Standard(new(.2f, .24f, .3f, 1));
        panel.State = panel.State with { Surface = SurfaceMode.Opaque };
        Add(edit, assets.Panel, panel, new(new Vector3(-.35f, 0, 2.15f), Quaternion.Identity, .65f)
            { Stretch = new(1, 1.3f, 1) });
    }

    private void AnimateXray(int frame)
    {
        if (effect != "xray") return;
        foreach (var part in _xrayParts)
        {
            TransformQvvs transform = part.Origin;
            transform.Position += new Vector3(MathF.Sin(frame / 90f + part.Index * .2f) * .2f, 0, 0);
            transform.Rotation = Quaternion.CreateFromYawPitchRoll(frame / 150f + part.Index * .23f,
                part.Index * .17f, 0);
            _objects[part.Index] = (_objects[part.Index].Entity, transform);
        }
    }
}
