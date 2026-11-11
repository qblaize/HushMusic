using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// The "Pin to sidebar" / "Unpin from sidebar" item of a detail page's menu. The page calls <see cref="Attach"/> once
/// it knows what it shows and <see cref="Detach"/> when it is left; in between the item follows pins made elsewhere.
/// </summary>
public sealed partial class PinToggle(ISidebarPins pins, ISidebarPinActions actions, IUiDispatcher dispatcher) : ObservableObject
{
    private SidebarPin? _target;
    private bool _listening;

    /// <summary>Something can be pinned (the page has loaded).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    public partial bool CanPin { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Glyph))]
    public partial bool IsPinned { get; private set; }

    public string Label => SidebarPinItems.Label(IsPinned);

    public string Glyph => SidebarPinItems.Glyph(IsPinned);

    /// <summary>
    /// What the page shows; a pinned item takes its current title, and its art unless <paramref name="refreshArt"/> is
    /// false (the page's art is a stand-in that shouldn't replace a better one).
    /// </summary>
    public void Attach(SidebarPin? target, bool refreshArt = true)
    {
        _target = target;
        CanPin = target is not null;
        if (!_listening)
        {
            _listening = true;
            pins.Changed += OnPinsChanged;
        }

        Sync();
        if (target is not null)
        {
            actions.Refresh(refreshArt ? target : target with { ThumbnailUrl = null });
        }
    }

    public void Detach()
    {
        if (_listening)
        {
            _listening = false;
            pins.Changed -= OnPinsChanged;
        }
    }

    /// <summary>The item is gone (a deleted playlist): its pin goes too.</summary>
    public Task UnpinAsync() =>
        _target is { } target && actions.IsPinned(target) ? actions.UnpinAsync(target) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanPin))]
    private Task ToggleAsync() => _target is { } target ? actions.ToggleAsync(target) : Task.CompletedTask;

    private void OnPinsChanged(object? sender, EventArgs e) => dispatcher.Run(Sync);

    private void Sync() => IsPinned = _target is { } target && actions.IsPinned(target);
}
