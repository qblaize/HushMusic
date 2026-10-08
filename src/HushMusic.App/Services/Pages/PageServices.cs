using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Pages;

/// <summary>The services every content page view model needs, bundled to keep constructors short.</summary>
public sealed class PageServices(
    INotificationService notifications,
    IMediaItemActions actions,
    INavigationService navigation,
    IUiDispatcher dispatcher,
    IAuthService auth,
    ILikeStateService likes,
    IStreamWarmup warmup,
    INavigationPreviews previews)
{
    /// <summary>The item the current detail page was opened from (title and cached art for the loading header).</summary>
    public INavigationPreviews Previews { get; } = previews;

    public IStreamWarmup Warmup { get; } = warmup;

    public INotificationService Notifications { get; } = notifications;

    public IMediaItemActions Actions { get; } = actions;

    public INavigationService Navigation { get; } = navigation;

    public IUiDispatcher Dispatcher { get; } = dispatcher;

    public IAuthService Auth { get; } = auth;

    public ILikeStateService Likes { get; } = likes;
}
