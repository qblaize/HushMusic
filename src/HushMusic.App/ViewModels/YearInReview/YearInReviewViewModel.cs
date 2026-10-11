using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Windows.System;
using Windows.UI;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Stats;

namespace HushMusic.App.ViewModels.YearInReview;

/// <summary>
/// "Your year in Hush": a full-window story of a year's listening (Stats → Your year in Hush). The view exists only
/// while it is open (<see cref="IsRealized"/>); this keeps the slides, the current one and whether it is paused, and
/// saves slides as images.
/// </summary>
public sealed partial class YearInReviewViewModel(
    IListeningStats stats,
    IImageCache images,
    INotificationService notifications,
    IAppPaths paths,
    ILogger<YearInReviewViewModel> logger) : ObservableObject
{
    /// <summary>How long each slide stays before the next one.</summary>
    public static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(6);

    private const string ImageFolderName = "Hush";

    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";
    private CancellationTokenSource? _cts;

    /// <summary>The view is created (true from opening until its closing animation has finished).</summary>
    [ObservableProperty]
    public partial bool IsRealized { get; set; }

    /// <summary>Shown or opening; false as soon as it starts to close.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>The view fully covers the window (the shell can hide what is underneath).</summary>
    [ObservableProperty]
    public partial bool CoversShell { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlides))]
    public partial IReadOnlyList<RecapSlide> Slides { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsLast))]
    public partial int Index { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseGlyph), nameof(PauseLabel))]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial int Year { get; set; }

    /// <summary>"Your 2026 in Hush".</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>The year's top song, blurred behind everything.</summary>
    [ObservableProperty]
    public partial string? BackdropUrl { get; set; }

    /// <summary>From the top song's cover; null keeps the app's accent.</summary>
    [ObservableProperty]
    public partial Color? Accent { get; set; }

    public bool HasSlides => Slides.Count > 0;

    public RecapSlide? Current => Index >= 0 && Index < Slides.Count ? Slides[Index] : null;

    public bool IsLast => Index >= Slides.Count - 1;

    public string PauseGlyph => IsPaused ? "" : "";

    public string PauseLabel => IsPaused ? "Resume (Space)" : "Pause (Space)";

    /// <summary>Loads <paramref name="year"/> and opens the story on its first slide.</summary>
    public async Task OpenAsync(int year)
    {
        if (IsOpen)
        {
            return;
        }

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            // Reading the year is file work: off the UI thread.
            var review = await Task.Run(() => stats.GetYearReviewAsync(year, cts.Token), cts.Token);
            var cover = review.TopSongs.FirstOrDefault()?.ArtUrl;
            Color? accent = null;
            try
            {
                accent = await Task.Run(() => RecapArt.AccentOfAsync(images, cover, cts.Token), cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not read an accent colour from {Url}", cover);
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            Year = year;
            Title = $"Your {year.ToString(CultureInfo.InvariantCulture)} in Hush";
            BackdropUrl = cover;
            Accent = accent;
            Slides = RecapSlideBuilder.Build(review);
            Index = 0;
            IsPaused = false;
            IsRealized = true;
            IsOpen = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            notifications.ShowError("Couldn't open your year in Hush", ex);
        }
    }

    [RelayCommand]
    public void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
    }

    /// <summary>Called by the view once its closing animation is done: everything it showed is let go of.</summary>
    public void OnClosed()
    {
        if (IsOpen)
        {
            return;
        }

        IsRealized = false;
        Slides = [];
        Index = 0;
        BackdropUrl = null;
    }

    [RelayCommand]
    public void Next()
    {
        if (Index < Slides.Count - 1)
        {
            Index++;
        }
    }

    [RelayCommand]
    public void Previous()
    {
        if (Index > 0)
        {
            Index--;
        }
        else
        {
            // Already on the first slide: start it again.
            OnPropertyChanged(nameof(Index));
        }
    }

    [RelayCommand]
    public void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Esc closes, Left and Right move, Space pauses. Returns true when the key was handled.</summary>
    public bool HandleKey(VirtualKey key, VirtualKeyModifiers modifiers, bool isRepeat)
    {
        if (!IsOpen || modifiers != VirtualKeyModifiers.None)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Escape:
                Close();
                return true;
            case VirtualKey.Left:
                Previous();
                return true;
            case VirtualKey.Right:
                Next();
                return true;
            case VirtualKey.Space:
                if (!isRepeat)
                {
                    TogglePause();
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Where the current slide's image goes: Pictures\Hush\"Your 2026 in Hush - Top song.png", numbered when that is
    /// taken. Test instances save into their own data folder instead, never the user's Pictures.
    /// </summary>
    public string ImagePathForCurrent()
    {
        var pictures = _testInstance
            ? Path.Combine(paths.Root, "pictures")
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.Create);
        var folder = Path.Combine(pictures, ImageFolderName);
        Directory.CreateDirectory(folder);

        var name = $"{Title} - {Current?.Name ?? "Slide"}";
        var path = Path.Combine(folder, name + ".png");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name} ({n.ToString(CultureInfo.InvariantCulture)}).png");
        }

        return path;
    }

    public void ReportSaved(string path)
    {
        logger.LogInformation("Saved a year-in-review image to {File}", Path.GetFileName(path));
        notifications.Show(new AppNotification(NotificationSeverity.Success, "Image saved", $"Saved in Pictures\\{ImageFolderName}.")
        {
            Action = new NotificationAction("Open folder", () => ShowInFolder(path)),
        });
    }

    public void ReportSaveFailed(Exception exception) => notifications.ShowError("Couldn't save the image", exception);

    private void ShowInFolder(string path)
    {
        try
        {
            // Explorer wants "/select," and the quoted path as one argument.
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the folder of {File}", Path.GetFileName(path));
        }
    }
}
