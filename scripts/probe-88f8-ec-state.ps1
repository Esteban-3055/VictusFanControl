$ErrorActionPreference = 'Stop'

dotnet run --project .\src\VictusFanControl -c Release -- --probe-88f8-ec-state
exit $LASTEXITCODE
