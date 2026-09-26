$ErrorActionPreference = 'Stop'

dotnet run --project .\src\VictusFanControl -c Release -- --safety-self-test
exit $LASTEXITCODE
