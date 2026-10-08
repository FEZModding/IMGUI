[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryDir = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $repositoryDir 'release'
$packageDir = Join-Path $releaseDir 'IMGUI'
$archive = Join-Path $releaseDir 'IMGUI.zip'

Remove-Item -LiteralPath $packageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

try {
    dotnet publish (Join-Path $repositoryDir 'IMGUI.csproj') --configuration Release -p:ContinuousIntegrationBuild=true "-p:ModOutputDir=$packageDir"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $archive
}
finally {
    Remove-Item -LiteralPath $packageDir -Recurse -Force -ErrorAction SilentlyContinue
}
