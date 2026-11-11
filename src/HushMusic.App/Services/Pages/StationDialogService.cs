using Microsoft.Extensions.Logging;
using HushMusic.App.Dialogs.Radio;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>Radio dialogs. Only asks the user; the caller saves the station.</summary>
public interface IStationDialogService
{
    /// <summary>"Add a station": the checked station, or null when cancelled.</summary>
    Task<RadioStation?> PromptAddAsync();
}

internal sealed class StationDialogService(IDialogService dialogs, IStationProbe probe, ILogger<StationDialogService> logger) : IStationDialogService
{
    public async Task<RadioStation?> PromptAddAsync()
    {
        var viewModel = new AddStationViewModel(probe, logger);
        await dialogs.ShowAsync(new AddStationDialog(viewModel));
        return viewModel.Result;
    }
}
