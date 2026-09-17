param([string]$Dotnet = 'dotnet', [string]$Executable)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Join-Path $PSScriptRoot '..')
if ($Dotnet -eq 'dotnet' -and (Test-Path '.tools/dotnet/dotnet.exe')) { $Dotnet = (Resolve-Path '.tools/dotnet/dotnet.exe').Path }
$env:DOTNET_ROOT = Split-Path (Get-Command $Dotnet).Source
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $Dotnet restore --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
& $Dotnet format TransMessage.slnx --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Formatting failed.' }
& $Dotnet build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Executable) { $env:TM_TEST_EXE = (Resolve-Path -LiteralPath $Executable).Path }
& $Dotnet test tests/TransMessage.Tests --no-build --logger 'trx;LogFileName=verified.trx' --results-directory artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& $Dotnet run --project tests/TransMessage.GuiTests --no-build -- artifacts/gui
if ($LASTEXITCODE -ne 0) { throw 'GUI render checks failed.' }
& $Dotnet list package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed.' }
