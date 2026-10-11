namespace HushMusic.Playback.Pulse;

/// <summary>An in-place radix-2 FFT of one fixed size (a power of two), with its tables computed once.</summary>
internal sealed class Fft
{
    private readonly int[] _reversed;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size must be a power of two.");
        }

        Size = size;
        var bits = 0;
        while (1 << bits < size)
        {
            bits++;
        }

        _reversed = new int[size];
        for (var i = 0; i < size; i++)
        {
            var reversed = 0;
            for (var bit = 0; bit < bits; bit++)
            {
                reversed |= ((i >> bit) & 1) << (bits - 1 - bit);
            }

            _reversed[i] = reversed;
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            var angle = -2 * Math.PI * i / size;
            _cos[i] = (float)Math.Cos(angle);
            _sin[i] = (float)Math.Sin(angle);
        }
    }

    public int Size { get; }

    /// <summary>Replaces (<paramref name="real"/>, <paramref name="imaginary"/>) with its discrete Fourier transform.</summary>
    public void Transform(Span<float> real, Span<float> imaginary)
    {
        if (real.Length != Size || imaginary.Length != Size)
        {
            throw new ArgumentException($"Both parts must hold {Size} values.");
        }

        for (var i = 0; i < Size; i++)
        {
            var j = _reversed[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= Size; length <<= 1)
        {
            var half = length / 2;
            var step = Size / length;
            for (var start = 0; start < Size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * step];
                    var wi = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = (real[b] * wr) - (imaginary[b] * wi);
                    var ti = (real[b] * wi) + (imaginary[b] * wr);
                    real[b] = real[a] - tr;
                    imaginary[b] = imaginary[a] - ti;
                    real[a] += tr;
                    imaginary[a] += ti;
                }
            }
        }
    }
}
