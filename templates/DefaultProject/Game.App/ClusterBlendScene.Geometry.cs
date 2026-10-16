using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.Resources;
using SomeEngine.Runtime;

internal sealed partial class ClusterBlendScene
{
    private readonly List<(int Index, TransformQvvs Origin)> _curvedParts = [];

    private void AddDeformedPhong(SceneEdit edit)
    {
        _curvedParts.Clear();
        UnlitMaterial source = assets.DeformedPhong;
        for (int group = 0; group < 4; group++)
        {
            var material = new UnlitMaterial
            {
                Shader = source.Shader, AlbedoMap = source.AlbedoMap, MaterialSampler = source.MaterialSampler,
                BaseColorTint = new(.2f + group * .2f, .8f - group * .15f, 1, .35f),
                State = source.State with { Surface = SurfaceMode.Translucent, TwoSided = true },
            };
            _materials.Add(material);
            for (int layer = 0; layer < 4; layer++)
            {
                TransformQvvs transform = new(new Vector3((group % 2 - .5f) * .9f,
                    (group / 2 - .5f) * .7f, 2.4f + layer * .2f), Quaternion.Identity, .35f);
                _curvedParts.Add((_objects.Count, transform));
                Add(edit, assets.Part, material, transform);
            }
        }
    }

    private void AnimateDeformedPhong(int frame)
    {
        if (effect != "deformed-phong") return;
        foreach (var part in _curvedParts)
        {
            TransformQvvs transform = part.Origin;
            transform.Position += new Vector3(MathF.Sin(frame / 60f + part.Index * .3f) * .15f, 0, 0);
            transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, frame / 120f + part.Index * .2f);
            _objects[part.Index] = (_objects[part.Index].Entity, transform);
        }
    }
}
