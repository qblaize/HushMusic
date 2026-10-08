using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Services;

public sealed class NotificationService(ILogger<NotificationService> logger) : INotificationService
{
    public event EventHandler<AppNotification>? Raised;

    public void Show(AppNotification notification)
    {
        var level = notification.Severity switch
        {
            NotificationSeverity.Error => LogLevel.Error,
            NotificationSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, notification.Exception, "{Title}: {Message}", notification.Title, notification.Message);
        Raised?.Invoke(this, notification);
    }

    public void ShowError(string title, Exception exception) =>
        Show(new AppNotification(NotificationSeverity.Error, title, Describe(exception), exception));

    public void ShowInfo(string title, string message) =>
        Show(new AppNotification(NotificationSeverity.Informational, title, message));

    private static string Describe(Exception exception) => exception switch
    {
        AuthRequiredException => exception.Message,
        HttpRequestException => "Network error. Check your connection and try again.",
        TaskCanceledException => "The request timed out.",
        _ => exception.Message,
    };
}
