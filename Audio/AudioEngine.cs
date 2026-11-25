using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FifineAM8Studio.Audio;

public sealed record AudioDeviceInfo(string Id, string FriendlyName);

public sealed class AudioEngine : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _virtualOut;
    private BufferedWaveProvider? _virtualBuf;
    private WasapiOut? _monitorOut;
    private BufferedWaveProvider? _monitorBuf;

    private readonly object _sync = new();
    private float[] _monoScratch = new float[4096];
    private byte[] _outBytesScratch = new byte[16384];
    private byte[] _monBytesScratch = new byte[16384];

    public DspChain Dsp { get; } = new();
    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "Stopped";

    public static List<AudioDeviceInfo> GetCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .OrderBy(d => d.FriendlyName)
            .ToList();
    }

    public static List<AudioDeviceInfo> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .OrderBy(d => d.FriendlyName)
            .ToList();
    }

    public void Start(string? captureDeviceId, string? virtualOutDeviceId, string? monitorOutDeviceId)
    {
        lock (_sync)
        {
            StopInternal();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var capDev = !string.IsNullOrEmpty(captureDeviceId)
                    ? enumerator.GetDevice(captureDeviceId)
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

                _capture = new WasapiCapture(capDev, false, 20);
                var capFmt = _capture.WaveFormat;
                int sampleRate = capFmt.SampleRate;
                Dsp.ConfigureFilters(sampleRate);

                var stereoFloatFmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

                if (!string.IsNullOrEmpty(virtualOutDeviceId))
                {
                    try
                    {
                        var vDev = enumerator.GetDevice(virtualOutDeviceId);
                        _virtualBuf = new BufferedWaveProvider(stereoFloatFmt)
                        {
                            DiscardOnBufferOverflow = true,
                            BufferDuration = TimeSpan.FromMilliseconds(140)
                        };
                        _virtualOut = new WasapiOut(vDev, AudioClientShareMode.Shared, false, 25);
                        _virtualOut.Init(_virtualBuf);
                        _virtualOut.Play();
                    }
                    catch
                    {
                        _virtualOut?.Dispose();
                        _virtualOut = null;
                        _virtualBuf = null;
                    }
                }

                if (!string.IsNullOrEmpty(monitorOutDeviceId) && monitorOutDeviceId != virtualOutDeviceId)
                {
                    try
                    {
                        var mDev = enumerator.GetDevice(monitorOutDeviceId);
                        _monitorBuf = new BufferedWaveProvider(stereoFloatFmt)
                        {
                            DiscardOnBufferOverflow = true,
                            BufferDuration = TimeSpan.FromMilliseconds(140)
                        };
                        _monitorOut = new WasapiOut(mDev, AudioClientShareMode.Shared, false, 25);
                        _monitorOut.Init(_monitorBuf);
                        _monitorOut.Play();
                    }
                    catch
                    {
                        _monitorOut?.Dispose();
                        _monitorOut = null;
                        _monitorBuf = null;
                    }
                }

                _capture.DataAvailable += OnCaptureData;
                _capture.StartRecording();
                IsRunning = true;
                StatusText = $"Live ({sampleRate / 1000.0:0.#} kHz)";
            }
            catch (Exception ex)
            {
                StopInternal();
                StatusText = $"Error: {ex.Message}";
            }
        }
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture == null) return;

        var fmt = _capture.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frameBytes = channels * bytesPerSample;
        int frames = e.BytesRecorded / Math.Max(1, frameBytes);
        if (frames <= 0) return;

        if (_monoScratch.Length < frames)
            _monoScratch = new float[frames * 2];

        // Convert incoming WASAPI capture buffer (Float32 or PCM16) to mono float32
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32))
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int baseOffset = f * frameBytes;
                for (int c = 0; c < channels; c++)
                    sum += BitConverter.ToSingle(e.Buffer, baseOffset + c * 4);
                _monoScratch[f] = sum / channels;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int baseOffset = f * frameBytes;
                for (int c = 0; c < channels; c++)
                    sum += BitConverter.ToInt16(e.Buffer, baseOffset + c * 2) / 32768f;
                _monoScratch[f] = sum / channels;
            }
        }
        else
        {
            return;
        }

        // Run real-time DSP chain
        Dsp.ProcessBuffer(_monoScratch, frames);

        // Pack stereo float32 (L=R=processed mono) for Virtual Mic & Live Monitor
        int outByteCount = frames * 8;
        if (_outBytesScratch.Length < outByteCount)
            _outBytesScratch = new byte[outByteCount * 2];

        if (_virtualBuf != null)
        {
            for (int f = 0; f < frames; f++)
            {
                int off = f * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(_monoScratch[f]), 0, _outBytesScratch, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(_monoScratch[f]), 0, _outBytesScratch, off + 4, 4);
            }
            _virtualBuf.AddSamples(_outBytesScratch, 0, outByteCount);
        }

        if (_monitorBuf != null && Dsp.Params.MonitorEnabled)
        {
            if (_monBytesScratch.Length < outByteCount)
                _monBytesScratch = new byte[outByteCount * 2];
            float mv = Dsp.Params.MonitorVolume;
            for (int f = 0; f < frames; f++)
            {
                float s = _monoScratch[f] * mv;
                int off = f * 8;
                Buffer.BlockCopy(BitConverter.GetBytes(s), 0, _monBytesScratch, off, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(s), 0, _monBytesScratch, off + 4, 4);
            }
            _monitorBuf.AddSamples(_monBytesScratch, 0, outByteCount);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnCaptureData;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        if (_virtualOut != null)
        {
            try { _virtualOut.Stop(); } catch { }
            _virtualOut.Dispose();
            _virtualOut = null;
        }
        _virtualBuf = null;
        if (_monitorOut != null)
        {
            try { _monitorOut.Stop(); } catch { }
            _monitorOut.Dispose();
            _monitorOut = null;
        }
        _monitorBuf = null;
        IsRunning = false;
        StatusText = "Stopped";
    }

    public void Dispose() => Stop();
}
