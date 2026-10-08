using HushMusic.App.Services.Shell;

namespace HushMusic.App.Services;

public interface IDialogService
{
    /// <summary>Shows a ContentDialog on the main window. Dialogs are serialized (WinUI allows one at a time).</summary>
    Task<ContentDialogResult> ShowAsync(ContentDialog dialog);
}

public sealed class DialogService(IThemeService theme) : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            var root = App.MainWindow?.Content?.XamlRoot ?? throw new InvalidOperationException("Main window is not ready.");
            dialog.XamlRoot = root;
            dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];

            // The dialog lives in the popup layer, outside the themed window content: give it the shown theme explicitly.
            void FollowTheme(object? sender, EventArgs e) => dialog.RequestedTheme = theme.ActualTheme;
            FollowTheme(null, EventArgs.Empty);
            theme.ThemeChanged += FollowTheme;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                theme.ThemeChanged -= FollowTheme;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
