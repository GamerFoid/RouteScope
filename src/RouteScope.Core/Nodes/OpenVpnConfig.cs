namespace RouteScope.Core.Nodes;

/// <summary>
/// Reads the few fields RouteScope needs out of an OpenVPN <c>.ovpn</c> file.
/// The file itself is handed to openvpn untouched — only the label, endpoint and credential
/// requirement are read from it, so anything openvpn understands keeps working.
/// </summary>
public sealed class OpenVpnConfig
{
    public string RemoteHost { get; private set; } = "";
    public int RemotePort { get; private set; } = 1194;
    public string Protocol { get; private set; } = "udp";
    public string Device { get; private set; } = "tun";

    /// <summary>
    /// True when the file says bare <c>auth-user-pass</c> with no filename. OpenVPN then
    /// prompts on the console, which blocks forever in a GUI app — that is the "did not
    /// bring up the adapter in time" failure.
    /// </summary>
    public bool NeedsCredentials { get; private set; }

    /// <summary>True when the private key is encrypted and needs a passphrase.</summary>
    public bool NeedsKeyPassphrase { get; private set; }

    public bool IsUsable => RemoteHost.Length > 0;

    public string Endpoint => RemoteHost.Length == 0 ? "unknown" : $"{RemoteHost}:{RemotePort}";

    /// <summary>
    /// Makes a hand-written .ovpn safe to run unattended. Three things break OpenVPN when it
    /// is started by a GUI app that only wants to tunnel selected processes:
    /// <list type="bullet">
    /// <item><c>dev tunN</c> looks for an adapter literally named "tun2"; on Windows there is
    /// only "OpenVPN TAP-Windows6".</item>
    /// <item><c>setenv opt block-outside-dns</c> (and the bare <c>block-outside-dns</c>
    /// directive) makes OpenVPN install WFP filters that block DNS on every other adapter.
    /// That is a whole-machine change and it kills the engine's own DNS. It also bypasses
    /// <c>pull-filter</c>, because it is a local setenv, not a pushed option.</item>
    /// <item>A local <c>redirect-gateway</c> would hijack the machine's default route even
    /// with <c>--route-nopull</c>.</item>
    /// </list>
    /// Everything else — certificates, keys, ciphers, remotes — is left byte-for-byte as written.
    /// </summary>
    public static string Normalize(string text, Action<string>? onChange = null)
    {
        var output = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            // "dev tun2" / "dev tap0" -> "dev tun". OpenVPN on Windows then uses its TAP-Windows
            // adapter instead of looking for an adapter literally named tun2.
            if (trimmed.StartsWith("dev ", StringComparison.OrdinalIgnoreCase))
            {
                var device = trimmed[4..].Trim();
                var normalized = device.StartsWith("tap", StringComparison.OrdinalIgnoreCase) ? "tap" : "tun";
                if (device != normalized)
                    onChange?.Invoke($"dev {device} -> dev {normalized}");
                output.Add("dev " + normalized);
                continue;
            }

            // setenv opt X makes OpenVPN behave as if the server had pushed X. These bypass
            // pull-filter entirely, so strip every one that touches networking.
            if (trimmed.StartsWith("setenv opt ", StringComparison.OrdinalIgnoreCase))
            {
                var option = trimmed["setenv opt ".Length..].Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";

                if (NetworkAffectingOptions.Contains(option))
                {
                    onChange?.Invoke($"removed 'setenv opt {option}'");
                    continue;
                }
            }

            // A bare directive of the same kind is just as damaging and is not covered by
            // --route-nopull (which only suppresses PUSHED options). Note that
            // "pull-filter ignore ..." starts with "pull-filter", not the option, so the
            // protective lines in a working config are left alone. dhcp-option is deliberately
            // NOT stripped: a local DNS hint is inert with --route-nopull.
            var first = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (DangerousDirectives.Contains(first))
            {
                onChange?.Invoke($"removed '{first}'");
                continue;
            }

            output.Add(line);
        }

