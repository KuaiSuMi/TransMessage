param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs/TransMessage'), [switch]$Startup, [switch]$Codex)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath (Join-Path $source 'TransMessage.exe'))) { throw 'Run this script from the extracted Release package.' }
if ($source.TrimEnd('\') -eq $target.TrimEnd('\')) { throw 'Choose a destination different from the extracted package directory.' }
New-Item -ItemType Directory -Force $target | Out-Null
Get-ChildItem -LiteralPath $source | Where-Object { $_.Name -notin @('data','portable.flag') } | Copy-Item -Destination $target -Recurse -Force
$exe = Join-Path $target 'TransMessage.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'TransMessage.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.Save()
if ($Startup) { & $exe startup enable; if ($LASTEXITCODE -ne 0) { throw 'Startup registration failed.' } }
if ($Codex) { & $exe codex install; if ($LASTEXITCODE -ne 0) { throw 'Codex integration failed.' } }
Write-Output "Ready: $exe"
