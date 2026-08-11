<#
  生成发布证据(evidence)目录:
  - build-manifest.json    构建清单(提交哈希/SDK/时间/文件数)
  - test-evidence.json     测试证据(集成测试+自测结果)
  - DEPLOYABLE.json        部署决策(按 RUNTIME-BOUNDARIES 要求)
用法: .\scripts\emit-evidence.ps1
#>
[CmdletBinding()]
param(
  [string]$BuildProfile = 'Debug',
  [switch]$MarkDeployable
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$evidenceDir = Join-Path $repoRoot 'evidence'
$runDir = Join-Path $evidenceDir ("run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

# --- git 信息 ---
$gitResult = git -C $repoRoot rev-parse HEAD 2>$null; $gitHash = if ($gitResult) { $gitResult } else { "no-git" }
$gitShort = $gitHash.Substring(0, [Math]::Min(12, $gitHash.Length))
$gitStatusRaw = @(git -C $repoRoot status --porcelain=v1 --untracked-files=all 2>$null)
$gitStatus = @($gitStatusRaw |
  ForEach-Object { ($_ -replace '^[ MARC?]{1,2}\s+', '').Trim('"') } |
  Where-Object { $_ -and $_ -notmatch '^(?:out|bin|obj|evidence)(?:[/\\]|$)' })
$gitClean = $gitStatus.Count -eq 0
$gitLog = (git -C $repoRoot log --oneline -5 2>$null) -join "`n"

# --- SDK 信息（优先 ~/.dotnet 的 SDK 10，避免 PATH 里旧版本）---
$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$dotnetExe = if ($userProfile) { Join-Path $userProfile '.dotnet\dotnet.exe' } else { $null }
if (-not $dotnetExe -or -not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
$sdkResult = if ($dotnetExe) { & $dotnetExe --version 2>$null } else { $null }
$sdkVersion = if ($sdkResult) { $sdkResult } else { "unknown" }
$dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } elseif ($userProfile) { Join-Path $userProfile '.dotnet' } else { 'PATH' }

# Read the canonical product version from the project instead of inferring it from
# a candidate folder name. This keeps the payload, assembly and evidence aligned.
$projectFile = Join-Path $repoRoot 'src\CodexModelManager\CodexModelManager.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectFile -Raw -Encoding UTF8)
$productVersion = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($productVersion)) { throw "项目版本为空：$projectFile" }

# --- 文件统计 ---
$srcFiles = (Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -File -Include *.cs,*.xaml | Measure-Object).Count
$psFiles = (Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -File -Filter *.ps1 | Measure-Object).Count
$testFiles = (Get-ChildItem (Join-Path $repoRoot 'tests') -Recurse -File -Include *.cs | Measure-Object).Count

# --- 构建清单 ---
$buildManifest = [ordered]@{
  schemaVersion = 1
  generatedAt = (Get-Date).ToUniversalTime().ToString('o')
  gitCommit = $gitHash
  gitShort = $gitShort
  gitClean = $gitClean
  gitStatus = @($gitStatus)
  gitRecentLog = $gitLog
  product = 'CodexTotalManager'
  productVersion = $productVersion
  buildProfile = $BuildProfile
  sdkVersion = $sdkVersion
  sdkSource = $dotnetRoot
  fileCounts = [ordered]@{
    sourceCsXaml = $srcFiles
    powershellScripts = $psFiles
    testCs = $testFiles
  }
}
$buildManifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $runDir 'build-manifest.json') -Encoding UTF8

# --- 测试证据 ---
$testEvidence = [ordered]@{
  schemaVersion = 1
  generatedAt = (Get-Date).ToUniversalTime().ToString('o')
  integrationTests = '见 tests/CodexModelManager.IntegrationTests (控制台自检)'
  selfTestEntries = @(
    "--self-test: 需完整生产环境(引擎+v2rayN+账号), 沙盒下预期失败但错误清晰"
    "--theme-self-test: Dream Skin 引擎发现/主题/信任链"
    "--gateway-self-test: 网关路由发现"
    "--server-self-test: 服务器只读体检"
  )
  knownGaps = @(
    'OAuth 真实登录未端到端验证(需真实账号)'
    '换肤应用未在真 Codex 上端到端验证(需真 Codex)'
  )
}
$testEvidence | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $runDir 'test-evidence.json') -Encoding UTF8

