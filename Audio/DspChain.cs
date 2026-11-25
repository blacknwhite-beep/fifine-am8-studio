using System;

namespace FifineAM8Studio.Audio;

public enum EqFilterType
{
    Peaking = 0,
    LowShelf = 1,
    HighShelf = 2,
    HighPass = 3,
    BandPass = 4
}

public enum VoiceFxMode
{
    Default = 0,
    Monster = 1,
    Robot = 2,
    Megaphone = 3
}

public sealed class BiquadFilter
{
    private double _b0 = 1.0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    public void Configure(EqFilterType type, double sampleRate, double freqHz, double q, double gainDb)
    {
        if (sampleRate <= 0) sampleRate = 48000.0;
        freqHz = Math.Clamp(freqHz, 20.0, sampleRate * 0.45);
        q = Math.Clamp(q, 0.1, 12.0);

        double w0 = 2.0 * Math.PI * freqHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);
        double a = Math.Pow(10.0, gainDb / 40.0);

        double b0, b1, b2, a0, a1, a2;

        switch (type)
        {
            case EqFilterType.LowShelf:
            {
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha);
                b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0);
                b2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha);
                a0 = (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha;
                a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0);
                a2 = (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha;
                break;
            }
            case EqFilterType.HighShelf:
            {
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha);
                b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0);
                b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha);
                a0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha;
                a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0);
                a2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha;
                break;
            }
            case EqFilterType.HighPass:
            {
                b0 = (1.0 + cosW0) * 0.5;
                b1 = -(1.0 + cosW0);
                b2 = (1.0 + cosW0) * 0.5;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha;
                break;
            }
            case EqFilterType.BandPass:
            {
                b0 = alpha;
                b1 = 0.0;
                b2 = -alpha;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha;
                break;
            }
            default: // Peaking
            {
                b0 = 1.0 + alpha * a;
                b1 = -2.0 * cosW0;
                b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a;
                a1 = -2.0 * cosW0;
                a2 = 1.0 - alpha / a;
                break;
            }
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public float Process(float input)
    {
        double y = _b0 * input + _z1;
        _z1 = _b1 * input - _a1 * y + _z2;
        _z2 = _b2 * input - _a2 * y;
        if (double.IsNaN(y) || double.IsInfinity(y))
        {
            _z1 = 0;
            _z2 = 0;
            return 0f;
        }
        return (float)y;
    }

    public double MagnitudeDb(double sampleRate, double freqHz)
    {
        double w = 2.0 * Math.PI * freqHz / sampleRate;
        double cosW = Math.Cos(w);
        double sinW = Math.Sin(w);
        double cos2W = Math.Cos(2.0 * w);
        double sin2W = Math.Sin(2.0 * w);

        double numRe = _b0 + _b1 * cosW + _b2 * cos2W;
        double numIm = -(_b1 * sinW + _b2 * sin2W);
        double denRe = 1.0 + _a1 * cosW + _a2 * cos2W;
        double denIm = -(_a1 * sinW + _a2 * sin2W);

        double magSq = (numRe * numRe + numIm * numIm) / Math.Max(1e-12, denRe * denRe + denIm * denIm);
        return 10.0 * Math.Log10(Math.Max(1e-12, magSq));
    }

    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
    }
}

public sealed class SimpleReverb
{
    private readonly float[][] _combBufs;
    private readonly int[] _combIdx;
    private readonly float[] _combLp;
    private readonly float[][] _apBufs;
    private readonly int[] _apIdx;

    public SimpleReverb(int sampleRate)
    {
        double scale = sampleRate / 44100.0;
        int[] combLens = { (int)(1116 * scale), (int)(1188 * scale), (int)(1277 * scale), (int)(1356 * scale) };
        int[] apLens = { (int)(556 * scale), (int)(441 * scale) };

        _combBufs = new float[combLens.Length][];
        _combIdx = new int[combLens.Length];
        _combLp = new float[combLens.Length];
        for (int i = 0; i < combLens.Length; i++)
            _combBufs[i] = new float[Math.Max(64, combLens[i])];

        _apBufs = new float[apLens.Length][];
        _apIdx = new int[apLens.Length];
        for (int i = 0; i < apLens.Length; i++)
            _apBufs[i] = new float[Math.Max(32, apLens[i])];
    }

