using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Windshot.Editing;

/// <summary>
/// A Grid whose mouse cursor can be set from outside. WinUI only exposes the cursor to
/// subclasses (<c>ProtectedCursor</c>), so the editor canvas is hosted in this.
/// </summary>
public sealed partial class CursorHost : Grid
{
    public InputCursor? Cursor
    {
        get => ProtectedCursor;
        set
        {
            if (!ReferenceEquals(ProtectedCursor, value))
                ProtectedCursor = value;
        }
    }
}
