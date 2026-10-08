namespace HushMusic.App.Controls.Items;

/// <summary>Three small equaliser bars marking the playing track: they bounce while playing and freeze when paused.</summary>
public sealed partial class NowPlayingIndicator : UserControl
{
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(NowPlayingIndicator), new PropertyMetadata(false, (d, _) => ((NowPlayingIndicator)d).Update()));

    private bool _isLoaded;
    private bool _started;

    public NowPlayingIndicator()
    {
        InitializeComponent();
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        Update();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _started = false;
        Bounce.Stop();
    }

    private void Update()
    {
        if (!_isLoaded || Visibility != Visibility.Visible)
        {
            return;
        }

        if (!IsPlaying)
        {
            Bounce.Pause();
        }
        else if (_started)
        {
            Bounce.Resume();
        }
        else
        {
            Bounce.Begin();
            _started = true;
        }
    }

    /// <summary>Call after changing <see cref="UIElement.Visibility"/>: hidden indicators don't animate.</summary>
    public void Refresh()
    {
        if (Visibility != Visibility.Visible && _started)
        {
            Bounce.Stop();
            _started = false;
            return;
        }

        Update();
    }
}
