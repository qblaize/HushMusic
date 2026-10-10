using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class AudioOutputChoiceTests
{
    private const string Speakers = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{76d542ef-9634-42cb-95e6-b0a4b9b5a5ca}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string Headset = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{1ed4362c-648e-4b1e-ac4c-212e7712a4ee}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string Monitor = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{33d6eb55-d95a-4b7a-8237-48a1137d1879}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_choice_is_the_system_default(string? chosen)
    {
        Assert.Null(AudioOutputChoice.Normalize(chosen));
        Assert.Null(AudioOutputChoice.Effective(chosen, [Speakers, Headset]));
    }

    [Fact]
    public void A_connected_choice_is_used()
    {
        Assert.Equal(Headset, AudioOutputChoice.Effective(Headset, [Speakers, Headset]));
    }

    [Fact]
    public void Ids_match_without_case_and_the_connected_spelling_is_used()
    {
        var saved = Headset.ToUpperInvariant();

        Assert.True(AudioOutputChoice.SameDevice(saved, Headset));
        Assert.Equal(Headset, AudioOutputChoice.Effective(saved, [Speakers, Headset]));
    }

    [Fact]
    public void A_choice_that_is_not_connected_falls_back_to_the_system_default()
    {
        Assert.Null(AudioOutputChoice.Effective(Headset, [Speakers, Monitor]));
        Assert.Null(AudioOutputChoice.Effective(Headset, []));
    }

    [Fact]
    public void Blank_ids_are_the_same_device_as_no_choice()
    {
        Assert.True(AudioOutputChoice.SameDevice(null, string.Empty));
        Assert.True(AudioOutputChoice.SameDevice(" ", null));
        Assert.False(AudioOutputChoice.SameDevice(null, Speakers));
    }

    [Fact]
    public void The_picker_lists_connected_outputs_by_name()
    {
        var list = AudioOutputChoice.ForPicker(
            [new(Speakers, "Speakers (Realtek(R) Audio)"), new(Headset, "Headset Earphone (Plantronics)"), new(Monitor, "E27q-20 (Display Audio)")],
            chosenId: null,
            chosenName: null);

        Assert.Equal(["E27q-20 (Display Audio)", "Headset Earphone (Plantronics)", "Speakers (Realtek(R) Audio)"], list.Select(d => d.Name));
        Assert.All(list, d => Assert.True(d.IsConnected));
    }

    [Fact]
    public void A_connected_choice_is_listed_once()
    {
        var list = AudioOutputChoice.ForPicker([new(Speakers, "Speakers"), new(Headset, "Headset")], Headset.ToUpperInvariant(), "Headset");

        Assert.Equal([Headset, Speakers], list.Select(d => d.Id));
        Assert.All(list, d => Assert.True(d.IsConnected));
    }

    [Fact]
    public void A_choice_that_is_not_connected_is_listed_last_under_its_known_name()
    {
        var list = AudioOutputChoice.ForPicker([new(Speakers, "Speakers")], Headset, " Headset Earphone ");

        Assert.Equal(2, list.Count);
        Assert.Equal(new AudioOutputDevice(Headset, "Headset Earphone", IsConnected: false), list[1]);
    }

    [Fact]
    public void A_choice_windows_no_longer_knows_has_no_name()
    {
        var list = AudioOutputChoice.ForPicker([], Headset, chosenName: null);

        Assert.Equal(new AudioOutputDevice(Headset, string.Empty, IsConnected: false), Assert.Single(list));
    }

    [Fact]
    public void Duplicate_and_disconnected_entries_are_left_out()
    {
        var list = AudioOutputChoice.ForPicker(
            [new(Speakers, "Speakers"), new(Speakers.ToUpperInvariant(), "Speakers"), new(Headset, "Headset", IsConnected: false), new(" ", "Blank")],
            chosenId: null,
            chosenName: null);

        Assert.Equal(Speakers, Assert.Single(list).Id);
    }
}
