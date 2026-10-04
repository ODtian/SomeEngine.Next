using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A caller's text editing behavior composed from public data and scope functions.</summary>
public static class TextEditingFunctions
{
    public static Entity TextEditing(Entity node)
    {
        Scope.Ensure<Focusable>(node);
        Scope.Ensure<Clicks>(node);
        Scope.Ensure<EditableText>(node);
        return node;
    }

    public static bool Edited(Entity node, out string text)
    {
        EditableText value = Scope.Read<EditableText>(node);
        text = value.Text ?? string.Empty;
        ref EditSeen seen = ref Scope.Write<EditSeen>(node);
        bool edited = value.Edits != seen.Count;
        seen.Count = value.Edits;
        return edited;
    }
}
