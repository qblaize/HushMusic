namespace HushMusic.Playback.Pulse;

/// <summary>
/// A synthetic groove for automated test instances (HUSHMUSIC_TEST_PULSE=1), which play muted, so the capture hears
/// only silence: a 120 BPM kick (accented on the first beat of each bar), a bass note on the off-beats and a soft
/// chord, added to the captured audio. It lets the effect be seen and measured without making a sound.
/// </summary>
internal sealed class TestSignal(int sampleRate)
{
    private const double Beat = 0.5;

    private long _position;

    public void AddTo(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var t = _position++ / (double)sampleRate;
            var sinceKick = t % Beat;
            var accent = (int)(t / Beat) % 4 == 0 ? 0.75 : 0.55;
            var kick = accent * Math.Sin(2 * Math.PI * 55 * sinceKick) * Math.Exp(-sinceKick / 0.12);
            var sinceBass = (t + (Beat / 2)) % Beat;
            var bass = 0.2 * Math.Sin(2 * Math.PI * 82.4 * sinceBass) * Math.Exp(-sinceBass / 0.09);
            var chord = 0.08 * (Math.Sin(2 * Math.PI * 440 * t) + Math.Sin(2 * Math.PI * 554.4 * t) + Math.Sin(2 * Math.PI * 659.3 * t));
            samples[i] += (float)(kick + bass + chord);
        }
    }
}
