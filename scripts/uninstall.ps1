param([string]$Directory = (Join-Path $env:LOCALAPPDATA 'Programs/TransMessage'), [switch]$PurgeData)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
$exe = Join-Path $target 'TransMessage.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'The named directory does not contain TransMessage.exe.' }
& $exe startup disable
if ($LASTEXITCODE -ne 0) { throw 'Failed to remove startup.' }
& $exe codex uninstall
if ($LASTEXITCODE -ne 0) { throw 'Codex settings changed elsewhere. Resolve this before removing the executable.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TransMessage.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
if ($PurgeData) {
    $localData = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'TransMessage')).TrimEnd('\')
    $portableData = [IO.Path]::GetFullPath((Join-Path $target 'data')).TrimEnd('\')
    if (-not $portableData.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid portable data path.' }
    if ($localData -ne [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'TransMessage')).TrimEnd('\')) { throw 'Invalid user data path.' }
    foreach ($path in @($localData, $portableData)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
}
Write-Output 'Integration and startup removed. Exit TransMessage from the tray, then delete its program folder when ready. Configuration and logs are retained unless -PurgeData was selected.'
