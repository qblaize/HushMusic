using Microsoft.UI.Input;

namespace HushMusic.App.Controls.TaskbarFlyout;

/// <summary>A tile of the flyout's "Up next" row: a button with the hand cursor. Style: <c>UpNextTileStyle</c>.</summary>
public sealed partial class UpNextTileButton : Button
{
    public UpNextTileButton()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
