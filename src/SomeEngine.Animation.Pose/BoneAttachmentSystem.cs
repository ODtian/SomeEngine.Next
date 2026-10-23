using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Consumes completed bone poses before ordinary entity transform propagation.</summary>
public struct BoneAttachmentSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _attachments;
    private List<Entity> _released;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _released = [];
        _attachments = context.World.Query(context.World.QueryDefinition()
            .Read<BoneAttachment>().Read<WorldTransform>().ReadWrite<LocalTransform>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _released.Clear();
        context.World.ExecuteQuery(_attachments, ref _released,
            static (QueryCursor rows, ref List<Entity> released) =>
            {
                foreach (QueryRow row in rows.Rows)
                {
                    BoneAttachment attachment = row.Read<BoneAttachment>();
                    if (attachment.Skeleton.IsDisposed || attachment.Bone.Instance.IsDisposed)
                    {
                        row.ReadWrite<LocalTransform>().Value = row.Read<WorldTransform>().Qvvs;
                        released.Add(row.Entity);
                    }
                    else
                        row.ReadWrite<LocalTransform>().Value = attachment.ReadLocalTransform();
                }
            });
        foreach (Entity attachment in _released)
        {
            context.World.Remove<BoneAttachment>(attachment);
            Hierarchy.Detach(context.World, attachment);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_attachments);
}