    public float Process(float input, float wetAmount)
    {
        float sum = 0f;
        const float feedback = 0.78f;
        const float damp = 0.35f;

        for (int i = 0; i < _combBufs.Length; i++)
        {
            var buf = _combBufs[i];
            int idx = _combIdx[i];
            float delayed = buf[idx];
            _combLp[i] = delayed * (1f - damp) + _combLp[i] * damp;
            buf[idx] = input + _combLp[i] * feedback;
            _combIdx[i] = (idx + 1) % buf.Length;
            sum += delayed;
        }

        float y = sum * 0.25f;
        for (int i = 0; i < _apBufs.Length; i++)
        {
            var buf = _apBufs[i];
            int idx = _apIdx[i];
            float delayed = buf[idx];
            float outVal = -y + delayed;
            buf[idx] = y + delayed * 0.5f;
            _apIdx[i] = (idx + 1) % buf.Length;
            y = outVal;
        }

        return input * (1f - wetAmount * 0.45f) + y * wetAmount;
    }
}

/// <summary>
/// True 2 ms look-ahead peak limiter. Output is delayed by the look-ahead
/// window while the gain is computed from the peak of the whole window, so a
/// transient (mouse slam, plosive) is attenuated *before* it reaches the
/// output. Final hard clip at the ceiling is a safety net.
/// </summary>
public sealed class LookaheadLimiter
{
    private readonly float[] _ring;
    private readonly int _lookahead;
    private readonly double _sampleRate;
    private int _write;
    private float _gain = 1f;

    public LookaheadLimiter(int sampleRate, double lookaheadMs = 2.0)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _lookahead = Math.Max(2, (int)(_sampleRate * Math.Max(0.25, lookaheadMs) / 1000.0));
        _ring = new float[_lookahead + 1];
    }

    public float Process(float x, float ceiling)
    {
        ceiling = Math.Clamp(ceiling, 0.05f, 0.95f);
        int n = _ring.Length;

        _ring[_write] = x;
        int oldest = (_write + 1) % n;
        float delayed = _ring[oldest];

        float peak = 0f;
        for (int i = 0; i < n; i++)
        {
            float a = Math.Abs(_ring[i]);
            if (a > peak) peak = a;
        }

        float target = peak > ceiling ? ceiling / peak : 1f;
        float release = 1f - (float)Math.Exp(-1.0 / (0.120 * _sampleRate));
        _gain = target < _gain ? target : _gain + (target - _gain) * release;

        _write = oldest;

        float y = delayed * _gain;
        if (y > ceiling) y = ceiling;
        if (y < -ceiling) y = -ceiling;
        return y;
    }
}

public sealed class DspParameters
{
    public static readonly double[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public static readonly EqFilterType[] BandTypes =
    {
        EqFilterType.LowShelf, EqFilterType.Peaking, EqFilterType.Peaking, EqFilterType.Peaking, EqFilterType.Peaking,
        EqFilterType.Peaking, EqFilterType.Peaking, EqFilterType.Peaking, EqFilterType.Peaking, EqFilterType.HighShelf
    };

    public bool IsMuted { get; set; }
    public float GainDb { get; set; } = 0f;

    public bool LowCutEnabled { get; set; } = true;
    public bool PresenceBoostEnabled { get; set; }

    public bool NoiseReductionEnabled { get; set; } = true;
    public float NoiseGateThresholdDb { get; set; } = -46f;

    public bool CompressorEnabled { get; set; } = true;
    public float CompressorAmount { get; set; } = 0.45f; // 0..1

    public bool LimiterEnabled { get; set; } = true;
    public float LimiterCeilingDb { get; set; } = -3f; // -12..-0.5 dB

    public bool EqEnabled { get; set; } = true;
    public double[] EqGainsDb { get; set; } = new double[10];

    public bool ReverbEnabled { get; set; }
    public float ReverbLevel { get; set; } = 0.22f; // 0..1

    public VoiceFxMode VoiceFx { get; set; } = VoiceFxMode.Default;

    public bool MonitorEnabled { get; set; }
    public float MonitorVolume { get; set; } = 0.75f;

}

public sealed class DspChain
{
    private int _sampleRate = 48000;
    private readonly BiquadFilter _lowCut = new();
    private readonly BiquadFilter _presence = new();
    private readonly BiquadFilter _megaphoneBp = new();
    private readonly BiquadFilter _monsterLp = new();
    private readonly BiquadFilter[] _eqBands = new BiquadFilter[10];
    private SimpleReverb _reverb;
    private LookaheadLimiter _limiter;

    // Gate / Compressor envelope state
    private float _gateEnv;
    private float _gateGain = 1f;
    private float _compEnv;
    private double _fxPhase;

