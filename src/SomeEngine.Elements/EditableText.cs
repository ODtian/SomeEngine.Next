using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Committed text, UTF-16 caret and selection anchor, and displayed IME composition.</summary>
public struct EditableText : IComponent
{
    /// <summary>The committed string; default strings represent empty text.</summary>
    public string Text;
    /// <summary>The active selection end, at a complete text element's UTF-16 boundary.</summary>
    public int Caret;
    /// <summary>The selection anchor, at a complete text element's UTF-16 boundary.</summary>
    public int SelectionStart;
    /// <summary>The displayed IME candidate string, replacing the selection's presentation.</summary>
    public string Composition;
    /// <summary>The platform's UTF-16 caret within Composition.</summary>
    public int CompositionCaret;
    /// <summary>A wrapping count of committed content changes.</summary>
    public uint Edits;
}

/// <summary>The edit count consumed by the caller's last Edited read.</summary>
public struct EditSeen : IComponent
{
    public uint Count;
}

/// <summary>Text input removes CR and LF, retaining a single editable line.</summary>
public struct SingleLineText : IComponent { }
