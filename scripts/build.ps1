param([string]$Version = '0.3.0', [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if ($Dotnet -eq 'dotnet' -and (Test-Path '.tools/dotnet/dotnet.exe')) { $Dotnet = (Resolve-Path '.tools/dotnet/dotnet.exe').Path }
$buildTime = [DateTime]::UtcNow.ToString('o')
$commit = git rev-parse --verify HEAD 2>$null
if ($LASTEXITCODE -ne 0) { $commit = 'uncommitted' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be SemVer x.y.z.' }
$packageRoot = Join-Path $repoRoot ('artifacts/package-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force $packageRoot, (Join-Path $repoRoot 'dist') | Out-Null
& $Dotnet publish src/TransMessage.App/TransMessage.App.csproj -c Release -r win-x64 --self-contained true -o $packageRoot -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" "-p:BuildTime=$buildTime" "-p:SourceRevisionId=$commit"
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
Copy-Item -LiteralPath README.md, THIRD_PARTY_NOTICES.md -Destination $packageRoot
Copy-Item -LiteralPath docs, examples, licenses -Destination $packageRoot -Recurse
New-Item -ItemType Directory (Join-Path $packageRoot 'scripts') | Out-Null
Copy-Item -LiteralPath scripts/install.ps1, scripts/uninstall.ps1 -Destination (Join-Path $packageRoot 'scripts')
$zip = Join-Path $repoRoot 'dist/TransMessage-win-x64.zip'
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $repoRoot 'dist/SHA256SUMS'), "$hash  TransMessage-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{ version=$Version; commit=$commit; build_time=$buildTime; architecture='win-x64'; self_contained=$true; sha256=$hash; size_bytes=(Get-Item $zip).Length; executable=(Join-Path $packageRoot 'TransMessage.exe') }
$manifest | ConvertTo-Json | Set-Content -LiteralPath 'dist/build-info.json' -Encoding utf8
Write-Output "Release: $zip"
Write-Output "Executable: $($manifest.executable)"
Write-Output "SHA256: $hash"
