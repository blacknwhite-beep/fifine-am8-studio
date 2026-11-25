using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace FifineAM8Studio.Audio;

/// <summary>
/// Controls the real FIFINE virtual-audio driver through the local WebSocket
/// (ws://127.0.0.1:24925) exposed by fifineAudioDriverControler.exe -- the exact
/// same channel FIFINE Genie uses (reverse engineered from its JS bundle).
///
/// This matters because Discord/OBS usually capture the FIFINE virtual
/// microphone directly from the driver and therefore bypass this app's WASAPI
/// DSP chain. Muting/setting volume through the driver reaches those apps no
/// matter which endpoint they selected.
/// </summary>
public sealed class FifineDriverClient : IDisposable
{
    private const string DriverUri = "ws://127.0.0.1:24925";
    private readonly object _sync = new();
    private ClientWebSocket? _ws;
    private readonly List<DriverDevice> _devices = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _muted;

    private sealed record DriverDevice(string Name, string Id);

    public bool Connected => _ws?.State == WebSocketState.Open;
    public string Status { get; private set; } = "Driver offline";

    private static string Json(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private bool EnsureConnected()
    {
        lock (_sync)
        {
            if (Connected) return true;
            try
            {
                _ws?.Dispose();
                var ws = new ClientWebSocket();
                var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                ws.ConnectAsync(new Uri(DriverUri), timeout.Token).Wait();
                if (ws.State != WebSocketState.Open) { ws.Dispose(); return false; }
                _ws = ws;
                RefreshDevicesLocked();
                Status = $"Driver online ({_devices.Count} FIFINE endpoints)";
                return true;
            }
            catch
            {
                _ws = null;
                Status = "Driver offline (Genie controller not running)";
                return false;
            }
        }
    }

    private void Send(string payload)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).Wait();
        }
        catch { }
    }

    private string Receive(int maxMessages = 4, int perMessageMs = 500)
    {
        var sb = new StringBuilder();
        if (_ws == null || _ws.State != WebSocketState.Open) return sb.ToString();
        var buffer = new byte[262144];
        for (int i = 0; i < maxMessages; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(perMessageMs);
                var task = _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (!task.Wait(perMessageMs + 150)) break;
                var res = task.Result;
                if (res.MessageType != WebSocketMessageType.Text) continue;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count)).Append('\n');
                if (res.EndOfMessage && sb.Length > 0) break;
            }
            catch { break; }
        }
        return sb.ToString();
    }

    private void RefreshDevicesLocked()
    {
        _devices.Clear();
        Send("{\"event\":\"getInputDeviceInfos\"}");
        ParseDevices(Receive());
        Send("{\"event\":\"getOutputDeviceInfos\"}");
        ParseDevices(Receive());
    }

    private void ParseDevices(string json)
    {
        // Minimal parser for {"deviceName":"...","deviceID":"...", ...} objects.
        const string nameKey = "\"deviceName\":\"";
        const string idKey = "\"deviceID\":\"";
        int pos = 0;
        while ((pos = json.IndexOf(nameKey, pos, StringComparison.Ordinal)) >= 0)
        {
            int nameStart = pos + nameKey.Length;
            int nameEnd = json.IndexOf('"', nameStart);
            if (nameEnd < 0) break;
            string name = json.Substring(nameStart, nameEnd - nameStart);

            int idPos = json.IndexOf(idKey, nameEnd, StringComparison.Ordinal);
            if (idPos < 0 || idPos - nameEnd > 200) { pos = nameEnd + 1; continue; }
            int idStart = idPos + idKey.Length;
            int idEnd = json.IndexOf('"', idStart);
            if (idEnd < 0) break;
            string id = json.Substring(idStart, idEnd - idStart);

            if (name.IndexOf("fifine", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !_devices.Any(d => d.Id == id))
                _devices.Add(new DriverDevice(name, id));

            pos = idEnd + 1;
        }
    }

    public bool Connect() => EnsureConnected();

    /// <summary>
    /// Hard-mute every FIFINE endpoint in the driver (physical mic, virtual mic
    /// and its stream/aux flavours). Discord/OBS then get silence regardless of
    /// which endpoint they capture.
    /// </summary>
    public void SetMute(bool muted)
    {
        _muted = muted;
        if (!EnsureConnected()) return;
        lock (_sync)
        {
            foreach (var d in _devices)
                Send($"{{\"event\":\"setDeviceMute\",\"payload\":{{\"deviceType\":\"\",\"deviceName\":\"{Json(d.Name)}\",\"deviceID\":\"{Json(d.Id)}\",\"deviceMute\":{(muted ? "true" : "false")}}}}}");
            Receive(1, 200);
        }
    }

    public void SetVolume(int volume)
    {
        if (!EnsureConnected()) return;
        volume = Math.Clamp(volume, 0, 100);
        lock (_sync)
        {
            foreach (var d in _devices)
                Send($"{{\"event\":\"setDeviceVolume\",\"payload\":{{\"deviceType\":\"\",\"deviceName\":\"{Json(d.Name)}\",\"deviceID\":\"{Json(d.Id)}\",\"deviceVolume\":{volume}}}}}");
            Receive(1, 200);
        }
    }

    public bool IsDriverMuted()
    {
        if (!EnsureConnected()) return _muted;
        lock (_sync)
        {
            Send("{\"event\":\"getInputDeviceInfos\"}");
            var r = Receive();
            const string key = "\"deviceMute\":true";
            return r.Contains(key, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        try { if (_muted) SetMute(false); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        _ws = null;
    }
}
