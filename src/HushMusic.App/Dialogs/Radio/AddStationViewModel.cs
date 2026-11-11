using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Radio;

namespace HushMusic.App.Dialogs.Radio;

/// <summary>"Add a station": a stream link (checked with <see cref="IStationProbe"/>) and an optional name.</summary>
public sealed partial class AddStationViewModel(IStationProbe probe, ILogger logger) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial string Link { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StationName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(ShowError))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial string? Error { get; set; }

    public bool CanAdd => !IsChecking && CustomStations.TryParseLink(Link, out _);

    public bool ShowError => !IsChecking && !string.IsNullOrEmpty(Error);

    /// <summary>The checked station, once <see cref="AddAsync"/> has succeeded.</summary>
    public RadioStation? Result { get; private set; }

    /// <summary>Checks the link; true when it leads to audio (<see cref="Result"/> is set), else <see cref="Error"/> says why.</summary>
    public async Task<bool> AddAsync(CancellationToken cancellationToken)
    {
        if (IsChecking)
        {
            return false;
        }

        if (!CustomStations.TryParseLink(Link, out var link))
        {
            Error = "Enter a web link, starting with http:// or https://.";
            return false;
        }

        Error = null;
        IsChecking = true;
        try
        {
            var found = await probe.ProbeAsync(link, cancellationToken);
            Result = CustomStations.Create(StationName, found, Guid.NewGuid());
            return true;
        }
        catch (HushException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Checking a station link failed");
            Error = "Couldn't check that link.";
        }
        finally
        {
            IsChecking = false;
        }

        return false;
    }

    partial void OnLinkChanged(string value) => Error = null;
}
