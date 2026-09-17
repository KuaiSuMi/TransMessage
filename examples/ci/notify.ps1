# A self-hosted Windows runner can wrap its build. Preserve the build's exit code.
& 'C:\Tools\TransMessage\TransMessage.exe' run -- dotnet build
exit $LASTEXITCODE