    // Live metering
    public volatile float InputLevelDb = -60f;
    public volatile float OutputLevelDb = -60f;
    public volatile bool GateIsOpen = true;

    private readonly float[] _waveformRing = new float[256];
    private int _waveWriteIdx;
    private int _waveDecim;

    public DspParameters Params { get; } = new();

    public DspChain()
    {
        for (int i = 0; i < 10; i++) _eqBands[i] = new BiquadFilter();
        _reverb = new SimpleReverb(_sampleRate);
        _limiter = new LookaheadLimiter(_sampleRate);
        ConfigureFilters(_sampleRate);
    }

    public void ConfigureFilters(int sampleRate)
    {
        if (sampleRate != _sampleRate && sampleRate > 0)
        {
            _sampleRate = sampleRate;
            _reverb = new SimpleReverb(_sampleRate);
            _limiter = new LookaheadLimiter(_sampleRate);
        }

        _lowCut.Configure(EqFilterType.HighPass, _sampleRate, 80.0, 0.707, 0.0);
        _presence.Configure(EqFilterType.Peaking, _sampleRate, 3200.0, 1.0, 3.5);
        _megaphoneBp.Configure(EqFilterType.BandPass, _sampleRate, 1350.0, 1.15, 0.0);
        _monsterLp.Configure(EqFilterType.LowShelf, _sampleRate, 180.0, 0.707, 7.5);

        for (int i = 0; i < 10; i++)
        {
            _eqBands[i].Configure(
                DspParameters.BandTypes[i],
                _sampleRate,
                DspParameters.BandFrequencies[i],
                0.707,
                Params.EqGainsDb[i]);
        }
    }

    public void UpdateEqBand(int index, double gainDb)
    {
        if (index < 0 || index >= 10) return;
        gainDb = Math.Clamp(gainDb, -12.0, 12.0);
        Params.EqGainsDb[index] = gainDb;
        _eqBands[index].Configure(
            DspParameters.BandTypes[index],
            _sampleRate,
            DspParameters.BandFrequencies[index],
            0.707,
            gainDb);
    }

    public double EvaluateCombinedEqDb(double freqHz)
    {
        double db = 0.0;
        if (Params.EqEnabled)
        {
            for (int i = 0; i < 10; i++)
                db += _eqBands[i].MagnitudeDb(_sampleRate, freqHz);
        }
        if (Params.LowCutEnabled)
            db += _lowCut.MagnitudeDb(_sampleRate, freqHz);
        if (Params.PresenceBoostEnabled)
            db += _presence.MagnitudeDb(_sampleRate, freqHz);
        return Math.Clamp(db, -18.0, 18.0);
    }

    public void CopyWaveform(float[] dest)
    {
        int start = _waveWriteIdx;
        int len = Math.Min(dest.Length, _waveformRing.Length);
        for (int i = 0; i < len; i++)
            dest[i] = _waveformRing[(start + i) % _waveformRing.Length];
    }

