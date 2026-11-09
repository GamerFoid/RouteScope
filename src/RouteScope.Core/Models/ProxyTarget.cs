using System.Text.RegularExpressions;

namespace RouteScope.Core.Models;

/// <summary>
/// Something whose network traffic should be routed through the proxy.
/// A folder matches every process image living underneath it, which is the
/// "just proxy this folder" behaviour RouteScope is built around.
/// </summary>
public sealed class ProxyTarget : NotifyBase
{
    private TargetKind _kind = TargetKind.Folder;
    private string _value = "";
    private string _displayName = "";
    private bool _enabled = true;
    private bool _includeChildProcesses = true;
    private string? _note;
    private int? _liveConnections;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public TargetKind Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value)) RaiseAll(nameof(EffectiveName), nameof(Subtitle), nameof(KindLabel), nameof(Glyph));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) RaiseAll(nameof(EffectiveName), nameof(Subtitle), nameof(Glyph));
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value)) Raise(nameof(EffectiveName));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>Also match processes launched by a matched process (launchers, game clients).</summary>
    public bool IncludeChildProcesses
    {
        get => _includeChildProcesses;
        set => Set(ref _includeChildProcesses, value);
    }

    public string? Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>Live count of open connections for this target, filled in by the dashboard.</summary>
    public int? LiveConnections
    {
        get => _liveConnections;
        set
        {
            if (Set(ref _liveConnections, value)) Raise(nameof(HasLiveConnections));
        }
    }

    public bool HasLiveConnections => _liveConnections is > 0;

    private bool _isVerified;

    /// <summary>
    /// True once RouteScope has actually seen traffic from this target go through the proxy
    /// during the current session. This is the honest answer to "is it working for my app?".
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVerified
    {
        get => _isVerified;
        set
        {
            if (Set(ref _isVerified, value)) RaiseAll(nameof(VerifyText));
        }
    }

    public string VerifyText => IsVerified ? "routing" : "not seen yet";

    /// <summary>True when the folder or file no longer exists (unplugged drive, uninstalled app).</summary>
    public bool IsMissing
    {
        get
        {
            if (Kind == TargetKind.ProcessName) return false;
            if (string.IsNullOrWhiteSpace(Value)) return true;
            try { return !Path.Exists(Value); }
            catch { return true; }
        }
    }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ProxyTarget Clone() => (ProxyTarget)MemberwiseClone();

    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : Kind == TargetKind.Folder ? (SafeLeaf(Value) ?? Value)
        : Kind == TargetKind.Application ? (Path.GetFileNameWithoutExtension(Value) is { Length: > 0 } n ? n : Value)
        : Value;

    public string KindLabel => Kind switch
    {
        TargetKind.Folder => "Folder",
        TargetKind.Application => "Application",
        TargetKind.ProcessName => "Process name",
        _ => "Target",
    };

    /// <summary>Segoe Fluent Icons glyph used on the target card.</summary>
    public string Glyph => Kind switch
    {
        TargetKind.Folder => "\uE8B7",
        TargetKind.Application => "\uECAA",
        _ => "\uE7C4",
    };

    private static string? SafeLeaf(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf;
        }
        catch { return null; }
    }

    /// <summary>Human readable one-liner for cards and tooltips.</summary>
    public string Subtitle => Kind switch
    {
        TargetKind.Folder => Value,
        TargetKind.Application => Value,
        TargetKind.ProcessName => "Any process named " + Value,
        _ => Value,
    };

    public bool Matches(string? processPath, string? processName)
    {
        if (!Enabled) return false;
        switch (Kind)
        {
            case TargetKind.ProcessName:
                return !string.IsNullOrEmpty(processName) &&
                       string.Equals(processName, Value, StringComparison.OrdinalIgnoreCase);

            case TargetKind.Application:
                return !string.IsNullOrEmpty(processPath) &&
                       string.Equals(Normalize(processPath), Normalize(Value), StringComparison.OrdinalIgnoreCase);

            case TargetKind.Folder:
                if (string.IsNullOrEmpty(processPath)) return false;
                var p = Normalize(processPath);
                var folder = Normalize(Value).TrimEnd('\\');
                return p.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    /// <summary>
    /// The regex sing-box will use for this target. Folder targets become an anchored
    /// case-insensitive prefix match; the other kinds become exact matches.
    /// </summary>
    public string? ToProcessPathRegex()
    {
        switch (Kind)
        {
            case TargetKind.Folder:
            {
                var folder = Normalize(Value).TrimEnd('\\');
                if (folder.Length == 0) return null;
                return "(?i)^" + Regex.Escape(folder + "\\");
            }
            case TargetKind.Application:
            {
                var exe = Normalize(Value);
                return exe.Length == 0 ? null : "(?i)^" + Regex.Escape(exe) + "$";
            }
            default:
                return null;
        }
    }

    public string? ToProcessName()
    {
        if (Kind == TargetKind.ProcessName) return Value.Trim();
        if (Kind == TargetKind.Application)
        {
            var n = Path.GetFileName(Normalize(Value));
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
        return null;
    }

    private static string Normalize(string path)
    {
        var s = path.Trim().Replace('/', '\\');
        s = Regex.Replace(s, @"\\{2,}", @"\\");
        return s;
    }
}
