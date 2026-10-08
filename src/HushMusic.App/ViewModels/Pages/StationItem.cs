using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>Where a station card sits on the Radio page; playing it queues the rest of that section.</summary>
public enum StationSection
{
    Favorites,
    Curated,
    Directory,
    Results,
}

/// <summary>What a station card asks of its page.</summary>
public interface IStationHost
{
    Task PlayAsync(StationItem item);

    Task ToggleFavoriteAsync(StationItem item);

    Task OpenWebsiteAsync(StationItem item);
}

/// <summary>One station card on the Radio page.</summary>
public sealed partial class StationItem : ObservableObject
{
    public StationItem(RadioStation station, StationSection section, IStationHost host)
    {
        Station = station;
        Section = section;
        Tags = string.Join(", ", station.Tags.Take(3).Select(t => t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..]));
        Details = DetailsOf(station);
        PlayCommand = new AsyncRelayCommand(() => host.PlayAsync(this));
        ToggleFavoriteCommand = new AsyncRelayCommand(() => host.ToggleFavoriteAsync(this));
        OpenWebsiteCommand = new AsyncRelayCommand(() => host.OpenWebsiteAsync(this), () => HasWebsite);
    }

    public RadioStation Station { get; }

    public StationSection Section { get; }

    public string Id => Station.Id;

    public string Name => Station.Name;

    public string? LogoUrl => Station.LogoUrl;

    /// <summary>"Deep house, Lounge".</summary>
    public string Tags { get; }

    /// <summary>"Romania · 128 kbps MP3".</summary>
    public string Details { get; }

    public bool HasWebsite => !string.IsNullOrWhiteSpace(Station.Homepage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph), nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; set; }

    /// <summary>The station the player has (playing or not).</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Current and playing (or connecting): the card shows the equaliser.</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    public string PlayLabel => "Play " + Station.Name;

    public string FavoriteGlyph => IsFavorite ? "" : "";

    public string FavoriteLabel => IsFavorite ? "Remove from favourites" : "Add to favourites";

    public IAsyncRelayCommand PlayCommand { get; }

    public IAsyncRelayCommand ToggleFavoriteCommand { get; }

    public IAsyncRelayCommand OpenWebsiteCommand { get; }

    private static string DetailsOf(RadioStation station)
    {
        var quality = station.BitrateKbps is { } kbps
            ? string.Create(CultureInfo.InvariantCulture, $"{kbps} kbps {station.Codec}").Trim()
            : station.Codec;
        var country = station.Country is { Length: > 18 } && station.CountryCode is { Length: 2 } code ? code : station.Country;
        return string.Join(" · ", new[] { country, quality }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
