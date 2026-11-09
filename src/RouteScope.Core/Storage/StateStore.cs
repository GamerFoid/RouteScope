using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteScope.Core.Models;

namespace RouteScope.Core.Storage;

/// <summary>
/// Loads and saves <see cref="AppState"/> atomically. Writes go to a temp file and are
/// then swapped in, so a crash mid-save can never leave the user without a config.
/// </summary>
public sealed class StateStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    /// <summary>Schema version this build writes. Bumped when a saved default needs fixing.</summary>
    public const int CurrentSchema = 2;

    private readonly object _gate = new();
    private Timer? _debounce;
    private AppState _state = new();

    public string FilePath { get; }

    public StateStore(string? filePath = null) => FilePath = filePath ?? AppPaths.StateFile;

    public AppState State => _state;

    public AppState Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (loaded is not null) _state = loaded;
            }
        }
        catch (Exception ex)
        {
            LogBus.Error("storage", $"Could not read {FilePath}: {ex.Message}. Starting with a fresh profile.");
            TryBackupCorruptFile();
            _state = new AppState();
        }

        _state.Settings ??= new Settings();
        _state.Settings.Dns ??= new DnsSettings();
        _state.Settings.Advanced ??= new AdvancedSettings();
        _state.Targets ??= new List<ProxyTarget>();
        _state.Nodes ??= new List<ProxyNode>();
        _state.Externals ??= new List<ExternalProxy>();
        _state.Presets ??= new List<Preset>();

        // Targets whose files are gone stay in the list (a disconnected drive is not a
        // reason to throw away the user's setup); the UI flags them instead.
        _state.SeedDefaults();
        Migrate();
        return _state;
    }

    /// <summary>
    /// Brings an older profile up to date. Migrations must be safe to run on every launch
    /// and must never silently discard user data.
    /// </summary>
    private void Migrate()
    {
        var before = _state.SchemaVersion;

        if (_state.SchemaVersion < 2)
        {
            // v1 shipped with strict_route on. On Windows it drops any traffic that does not
            // leave through the tunnel — including the proxy client's own upstream connection,
            // which collapses the chain. Turn it off for existing profiles too.
            if (_state.Settings.Advanced.StrictRoute)
            {
                _state.Settings.Advanced.StrictRoute = false;
                LogBus.Warn("storage",
                    "Turned off strict_route: it blocks the upstream proxy. Re-enable it in Settings → Advanced if you really want it.");
            }
            _state.SchemaVersion = 2;
        }

        if (_state.SchemaVersion != before)
        {
            LogBus.Info("storage", $"Profile upgraded from schema {before} to {_state.SchemaVersion}.");
            SaveCore();
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var backup = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(FilePath, backup, overwrite: true);
            LogBus.Warn("storage", "Saved a copy of the unreadable profile to " + backup);
        }
        catch { }
    }

    /// <summary>Writes immediately. Safe to call from any thread.</summary>
    public void Save()
    {
        lock (_gate) SaveCore();
    }

    /// <summary>Coalesces bursts of edits (e.g. typing) into one write.</summary>
    public void SaveDebounced(int milliseconds = 400)
    {
        _debounce ??= new Timer(_ =>
        {
            lock (_gate) SaveCore();
        }, null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(milliseconds, Timeout.Infinite);
    }

    private void SaveCore()
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(_state, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            LogBus.Error("storage", "Save failed: " + ex.Message);
        }
    }

    /// <summary>Exports the whole profile for sharing or backup.</summary>
    public string Export() => JsonSerializer.Serialize(_state, JsonOptions);

    public void Import(string json)
    {
        var incoming = JsonSerializer.Deserialize<AppState>(json, JsonOptions)
                       ?? throw new InvalidOperationException("That file is not a RouteScope profile.");
        _state = incoming;
        _state.Settings ??= new Settings();
        _state.SeedDefaults();
        Save();
    }
}