        // A hand-written config often omits the tail that the provider's "fixed" version has.
        // Without local dhcp-option DNS lines the TUN/TAP adapter ends up with NO DNS servers:
        // Windows then resolves through the physical adapter, gets the ISP's DNS sinkhole
        // (10.10.34.36), that private IP matches the LAN-bypass rule and times out on the
        // unbound direct outbound. Add the known-good tail so every .ovpn behaves alike.
        var protective = new List<string>();

        if (!output.Any(l => l.Trim().StartsWith("pull-filter ignore \"redirect-gateway\"", StringComparison.OrdinalIgnoreCase)))
            protective.Add("pull-filter ignore \"redirect-gateway\"");
        if (!output.Any(l => l.Trim().StartsWith("pull-filter ignore \"block-outside-dns\"", StringComparison.OrdinalIgnoreCase)))
            protective.Add("pull-filter ignore \"block-outside-dns\"");
        if (!output.Any(l => l.Trim().Equals("dhcp-option DNS 1.1.1.1", StringComparison.OrdinalIgnoreCase)))
            protective.Add("dhcp-option DNS 1.1.1.1");
        if (!output.Any(l => l.Trim().Equals("dhcp-option DNS 8.8.8.8", StringComparison.OrdinalIgnoreCase)))
            protective.Add("dhcp-option DNS 8.8.8.8");

        if (protective.Count > 0)
        {
            // Insert before the first inline block (<ca>, <cert>, ...) so the plain-directive
            // section stays together, exactly like a working provider config.
            var insertAt = output.FindIndex(l => l.TrimStart().StartsWith("<"));
            if (insertAt < 0) insertAt = output.Count;

            output.InsertRange(insertAt, protective);
            foreach (var added in protective) onChange?.Invoke($"added '{added}'");
        }

        return string.Join('\n', output);
    }

    private static readonly HashSet<string> DangerousDirectives =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "block-outside-dns",
            "redirect-gateway",
            "redirect-private",
            "route",
            "route-gateway",
            "topology",
            "ifconfig",
        };

    /// <summary>Options that would take over the machine instead of just the tunnel.</summary>
    private static readonly HashSet<string> NetworkAffectingOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "block-outside-dns",
            "redirect-gateway",
            "redirect-private",
            "dhcp-option",
            "route",
            "route-gateway",
            "topology",
            "ifconfig",
        };

    public static OpenVpnConfig Parse(string text)
    {
        var config = new OpenVpnConfig();
        var inKey = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith("</key>", StringComparison.OrdinalIgnoreCase)) { inKey = false; continue; }
            if (line.StartsWith("<key>", StringComparison.OrdinalIgnoreCase)) { inKey = true; continue; }
            if (inKey)
            {
                if (line.Contains("ENCRYPTED", StringComparison.OrdinalIgnoreCase))
                    config.NeedsKeyPassphrase = true;
                continue;
            }

            // Other inline blocks (<ca>, <cert>, <tls-auth>, ...) are for openvpn, not us.
            if (line.StartsWith('<')) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0].ToLowerInvariant())
            {
                case "auth-user-pass":
                    // "auth-user-pass" alone = interactive; "auth-user-pass creds.txt" = file.
                    if (parts.Length == 1) config.NeedsCredentials = true;
                    break;

                case "askpass":
                    config.NeedsKeyPassphrase = true;
                    break;

                case "remote":
                    if (config.RemoteHost.Length > 0) break;      // first remote wins
                    config.RemoteHost = parts[1];
                    if (parts.Length > 2 && int.TryParse(parts[2], out var port)) config.RemotePort = port;
                    break;

                case "proto":
                    config.Protocol = parts[1].ToLowerInvariant();
                    break;

                case "dev":
                    config.Device = parts[1].ToLowerInvariant();
                    break;
            }
        }

        return config;
    }
}
