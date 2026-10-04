using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class TextEditingFunctions
{
    /// <summary>Declares editable text and focus data, preserving the node's existing values.</summary>
    public static Entity TextEditing(Entity node)
    {
        FocusFunctions.Focusable(node);
        Scope.Ensure<EditableText>(node);
        return node;
    }

    /// <summary>Consumes all new committed edits and returns the current text.</summary>
    public static bool Edited(this Entity node, out string text)
    {
        EditableText value = Scope.Read<EditableText>(node);
        text = value.Text ?? string.Empty;
        ref EditSeen seen = ref Scope.Write<EditSeen>(node);
        bool edited = value.Edits != seen.Count;
        seen.Count = value.Edits;
        return edited;
    }
}
