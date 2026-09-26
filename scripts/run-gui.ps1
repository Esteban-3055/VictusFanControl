param(
    [string]$ModulesDir = ".\modules"
)

$ErrorActionPreference = 'Stop'

dotnet run --project .\src\VictusFanControl.App -c Release -- --modules-dir $ModulesDir

exit $LASTEXITCODE
