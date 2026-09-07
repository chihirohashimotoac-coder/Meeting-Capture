namespace MeetingRecorder.Diarization;

/// <summary>
/// Mel-frequency cepstral coefficients, computed from scratch.
/// </summary>
/// <remarks>
/// Written by hand rather than pulled from a package so that the diarizer has no
/// extra dependency and no native binary: it runs on the audio already in memory
/// and adds nothing to the distribution size. The parameters are the
/// conventional speech-processing values (25 ms window, 10 ms hop, 26 mel
/// filters, 13 cepstral coefficients over 0-8000 Hz), which is what makes the
/// resulting vectors comparable to published work rather than arbitrary.
/// </remarks>
public sealed class Mfcc
{
    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly int _hopSize;
    private readonly int _fftSize;
    private readonly int _filterCount;
    private readonly int _coefficientCount;
    private readonly float[] _window;
    private readonly float[][] _melFilters;
    private readonly double[,] _dct;

    public Mfcc(
        int sampleRate = 16000,
        double frameMs = 25,
        double hopMs = 10,
        int filterCount = 26,
        int coefficientCount = 13,
        double lowFrequency = 80,
        double highFrequency = 8000)
    {
        _sampleRate = sampleRate;
        _frameSize = (int)(sampleRate * frameMs / 1000.0);
        _hopSize = (int)(sampleRate * hopMs / 1000.0);
        _fftSize = NextPowerOfTwo(_frameSize);
        _filterCount = filterCount;
        _coefficientCount = coefficientCount;

        _window = new float[_frameSize];
        for (var i = 0; i < _frameSize; i++)
        {
            // Hamming window: standard for MFCC front-ends.
            _window[i] = (float)(0.54 - (0.46 * Math.Cos(2.0 * Math.PI * i / (_frameSize - 1))));
        }

        _melFilters = BuildMelFilterBank(Math.Min(highFrequency, sampleRate / 2.0), lowFrequency);

        _dct = new double[coefficientCount, filterCount];
        for (var k = 0; k < coefficientCount; k++)
        {
            for (var n = 0; n < filterCount; n++)
            {
                _dct[k, n] = Math.Cos(Math.PI * k * ((2.0 * n) + 1) / (2.0 * filterCount));
            }
        }
    }

    public int CoefficientCount => _coefficientCount;

    /// <summary>Computes one MFCC vector per frame.</summary>
    public List<double[]> Compute(ReadOnlySpan<float> samples)
    {
        var result = new List<double[]>();
        if (samples.Length < _frameSize)
        {
            return result;
        }

        var real = new double[_fftSize];
        var imaginary = new double[_fftSize];
        var power = new double[(_fftSize / 2) + 1];
        var melEnergies = new double[_filterCount];

        for (var offset = 0; offset + _frameSize <= samples.Length; offset += _hopSize)
        {
            Array.Clear(real);
            Array.Clear(imaginary);

            for (var i = 0; i < _frameSize; i++)
            {
                real[i] = samples[offset + i] * _window[i];
            }

            Fft(real, imaginary);

            for (var i = 0; i < power.Length; i++)
            {
                power[i] = ((real[i] * real[i]) + (imaginary[i] * imaginary[i])) / _fftSize;
            }

            for (var f = 0; f < _filterCount; f++)
            {
                double sum = 0;
                var filter = _melFilters[f];
                for (var i = 0; i < filter.Length; i++)
                {
                    sum += filter[i] * power[i];
                }

                melEnergies[f] = Math.Log(Math.Max(sum, 1e-10));
            }

            var coefficients = new double[_coefficientCount];
            for (var k = 0; k < _coefficientCount; k++)
            {
                double sum = 0;
                for (var n = 0; n < _filterCount; n++)
                {
                    sum += melEnergies[n] * _dct[k, n];
                }

                coefficients[k] = sum;
            }

            result.Add(coefficients);
        }

        return result;
    }

    private float[][] BuildMelFilterBank(double highFrequency, double lowFrequency)
    {
        var bins = (_fftSize / 2) + 1;
        var lowMel = HzToMel(lowFrequency);
        var highMel = HzToMel(highFrequency);
        var points = new double[_filterCount + 2];

        for (var i = 0; i < points.Length; i++)
        {
            var mel = lowMel + ((highMel - lowMel) * i / (points.Length - 1));
            points[i] = MelToHz(mel) * _fftSize / _sampleRate;
        }

        var filters = new float[_filterCount][];
        for (var f = 0; f < _filterCount; f++)
        {
            var filter = new float[bins];
            var left = points[f];
            var centre = points[f + 1];
            var right = points[f + 2];

            for (var bin = 0; bin < bins; bin++)
            {
                if (bin >= left && bin <= centre && centre > left)
                {
                    filter[bin] = (float)((bin - left) / (centre - left));
                }
                else if (bin > centre && bin <= right && right > centre)
                {
                    filter[bin] = (float)((right - bin) / (right - centre));
                }
            }

            filters[f] = filter;
        }

        return filters;
    }

    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + (hz / 700.0));

    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    /// <summary>In-place iterative radix-2 Cooley-Tukey FFT.</summary>
    internal static void Fft(double[] real, double[] imaginary)
    {
        var n = real.Length;
        if (n <= 1)
        {
            return;
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var wReal = Math.Cos(angle);
            var wImaginary = Math.Sin(angle);

            for (var i = 0; i < n; i += length)
            {
                double curReal = 1.0;
                double curImaginary = 0.0;

                for (var j = 0; j < length / 2; j++)
                {
                    var uReal = real[i + j];
                    var uImaginary = imaginary[i + j];
                    var vReal = (real[i + j + (length / 2)] * curReal) - (imaginary[i + j + (length / 2)] * curImaginary);
                    var vImaginary = (real[i + j + (length / 2)] * curImaginary) + (imaginary[i + j + (length / 2)] * curReal);

                    real[i + j] = uReal + vReal;
                    imaginary[i + j] = uImaginary + vImaginary;
                    real[i + j + (length / 2)] = uReal - vReal;
                    imaginary[i + j + (length / 2)] = uImaginary - vImaginary;

                    var nextReal = (curReal * wReal) - (curImaginary * wImaginary);
                    curImaginary = (curReal * wImaginary) + (curImaginary * wReal);
                    curReal = nextReal;
                }
            }
        }
    }
}
