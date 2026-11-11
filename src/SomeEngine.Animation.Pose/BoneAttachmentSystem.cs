using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Consumes completed bone poses before ordinary entity transform propagation.</summary>
public struct BoneAttachmentSystem : ISystem<ImmediateSystemContext>
{
    private Query _attachments;
    private List<Entity> _released;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _released = [];
        _attachments = context.World.Query(new QueryDescription()
            .WithStored<BoneAttachment>().WithStored<WorldTransform>().WithStored<LocalTransform>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _released.Clear();
        {
            using QueryScope read = _attachments.Open();
            Visit(in read, ref _released);

            static void Visit(scoped in QueryScope rows, ref List<Entity> released)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var values0 = chunk.Has<LocalTransform>() ? chunk.Write<LocalTransform>() : default;
                    chunk.TryRead<BoneAttachment>(out var values1);
                    chunk.TryRead<WorldTransform>(out var values2);
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {

                        BoneAttachment attachment = values1[rowIndex];
                        if (attachment.Skeleton.IsDisposed || attachment.Bone.Instance.IsDisposed)
                        {
                            values0[rowIndex].Value = values2[rowIndex].Qvvs;
                            released.Add(chunk.Entities[rowIndex]);
                        }
                        else
                            values0[rowIndex].Value = attachment.ReadLocalTransform();

                    }
                }

            }
        }
        foreach (Entity attachment in _released)
        {
            context.World.Remove<BoneAttachment>(attachment);
            Hierarchy.Detach(context.World, attachment);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
