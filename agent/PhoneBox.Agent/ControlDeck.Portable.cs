#if !WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>Existing master/media controls remain available; portable radio/sound uses installed CLI players and native-only features report unavailable explicitly.</summary>
internal sealed class PortableDeckNative(
    AudioControl audio,
    MediaMonitor media,
    Func<bool, (string Kind, string Path)?>? findPlayer = null) : IDeckNative
{
    private const long PlayerCacheTtlMs = 15_000;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Process> _sounds = [];
    private (long At, (string Kind, string Path)? Player)? _filePlayerCache;
    private (long At, (string Kind, string Path)? Player)? _streamPlayerCache;

    public DeckAudio Audio()
    {
        var master = audio.Snapshot();
        var sessions = OperatingSystem.IsLinux() ? ReadPlaybackStreams() : null;
        if (master is null && sessions is null) return new(false, "Audio backend is unavailable", [], []);
        IReadOnlyList<DeckDevice> devices = master is { } a
            ? [new("default", "System output", "output", true, a.Volume, a.Muted, 0, 1, a.CanControl)]
            : [];
        return new(true, null, devices, sessions ?? []);
    }

    // PulseAudio and PipeWire's PulseAudio server both expose playback streams as sink inputs.
    private static IReadOnlyList<DeckSession>? ReadPlaybackStreams()
    {
        var output = Sh.Run("pactl", "--format=json", "list", "sink-inputs");
        if (output is null) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var sessions = new List<DeckSession>();
            foreach (var stream in document.RootElement.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
                    !index.TryGetUInt32(out var streamId) ||
                    !stream.TryGetProperty("volume", out var volume) || volume.ValueKind != JsonValueKind.Object ||
                    !stream.TryGetProperty("mute", out var muted) ||
                    muted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;

                uint? loudest = null;
                foreach (var channel in volume.EnumerateObject())
                    if (channel.Value.ValueKind == JsonValueKind.Object &&
                        channel.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number &&
                        value.TryGetUInt32(out var raw))
                        loudest = Math.Max(loudest ?? 0, raw);
                if (loudest is null) continue;

                stream.TryGetProperty("properties", out var properties);
                // PipeWire can expose internal streams that ignore both volume and mute writes.
                if (StreamProperty(properties, "channelmix.lock-volumes") == "true") continue;
                var binary = StreamProperty(properties, "application.process.binary");
                var name = StreamProperty(properties, "application.name") ?? binary ??
                    StreamProperty(properties, "media.name") ?? "Application";
                // On this machine Spotifast's Soloist filter exposes the controllable playback stream.
                if (name == "Soloist output" &&
                    StreamProperty(properties, "node.name")?.StartsWith("spotifast.soloist.", StringComparison.Ordinal) == true)
                    name = "Spotifast";
                var pid = int.TryParse(StreamProperty(properties, "application.process.id"), out var processId)
                    ? processId : -1;
                var sinkId = stream.TryGetProperty("sink", out var sink) && sink.ValueKind == JsonValueKind.Number
                    ? "pulse:" + sink.GetRawText() : "";
                var level = (int)Math.Round(loudest.Value * 100.0 / 65536); // PA_VOLUME_NORM
                // Corked streams can resume on the same slider, for example when a player is paused.
                sessions.Add(new($"pulse:{streamId}", name, pid, sinkId, level, muted.GetBoolean(), 0, binary, true));
            }
            return sessions;
        }
        catch (JsonException) { return null; }
    }

    private static string? StreamProperty(JsonElement properties, string key) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String && property.GetString() is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : null;

    public bool Device(string id, int? volume, bool? muted, int? channel = null)
    {
        if (id != "default" || channel is not null || audio.Current() is not { CanControl: true } current) return false;
        if (volume is { } v && !audio.SetVolume(v)) return false;
        return muted is not { } m || m == current.Muted || audio.ToggleMute();
    }
    public bool Session(string id, int? volume, bool? muted)
    {
        const string prefix = "pulse:";
        if (!OperatingSystem.IsLinux() || volume is < 0 or > 100 || (volume is null && muted is null) ||
            !id.StartsWith(prefix, StringComparison.Ordinal) ||
            !uint.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var streamId))
            return false;
        var target = streamId.ToString(CultureInfo.InvariantCulture);
        if (volume is { } level && Sh.Run("pactl", "set-sink-input-volume", target, $"{level}%") is null) return false;
        return muted is not { } mute || Sh.Run("pactl", "set-sink-input-mute", target, mute ? "1" : "0") is not null;
    }
    public bool DefaultDevice(string id, int? role = null) => false;
    public IReadOnlyList<DeckMonitor> Monitors() => [];
    public bool Brightness(string id, int value) => false;
    public IReadOnlyList<DeckItem> PowerPlans() => [];
    public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
    public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>(media.Snapshot() is { } m
        ? [new("current", m.App, m.Title, m.Playing, m.PosMs, m.DurMs, m.CanSeek, false, false, false, "None")] : []);
    public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) =>
        id != "current" ? Task.FromResult(false) : action == "pause" ? media.PauseAsync() : media.Control(action, value);
    public Task<bool> PauseCurrentMedia(CancellationToken ct) => media.PauseAsync();

    /// <summary>A player that ended by itself (stream dropped, file finished), so a radio shows as stopped at once.</summary>
    public event Action<string>? SoundStopped;

    public string? ActionState(DeckAction action)
    {
        if (action.Kind is not ("sound" or "radio")) return null;
        if (ResolvePlayer(isStream: action.Kind == "radio") is null) return null;
        lock (_gate) return _sounds.ContainsKey(action.Id) ? "Playing" : "Stopped";
    }

    public void Play(string id, string file, string? device, int volume)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Playback target is empty", nameof(file));
        var isStream = Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (!isStream)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("Sound file not found", file);
            file = Path.GetFullPath(file);
        }
        var player = ResolvePlayer(isStream) ?? throw new NotSupportedException("Install ffplay, mpv, or vlc for audio playback");
        var vol = Math.Clamp(volume, 0, 100);
        var start = BuildStartInfo(player.Kind, player.Path, file, vol);

        lock (_gate)
        {
            StopSoundLocked(id);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, _) => _ = Task.Run(() =>
            {
                var ended = false;
                lock (_gate)
                    if (_sounds.TryGetValue(id, out var current) && ReferenceEquals(current, process))
                    {
                        StopSoundLocked(id);
                        ended = true;
                    }
                if (ended) SoundStopped?.Invoke(id);
            });
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    throw new IOException("Could not start audio player");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }
            _ = DrainPipeAsync(process.StandardOutput.BaseStream);
            _ = DrainPipeAsync(process.StandardError.BaseStream);
            _sounds[id] = process;
        }
    }

    public void StopSound(string? id)
    {
        lock (_gate) StopSoundLocked(id);
    }

    private void StopSoundLocked(string? id)
    {
        foreach (var key in _sounds.Keys.Where(k => id is null || k == id).ToArray())
        {
            var process = _sounds[key];
            _sounds.Remove(key);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    private (string Kind, string Path)? ResolvePlayer(bool isStream)
    {
        if (findPlayer is not null) return findPlayer(isStream);
        var now = Environment.TickCount64;
        lock (_gate)
        {
            var cached = isStream ? _streamPlayerCache : _filePlayerCache;
            if (cached is { } hit && now - hit.At < PlayerCacheTtlMs) return hit.Player;
            var found = FindInstalledPlayer(isStream);
            if (isStream) _streamPlayerCache = (now, found);
            else _filePlayerCache = (now, found);
            return found;
        }
    }

    private static (string Kind, string Path)? FindInstalledPlayer(bool isStream)
    {
        foreach (var tool in new[] { "ffplay", "mpv", "cvlc", "vlc" })
            if (ToolProcess.Find(tool) is { } found) return (tool, found);
        if (!isStream && OperatingSystem.IsMacOS() && ToolProcess.Find("afplay") is { } afplay)
            return ("afplay", afplay);
        return null;
    }

    internal static ProcessStartInfo BuildStartInfo(string kind, string executable, string target, int volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppPaths.NeutralCwd,
        };
        var normalizedVolume = (clamped / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        string[] args = kind switch
        {
            "ffplay" => ["-nodisp", "-autoexit", "-vn", "-sn", "-loglevel", "error", "-volume", clamped.ToString(CultureInfo.InvariantCulture), "-i", target],
            "mpv" => ["--no-config", "--no-terminal", "--no-video", "--really-quiet", $"--volume={clamped.ToString(CultureInfo.InvariantCulture)}", "--", target],
            "afplay" => ["-v", normalizedVolume, target],
            _ => ["--intf", "dummy", "--no-one-instance", "--play-and-exit", "--no-video", "--quiet", $"--gain={normalizedVolume}", "--", target],
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task DrainPipeAsync(Stream stream)
    {
        var buffer = new byte[2048];
        try { while (await stream.ReadAsync(buffer) > 0) { } } catch { }
    }

    public IReadOnlyList<DeckItem> MidiPorts() => [];
    public void Midi(int port, string message, int value) => throw new NotSupportedException("Native MIDI requires Windows");
    public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await Task.CompletedTask; yield break; }
    public void Dispose() => StopSound(null);
}
#endif