    public void ProcessBuffer(float[] monoSamples, int count)
    {
        if (count <= 0) return;

        float inSumSq = 0f;
        float outSumSq = 0f;
        float linearGain = (float)Math.Pow(10.0, Params.GainDb / 20.0);

        float gateThreshLin = (float)Math.Pow(10.0, Params.NoiseGateThresholdDb / 20.0);
        float gateAttack = 1f - (float)Math.Exp(-1.0 / (0.004 * _sampleRate));
        float gateRelease = 1f - (float)Math.Exp(-1.0 / (0.140 * _sampleRate));

        float compThreshDb = -10f - Params.CompressorAmount * 16f; // -10 to -26 dB
        float compRatio = 1.5f + Params.CompressorAmount * 4.5f;   // 1.5:1 to 6:1
        float compMakeup = (float)Math.Pow(10.0, (Params.CompressorAmount * 5.0) / 20.0);
        float compAttack = 1f - (float)Math.Exp(-1.0 / (0.006 * _sampleRate));
        float compRelease = 1f - (float)Math.Exp(-1.0 / (0.090 * _sampleRate));

        bool isMuted = Params.IsMuted;
        bool lowCut = Params.LowCutEnabled;
        bool presence = Params.PresenceBoostEnabled;
        bool nr = Params.NoiseReductionEnabled;
        bool eqOn = Params.EqEnabled;
        bool compOn = Params.CompressorEnabled;
        bool revOn = Params.ReverbEnabled && Params.ReverbLevel > 0.005f;
        float revAmt = Params.ReverbLevel;
        VoiceFxMode fx = Params.VoiceFx;
        bool limiterOn = Params.LimiterEnabled;
        float ceiling = (float)Math.Pow(10.0, Math.Clamp(Params.LimiterCeilingDb, -12f, -0.5f) / 20.0);

        for (int i = 0; i < count; i++)
        {
            float x = monoSamples[i];
            inSumSq += x * x;

            if (isMuted)
            {
                monoSamples[i] = 0f;
                PushWave(0f);
                continue;
            }

            // 1. Low-Cut (80 Hz HPF)
            if (lowCut) x = _lowCut.Process(x);

            // 2. Smooth Noise Gate / Noise Reduction Expander
            if (nr)
            {
                float absX = Math.Abs(x);
                _gateEnv += (absX - _gateEnv) * (absX > _gateEnv ? gateAttack : gateRelease);
                float targetGate = _gateEnv >= gateThreshLin
                    ? 1f
                    : Math.Clamp((_gateEnv / Math.Max(1e-6f, gateThreshLin)) * 0.25f, 0.02f, 1f);
                _gateGain += (targetGate - _gateGain) * (targetGate > _gateGain ? gateAttack : gateRelease);
                x *= _gateGain;
            }
            else
            {
                _gateGain = 1f;
            }

            // 3. 10-Band Parametric EQ
            if (eqOn)
            {
                for (int b = 0; b < 10; b++)
                    x = _eqBands[b].Process(x);
            }

            // 4. Mid-Freq Presence Boost
            if (presence) x = _presence.Process(x);

            // 5. Input Gain
            x *= linearGain;

            // 6. Studio Compressor
            if (compOn)
            {
                float absX = Math.Abs(x);
                _compEnv += (absX - _compEnv) * (absX > _compEnv ? compAttack : compRelease);
                float envDb = 20f * (float)Math.Log10(Math.Max(1e-6f, _compEnv));
                float gainRedDb = 0f;
                if (envDb > compThreshDb)
                    gainRedDb = (compThreshDb - envDb) * (1f - 1f / compRatio);
                x *= (float)Math.Pow(10.0, gainRedDb / 20.0) * compMakeup;
            }

            // 7. Voice FX
            if (fx != VoiceFxMode.Default)
            {
                switch (fx)
                {
                    case VoiceFxMode.Robot:
                        _fxPhase += 2.0 * Math.PI * 105.0 / _sampleRate;
                        if (_fxPhase > 2.0 * Math.PI) _fxPhase -= 2.0 * Math.PI;
                        x = x * (0.35f + 0.65f * (float)Math.Sin(_fxPhase));
                        break;
                    case VoiceFxMode.Monster:
                        _fxPhase += 2.0 * Math.PI * 52.0 / _sampleRate;
                        if (_fxPhase > 2.0 * Math.PI) _fxPhase -= 2.0 * Math.PI;
                        x = _monsterLp.Process(x * (0.55f + 0.45f * (float)Math.Sin(_fxPhase))) * 1.15f;
                        break;
                    case VoiceFxMode.Megaphone:
                        x = (float)Math.Tanh(_megaphoneBp.Process(x) * 3.8f) * 0.75f;
                        break;
                }
            }

            // 8. Room Reverb
            if (revOn) x = _reverb.Process(x, revAmt);

            // 9. Brickwall look-ahead peak limiter + hard safety clip.
            //    Catches keyboard/mouse slams and plosives that a slow
            //    compressor attack misses, with no digital overs.
            if (limiterOn && _limiter != null)
                x = _limiter.Process(x, ceiling);
            else if (x > ceiling) x = ceiling;
            else if (x < -ceiling) x = -ceiling;

            monoSamples[i] = x;
            outSumSq += x * x;
            PushWave(x);
        }

        float inRms = (float)Math.Sqrt(inSumSq / count);
        float outRms = (float)Math.Sqrt(outSumSq / count);
        InputLevelDb = Math.Clamp(20f * (float)Math.Log10(Math.Max(1e-5f, inRms)), -60f, 0f);
        OutputLevelDb = Math.Clamp(20f * (float)Math.Log10(Math.Max(1e-5f, outRms)), -60f, 0f);
        GateIsOpen = _gateGain > 0.5f;
    }

    private void PushWave(float sample)
    {
        if (++_waveDecim >= 12)
        {
            _waveDecim = 0;
            _waveformRing[_waveWriteIdx] = sample;
            _waveWriteIdx = (_waveWriteIdx + 1) % _waveformRing.Length;
        }
    }
}
