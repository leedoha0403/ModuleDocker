# Builds a runnable ModuleDock folder (Host + bundled sample widgets) into .\dist (framework-dependent, needs the .NET 8 Desktop Runtime).
param([string]$Output = (Join-Path $PSScriptRoot 'dist'))

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    dotnet publish src/Dora.Widget.Host -c Release -o $Output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    $widgets = Get-ChildItem (Join-Path $Output 'widgets') -Recurse -Filter '*.Widget.dll' -ErrorAction SilentlyContinue
    Write-Host ("Published to {0} with {1} widget module(s):" -f $Output, @($widgets).Count)
    $widgets | ForEach-Object { Write-Host ('  ' + $_.FullName.Substring($Output.Length + 1)) }
}
finally { Pop-Location }