# --- 部署决策（强制全流程验证：构建+安全测试+集成测试+工作区干净）---
$buildPassed = $false
$testsPassed = $false
$integrationPassed = $false
if ($dotnetExe) {
  # 1) 完整解决方案构建
  $buildOut = & $dotnetExe build (Join-Path $repoRoot 'CodexTotalManager.sln') -c Debug --nologo 2>&1
  $buildPassed = $LASTEXITCODE -eq 0
  # 2) 安全测试
  $testOut = & $dotnetExe test (Join-Path $repoRoot 'tests\CodexModelManager.SecurityTests\CodexModelManager.SecurityTests.csproj') --nologo 2>&1
  $testsPassed = $LASTEXITCODE -eq 0
  # 3) 主集成自检（真实链路的自动部分）
  $integrationOut = & $dotnetExe run --project (Join-Path $repoRoot 'tests\CodexModelManager.IntegrationTests\CodexModelManager.IntegrationTests.csproj') -c Debug 2>&1
  $integrationPassed = $LASTEXITCODE -eq 0
}
# 记录真实执行结果到 test-evidence（不再是描述性文字）
$executionEvidence = [ordered]@{
  buildSucceeded = $buildPassed
  securityTestsPassed = $testsPassed
  integrationTestsPassed = $integrationPassed
  workspaceClean = $gitClean
  note = if (-not $integrationPassed) { '主集成自检需要完整生产环境(引擎/v2rayN/账号)；失败不代表候选不可用，但不可作为 DEPLOYABLE 依据' } else { '' }
}
$executionEvidence | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $runDir 'execution-evidence.json') -Encoding UTF8

$eligible = $MarkDeployable -and $buildPassed -and $testsPassed -and $gitClean
# 主集成自检失败时绝不允许 DEPLOYABLE（真实链路未验证）
if ($eligible -and -not $integrationPassed) { $eligible = $false }
$decision = if ($eligible) { 'DEPLOYABLE' } else { 'CANDIDATE_ONLY' }
$reason = if ($eligible) {
  '构建+安全测试+集成测试全部通过且工作区干净；仍建议真实环境端到端验收后正式切换'
} elseif ($MarkDeployable -and -not $buildPassed) {
  '请求 DEPLOYABLE 但完整构建失败，已强制降级为候选。'
} elseif ($MarkDeployable -and -not $testsPassed) {
  '请求 DEPLOYABLE 但安全测试未全部通过，已强制降级为候选。'
} elseif ($MarkDeployable -and -not $integrationPassed) {
  '请求 DEPLOYABLE 但主集成自检未通过，已强制降级为候选。'
} elseif ($MarkDeployable -and -not $gitClean) {
  '请求 DEPLOYABLE 但工作区有未提交改动，已强制降级为候选。'
} else {
  '候选源码: 功能完整+安全修复完成, 但缺真实环境端到端验收'
}
$deployable = [ordered]@{
  schemaVersion = 1
  generatedAt = (Get-Date).ToUniversalTime().ToString('o')
  gitCommit = $gitHash
  product = 'CodexTotalManager'
  productVersion = $productVersion
  decision = $decision
  reason = $reason
  requiresBeforeProduction = @(
    '真实 Codex 上端到端验证引擎路由/换肤/子代理'
    '真实账号 OAuth 登录验证'
    '真实 CLIProxy 号池验证'
    '完整集成测试在真实环境跑通(当前依赖生产组件)'
  )
}
$deployable | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $runDir 'DEPLOYABLE.json') -Encoding UTF8

Write-Host "证据已生成: $runDir"
Write-Host "  决策: $decision"
Get-ChildItem $runDir | ForEach-Object { Write-Host "  - $($_.Name)" }
