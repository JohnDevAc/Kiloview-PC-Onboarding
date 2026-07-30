param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'Kiloview.PcOnboarding.csproj'
$variant = if ($FrameworkDependent) { "$Runtime-framework-dependent" } else { $Runtime }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$compression = if ($FrameworkDependent) { 'false' } else { 'true' }
$output = Join-Path $projectRoot "artifacts\$variant"

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContained `
    --output $output `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=$compression `
    -p:DebugType=None `
    -p:DebugSymbols=false

$readme = Join-Path $projectRoot 'README.md'
$license = Join-Path $projectRoot 'LICENSE.md'
Copy-Item -LiteralPath $readme -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $output 'LICENSE.md') -Force

$archive = Join-Path $projectRoot "artifacts\Kiloview-PC-Onboarding-$variant.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal

Write-Host "PC onboarding utility published to $output"
Write-Host "Distribution package created at $archive"
