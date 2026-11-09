param([switch]$NoBuild)
$ErrorActionPreference = "Stop"
$root = "D:\RouteScope"
Set-Location $root

if (-not $NoBuild) {
    dotnet build src\RouteScope.App\RouteScope.App.csproj -v q --nologo 2>&1 | Select-Object -Last 6
}

$exe = Join-Path $root "src\RouteScope.App\bin\Debug\net10.0-windows\RouteScope.exe"
$out = Join-Path $root ".render"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Get-Process RouteScope -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

$p = Start-Process -FilePath $exe -ArgumentList "--render", "`"$out`"" -PassThru
$p | Wait-Process -Timeout 90 -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Get-Process RouteScope -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }

Get-Content (Join-Path $out "render.log") -ErrorAction SilentlyContinue | Select-Object -Last 10
