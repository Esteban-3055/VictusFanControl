$ErrorActionPreference = 'Stop'

dotnet run --project .\src\VictusFanControl -c Release -- --control-self-test
exit $LASTEXITCODE
