param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'Kiloview.PcOnboarding.csproj'
$agentProject = Join-Path $projectRoot 'Agent\Kiloview.PcAgent.csproj'
$variant = if ($FrameworkDependent) { "$Runtime-framework-dependent" } else { $Runtime }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$compression = if ($FrameworkDependent) { 'false' } else { 'true' }
$output = Join-Path $projectRoot "artifacts\$variant"
$agentOutput = Join-Path $projectRoot "artifacts\agent-$variant"

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
if (Test-Path -LiteralPath $agentOutput) {
    Remove-Item -LiteralPath $agentOutput -Recurse -Force
}

dotnet publish $agentProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContained `
    --output $agentOutput `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=$compression `
    -p:DebugType=None `
    -p:DebugSymbols=false

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContained `
    --output $output `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=$compression `
    -p:DebugType=None `
    -p:DebugSymbols=false

$agentPayload = Join-Path $output 'Agent'
New-Item -ItemType Directory -Path $agentPayload -Force | Out-Null
Copy-Item -Path (Join-Path $agentOutput '*') -Destination $agentPayload -Recurse -Force

$readme = Join-Path $projectRoot 'README.md'
$license = Join-Path $projectRoot 'LICENSE.md'
$remoteOnboardingHandover = Join-Path $projectRoot 'SERVER-REMOTE-ONBOARDING-HANDOVER.md'
$retryHandover = Join-Path $projectRoot 'SERVER-ONBOARDING-RETRY-HANDOVER.md'
$multicastHandover = Join-Path $projectRoot 'SERVER-MULTICAST-CONFIGURATION-HANDOVER.md'
$multicast24Handover = Join-Path $projectRoot 'AGENT-MULTICAST-24-UPGRADE-HANDOVER.md'
$testMachineHandover = Join-Path $projectRoot 'TEST-MACHINE-HANDOVER.md'
Copy-Item -LiteralPath $readme -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $output 'LICENSE.md') -Force
Copy-Item -LiteralPath $remoteOnboardingHandover -Destination (Join-Path $output 'SERVER-REMOTE-ONBOARDING-HANDOVER.md') -Force
Copy-Item -LiteralPath $retryHandover -Destination (Join-Path $output 'SERVER-ONBOARDING-RETRY-HANDOVER.md') -Force
Copy-Item -LiteralPath $multicastHandover -Destination (Join-Path $output 'SERVER-MULTICAST-CONFIGURATION-HANDOVER.md') -Force
Copy-Item -LiteralPath $multicast24Handover -Destination (Join-Path $output 'AGENT-MULTICAST-24-UPGRADE-HANDOVER.md') -Force
Copy-Item -LiteralPath $testMachineHandover -Destination (Join-Path $output 'TEST-MACHINE-HANDOVER.md') -Force
Copy-Item -LiteralPath $testMachineHandover -Destination (Join-Path $projectRoot 'artifacts\TEST-MACHINE-HANDOVER.md') -Force

$archive = Join-Path $projectRoot "artifacts\Kiloview-PC-Onboarding-$variant.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal

$archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
$checksumPath = "$archive.sha256"
Set-Content -LiteralPath $checksumPath -Value "$archiveHash  $(Split-Path -Leaf $archive)" -Encoding ascii

Write-Host "PC onboarding utility published to $output"
Write-Host "Distribution package created at $archive"
Write-Host "Checksum manifest created at $checksumPath"
