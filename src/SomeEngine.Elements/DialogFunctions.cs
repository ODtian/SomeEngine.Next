using SomeEngine.Views;
using static SomeEngine.Elements.DialogTitleFunctions;

namespace SomeEngine.Elements;

public static partial class DialogFunctions
{
    /// <summary>Opens a dialog with its title already declared; dispose the returned scope after adding content.</summary>
    public static ParentScope TitledDialog(string title)
    {
        ParentScope content = Dialog().Content();
        DialogTitle(title);
        return content;
    }
}
