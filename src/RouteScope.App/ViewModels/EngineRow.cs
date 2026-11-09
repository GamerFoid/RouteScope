using RouteScope.App.Mvvm;
using RouteScope.Core.Updates;

namespace RouteScope.App.ViewModels;

/// <summary>One row in the "Cores &amp; updates" list.</summary>
public sealed class EngineRow : ObservableObject
{
    private string _installedVersion = "";
    private bool _isInstalled;
    private bool _isBusy;
    private double _progress;
    private string _statusText = "";

    public EngineRow(EngineDefinition definition) => Definition = definition;

    public EngineDefinition Definition { get; }
    public string Name => Definition.DisplayName;
    public string Description => Definition.Description;
    public string Repo => Definition.Repo;
    public bool IsRunnable => Definition.IsRunnable;
    public string RoleLabel => Definition.IsRunnable ? "routing engine" : "optional core";

    public string InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (Set(ref _installedVersion, value)) RaiseAll(nameof(VersionText), nameof(ActionLabel));
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (Set(ref _isInstalled, value)) RaiseAll(nameof(VersionText), nameof(ActionLabel), nameof(StatusKey));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value)) RaiseAll(nameof(ActionLabel), nameof(CanAct));
        }
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string VersionText => IsInstalled ? $"v{InstalledVersion}" : "not installed";
    public string StatusKey => IsInstalled ? "B.Success" : "B.Text.Tertiary";
    public bool CanAct => !IsBusy;

    private string? _latestVersion;

    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (Set(ref _latestVersion, value)) RaiseAll(nameof(HasUpdate), nameof(ActionLabel), nameof(VersionText));
        }
    }

    public bool HasUpdate =>
        !string.IsNullOrEmpty(LatestVersion) &&
        (!IsInstalled || EngineCatalog.ParseVersion(LatestVersion!) > EngineCatalog.ParseVersion(InstalledVersion));

    /// <summary>
    /// "Check" once installed: pressing it re-reads the published version and only downloads
    /// when there is actually something newer.
    /// </summary>
    public string ActionLabel => IsBusy
        ? "Working…"
        : HasUpdate ? "Update" : IsInstalled ? "Check" : "Install";
}
