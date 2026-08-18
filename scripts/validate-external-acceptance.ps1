[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$AcceptanceEvidencePath,
  [Parameter(Mandatory = $true)]
  [string]$PayloadManifestPath,
  [Parameter(Mandatory = $true)]
  [string]$ProductVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Leaf([string]$Path, [string]$Label) {
  if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "$Label does not exist: $Path"
  }
  return [IO.Path]::GetFullPath($Path)
}

function Read-JsonObject([string]$Path, [string]$Label) {
  try { return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
  catch { throw "$Label is not valid JSON: $($_.Exception.Message)" }
}

function Require-Text($Object, [string]$Name, [string]$Label) {
  $property = $Object.PSObject.Properties[$Name]
  if (-not $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
    throw "$Label is missing $Name."
  }
  return ([string]$property.Value).Trim()
}

$acceptancePath = Assert-Leaf $AcceptanceEvidencePath 'Real Codex acceptance evidence'
$manifestPath = Assert-Leaf $PayloadManifestPath 'Candidate payload manifest'
$acceptance = Read-JsonObject $acceptancePath 'Real Codex acceptance evidence'
$manifest = Read-JsonObject $manifestPath 'Candidate payload manifest'

if ([int]$acceptance.schemaVersion -ne 1) { throw 'Acceptance schemaVersion must be 1.' }
if ((Require-Text $acceptance 'product' 'Acceptance evidence') -cne 'CodexTotalManager') {
  throw 'Acceptance product does not match.'
}
if ((Require-Text $acceptance 'productVersion' 'Acceptance evidence') -cne $ProductVersion) {
  throw 'Acceptance productVersion does not match the current product.'
}
if ((Require-Text $acceptance 'outcome' 'Acceptance evidence') -cne 'PASSED') {
  throw 'Acceptance outcome is not PASSED.'
}
if ($acceptance.dedicatedTestComputer -ne $true) {
  throw 'Acceptance must explicitly come from a dedicated test computer.'
}
$runId = Require-Text $acceptance 'testComputerRunId' 'Acceptance evidence'
if ($runId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}$') {
  throw 'testComputerRunId must be a UUID generated for this dedicated test run.'
}
$realCodexVersion = Require-Text $acceptance 'realCodexVersion' 'Acceptance evidence'
if ($realCodexVersion.Length -gt 120) { throw 'realCodexVersion is too long.' }

$manifestIdentityValid = [int]$manifest.schemaVersion -ge 2 -and `
  ([string]$manifest.product) -ceq 'CodexTotalManager' -and `
  ([string]$manifest.productVersion) -ceq $ProductVersion
if (-not $manifestIdentityValid) {
  throw 'Candidate manifest product or version does not match.'
}
if ([string]$manifest.releaseDecision -cne 'READY_FOR_EXTERNAL_BUSINESS_VALIDATION') {
  throw 'Candidate manifest is not ready for external business validation.'
}
$automatedChecksPassed = $manifest.verification.buildPassed -eq $true -and `
  $manifest.verification.securityTestsPassed -eq $true -and `
  $manifest.verification.integrationTestsPassed -eq $true
if (-not $automatedChecksPassed) {
  throw 'Candidate manifest does not prove build, security and isolated integration success.'
}
if (@($manifest.files).Count -lt 1) { throw 'Candidate manifest has no file hash list.' }

$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToUpperInvariant()
$claimedHash = (Require-Text $acceptance 'candidateManifestSha256' 'Acceptance evidence').ToUpperInvariant()
if ($claimedHash -notmatch '^[0-9A-F]{64}$' -or $claimedHash -cne $manifestHash) {
  throw 'Acceptance evidence is not bound to this candidate manifest.'
}

$executedText = Require-Text $acceptance 'executedAtUtc' 'Acceptance evidence'
$executedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($executedText, [ref]$executedAt)) {
  throw 'executedAtUtc is invalid.'
}
$generatedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$manifest.generatedAt, [ref]$generatedAt)) {
  throw 'Candidate manifest generatedAt is invalid.'
}
if ($executedAt.ToUniversalTime() -lt $generatedAt.ToUniversalTime()) {
  throw 'Acceptance predates the candidate manifest.'
}
if ($executedAt.ToUniversalTime() -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
  throw 'Acceptance time is in the future.'
}

$checksProperty = $acceptance.PSObject.Properties['checks']
if (-not $checksProperty -or $null -eq $checksProperty.Value) { throw 'Acceptance evidence is missing checks.' }
$requiredChecks = @(
  'officialModelMessaging',
  'officialStreamingToolCalls',
  'thirdPartyModelMessaging',
  'thirdPartyToolCalls',
  'conversationContinuity',
  'accountPoolSwitch',
  'billingAttribution',
  'codexNotRestarted',
  'skinCompatibility',
  'disconnectRestoresConfiguration'
)
foreach ($name in $requiredChecks) {
  $property = $checksProperty.Value.PSObject.Properties[$name]
  if (-not $property -or $property.Value -ne $true) {
    throw "Required real Codex acceptance check did not pass: $name"
  }
}

[ordered]@{
  schemaVersion = 1
  valid = $true
  product = 'CodexTotalManager'
  productVersion = $ProductVersion
  candidateManifestSha256 = $manifestHash
  acceptanceEvidenceSha256 = (Get-FileHash -LiteralPath $acceptancePath -Algorithm SHA256).Hash.ToUpperInvariant()
  testComputerRunId = $runId
  realCodexVersion = $realCodexVersion
  executedAtUtc = $executedAt.ToUniversalTime().ToString('o')
  passedChecks = @($requiredChecks)
} | ConvertTo-Json -Depth 5 -Compress
