# Builds the two release artefacts for RouteScope.
#
#   .\tools\publish.ps1                 -> dist\RouteScope           (runnable folder)
#   .\tools\publish.ps1 -Zip            -> dist\RouteScope-win-x64.zip
#   .\tools\publish.ps1 -Zip -Bundled   -> also dist\RouteScope-win-x64-bundled.zip
#
# The base zip ships the app only; engines are downloaded on first run.
# The bundled zip adds sing-box, Xray-core, Psiphon and OpenVPN so it works offline.
param(
    [string]$Runtime = "win-x64",
    [switch]$Zip,
    [switch]$Bundled,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$out = Join-Path $root "dist\RouteScope"

# Keep the user's profile across rebuilds: wiping the folder would delete their servers,
# subscriptions and saved logins every time.
$backup = Join-Path $root ".publish-data-backup"
$dataDir = Join-Path $out "data"
if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
if (Test-Path $dataDir)
{
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    Copy-Item $dataDir $backup -Recurse -Force
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

Write-Host "Publishing RouteScope ($Runtime, self-contained=$selfContained)..." -ForegroundColor Cyan

dotnet publish "src\RouteScope.App\RouteScope.App.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item "README.md"  (Join-Path $out "README.md")  -ErrorAction SilentlyContinue
Copy-Item "NOTICE.md"  (Join-Path $out "NOTICE.md")  -ErrorAction SilentlyContinue
Copy-Item "LICENSE"    (Join-Path $out "LICENSE")    -ErrorAction SilentlyContinue

# Restore the profile into the runnable folder, but never into the distributable zip.
if (Test-Path $backup)
{
    Copy-Item (Join-Path $backup "data") $out -Recurse -Force
    Remove-Item $backup -Recurse -Force
    Write-Host "Preserved the existing data\ profile." -ForegroundColor DarkGray
}

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done: {0}  ({1:N0} MB)" -f $out, $size) -ForegroundColor Green

# Folders that must never be published: someone's profile, logs, caches.
$privateFolders = @("data", "logs", "cache")

function New-ReleaseZip([string]$path, [string]$source, [switch]$IncludeEngines)
{
    if (Test-Path $path) { Remove-Item $path -Force }

    $staging = Join-Path $root ".publish-staging"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    # Copy everything except the private folders.
    Get-ChildItem $source | Where-Object { $_.Name -notin $privateFolders } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $staging $_.Name) -Recurse -Force }

    if ($IncludeEngines)
    {
        $engines = Join-Path $staging "engines"
        New-Item -ItemType Directory -Force -Path $engines | Out-Null

        # Pull each core from the local install cache if present; otherwise skip and warn.
        $sources = @(
            @{ Id = "singbox"; From = Join-Path $root "src\RouteScope.App\bin\Debug\net10.0-windows\engines\singbox" },
            @{ Id = "xray";    From = Join-Path $root "src\RouteScope.App\bin\Debug\net10.0-windows\engines\xray" },
            @{ Id = "psiphon"; From = Join-Path $root "src\RouteScope.App\bin\Debug\net10.0-windows\engines\psiphon" }
        )

        foreach ($item in $sources)
        {
            if (-not (Test-Path $item.From))
            {
                Write-Host ("  ! {0} not found locally - the bundled zip will not include it" -f $item.Id) -ForegroundColor Yellow
                continue
            }

            $target = Join-Path $engines $item.Id
            Copy-Item $item.From $target -Recurse -Force

            # Runtime caches are not part of a release.
            Get-ChildItem $target -Recurse -Include cache.db,*.log,*.pid -File -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue

            Write-Host ("  + engines\{0}" -f $item.Id) -ForegroundColor DarkGray
        }

        # OpenVPN is never downloaded by the app, so it can only come from a local install.
        $openVpn = @(
            "C:\Program Files\OpenVPN\bin\openvpn.exe",
            "C:\Program Files (x86)\OpenVPN\bin\openvpn.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if ($openVpn)
        {
            $openVpnDir = Join-Path $engines "openvpn"
            New-Item -ItemType Directory -Force -Path $openVpnDir | Out-Null
            Copy-Item $openVpn $openVpnDir -Force
            Write-Host "  + engines\openvpn" -ForegroundColor DarkGray
        }
        else
        {
            Write-Host "  ! OpenVPN not installed - the bundled zip will not include openvpn.exe" -ForegroundColor Yellow
        }
    }

    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $path -CompressionLevel Optimal
    Remove-Item $staging -Recurse -Force

    Write-Host ("Zip:   {0}  ({1:N1} MB)" -f $path, ((Get-Item $path).Length / 1MB)) -ForegroundColor Green
}

if ($Zip)
{
    New-ReleaseZip -path (Join-Path $root "dist\RouteScope-$Runtime.zip") -source $out
}

if ($Bundled)
{
    New-ReleaseZip -path (Join-Path $root "dist\RouteScope-$Runtime-bundled.zip") -source $out -IncludeEngines
}
