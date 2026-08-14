<#
  CodexTotalManager 构建与候选包生成脚本。

  常用命令：
    .\build.ps1 -Release
    .\build.ps1 -Publish -Version 3.0.0-rc.27
    .\build.ps1 -Publish -DetachedOnly -Version 3.0.0-rc.27

  -Publish 会先运行安全测试和集成自检，再生成 win-x64 自包含候选包；
  不安装、不启动总管家，也不会启动 Codex。
  发布包必须同时带入经过锁定的 CLIProxyAPI、签名有效的 Node.js 22+，以及
  Node 的 LICENSE 或同版本 OpenJS 官方签名分发包。
#>
[CmdletBinding()]
param(
  [switch]$Release,
  [switch]$Publish,
  [switch]$Test,
  [switch]$DetachedOnly,
  [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
  [string]$Version,
  [string]$CliProxyApiArtifactPath,
  [string]$NodeArtifactPath,
  [string]$NodeLicensePath,
  [string]$NodeDistributionPath,
  [ValidateSet('win-x64')]
  [string]$RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$requiredSdk = '10.0.302'
$expectedCliProxySha256 = 'BD3456675B98CFF406B600D1361F1441879220CAD2DD4083B63409A09210629B'
$expectedCliProxyVersion = '7.2.104'
$temporaryInputRoot = $null

function Resolve-DotNet {
  $candidates = @()
  if ($env:DOTNET_ROOT) { $candidates += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
  $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
  if ($userProfile) { $candidates += Join-Path $userProfile '.dotnet\dotnet.exe' }
  $pathDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
  if ($pathDotnet) { $candidates += $pathDotnet }

  foreach ($candidate in @($candidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $sdks = & $candidate --list-sdks 2>$null
    if ($sdks -match [regex]::Escape($requiredSdk)) {
      Write-Host "使用 dotnet: $candidate (SDK $requiredSdk 可用)" -ForegroundColor Green
      return $candidate
    }
  }
  throw "未找到 .NET SDK $requiredSdk。请安装 .NET 10 SDK 或设置 DOTNET_ROOT。"
}

function Assert-File([string]$Path, [string]$Label) {
  if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "$Label 不存在：$Path"
  }
  return [IO.Path]::GetFullPath($Path)
}

function Get-ValidSignature([string]$Path, [string]$ExpectedSignerText) {
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
    throw "文件数字签名无效：$Path ($($signature.Status))"
  }
  $subject = [string]$signature.SignerCertificate.Subject
  if ($subject -notlike "*$ExpectedSignerText*") {
    throw "文件签名者不符合预期：$Path ($subject)"
  }
  return $subject
}

function Resolve-NodeExecutable {
  if (-not [string]::IsNullOrWhiteSpace($NodeArtifactPath)) {
    return Assert-File $NodeArtifactPath 'Node.js 可执行文件'
  }
  $command = Get-Command node.exe -ErrorAction SilentlyContinue
  if (-not $command) { $command = Get-Command node -ErrorAction SilentlyContinue }
  if (-not $command) {
    throw '没有找到 Node.js。生成候选包必须通过 -NodeArtifactPath 提供官方签名的 Node.js 22 或以上版本。'
  }
  return Assert-File $command.Source 'Node.js 可执行文件'
}

function Resolve-InstalledNodeDistribution([string]$NodeVersion) {
  if (-not [string]::IsNullOrWhiteSpace($NodeDistributionPath)) {
    return Assert-File $NodeDistributionPath 'Node.js 官方分发包'
  }
  $userDataRoot = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData'
  foreach ($identity in @(Get-ChildItem -LiteralPath $userDataRoot -ErrorAction SilentlyContinue)) {
    foreach ($product in @(Get-ChildItem -LiteralPath (Join-Path $identity.PSPath 'Products') -ErrorAction SilentlyContinue)) {
      $propertiesPath = Join-Path $product.PSPath 'InstallProperties'
      if (-not (Test-Path -LiteralPath $propertiesPath)) { continue }
      $properties = Get-ItemProperty -LiteralPath $propertiesPath -ErrorAction SilentlyContinue
      if ($properties.DisplayName -eq 'Node.js' -and $properties.DisplayVersion -eq $NodeVersion -and
          (Test-Path -LiteralPath $properties.LocalPackage -PathType Leaf)) {
        return [IO.Path]::GetFullPath([string]$properties.LocalPackage)
      }
    }
  }
  return $null
}

function Copy-BuildInput([string]$Source, [string]$Name) {
  $destination = Join-Path $temporaryInputRoot $Name
  Copy-Item -LiteralPath $Source -Destination $destination -Force
  return $destination
}

function Assert-PathBelow([string]$Path, [string]$Parent, [string]$Label) {
  $full = [IO.Path]::GetFullPath($Path)
  $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
  if (-not ($full + '\').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Label 越过允许的目录边界：$full"
  }
  return $full
}

$dotnet = Resolve-DotNet
$config = if ($Release -or $Publish) { 'Release' } else { 'Debug' }
$sln = Join-Path $repoRoot 'CodexTotalManager.sln'
$project = Join-Path $repoRoot 'src\CodexModelManager\CodexModelManager.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)
$versionPropertyGroup = @($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1)
$projectVersion = if ($versionPropertyGroup.Count -eq 1) { [string]$versionPropertyGroup[0].Version } else { '' }
if ([string]::IsNullOrWhiteSpace($projectVersion)) { throw '项目版本为空，无法构建。' }
$productVersion = if ([string]::IsNullOrWhiteSpace($Version)) { $projectVersion } else { $Version }
$msbuildVersionArgs = @("-p:Version=$productVersion")
$securityTestsPassed = $false
$integrationTestsPassed = $false
$runTests = $Test -or $Publish

if ($runTests) {
  $existingCliProxy = Join-Path $repoRoot 'out\publish\Resources\CLIProxyAPI\cli-proxy-api.exe'
  $workspaceCliProxy = Join-Path (Split-Path -Parent $repoRoot) '.tools\CLIProxyAPI-7.2.104\cli-proxy-api.exe'
  if ([string]::IsNullOrWhiteSpace($CliProxyApiArtifactPath) -and (Test-Path -LiteralPath $existingCliProxy -PathType Leaf)) {
    $CliProxyApiArtifactPath = $existingCliProxy
  }
  if ([string]::IsNullOrWhiteSpace($CliProxyApiArtifactPath) -and (Test-Path -LiteralPath $workspaceCliProxy -PathType Leaf)) {
    $CliProxyApiArtifactPath = $workspaceCliProxy
  }
  $CliProxyApiArtifactPath = Assert-File $CliProxyApiArtifactPath 'CLIProxyAPI 外部制品'
  $testCliProxyHash = (Get-FileHash -LiteralPath $CliProxyApiArtifactPath -Algorithm SHA256).Hash
  if ($testCliProxyHash -ine $expectedCliProxySha256) {
    throw "CLIProxyAPI 测试制品哈希不匹配。预期 $expectedCliProxySha256，实际 $testCliProxyHash。"
  }
}

Write-Host "`n==== 构建 ($config) ====" -ForegroundColor Cyan
& $dotnet build $sln -c $config --nologo @msbuildVersionArgs
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }

if ($runTests) {
  Write-Host "`n==== 运行安全测试 ====" -ForegroundColor Cyan
  $securityProject = Join-Path $repoRoot 'tests\CodexModelManager.SecurityTests\CodexModelManager.SecurityTests.csproj'
  & $dotnet test $securityProject -c $config --nologo --no-build
  if ($LASTEXITCODE -ne 0) { throw '安全测试失败。' }
  $securityTestsPassed = $true

  Write-Host "`n==== 运行集成自检 ====" -ForegroundColor Cyan
  $integrationProject = Join-Path $repoRoot 'tests\CodexModelManager.IntegrationTests\CodexModelManager.IntegrationTests.csproj'
  $previousCliProxyArtifact = [Environment]::GetEnvironmentVariable('CMM_TEST_CLIPROXY_ARTIFACT', 'Process')
  try {
    [Environment]::SetEnvironmentVariable(
      'CMM_TEST_CLIPROXY_ARTIFACT',
      $CliProxyApiArtifactPath,
      'Process')
    & $dotnet run --project $integrationProject -c $config --no-build
    if ($LASTEXITCODE -ne 0) { throw '集成自检失败。' }
  } finally {
    [Environment]::SetEnvironmentVariable(
      'CMM_TEST_CLIPROXY_ARTIFACT',
      $previousCliProxyArtifact,
      'Process')
  }
  $integrationTestsPassed = $true
}

if ($Publish) {
  Write-Host "`n==== 生成自包含候选包 ====" -ForegroundColor Cyan
  $outDir = Join-Path $repoRoot 'out\publish'
  $outFull = Assert-PathBelow $outDir $repoRoot '发布目录'
  $cliProxySource = Assert-File $CliProxyApiArtifactPath 'CLIProxyAPI 外部制品'
  $cliProxyHash = (Get-FileHash -LiteralPath $cliProxySource -Algorithm SHA256).Hash
  if ($cliProxyHash -ine $expectedCliProxySha256) {
    throw "CLIProxyAPI 哈希不匹配。预期 $expectedCliProxySha256，实际 $cliProxyHash。"
  }

  $nodeSource = Resolve-NodeExecutable
  $nodeSigner = Get-ValidSignature $nodeSource 'OpenJS Foundation'
  $nodeVersion = ((& $nodeSource -p 'process.versions.node') -join '').Trim()
  if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "无法读取 Node.js 版本：$nodeSource"
  }
  $nodeMajor = [int]($nodeVersion -split '\.')[0]
  if ($nodeMajor -lt 22) { throw "Dream Skin 要求 Node.js 22 或以上，实际为 $nodeVersion。" }
  $nodeHash = (Get-FileHash -LiteralPath $nodeSource -Algorithm SHA256).Hash

  $licenseSource = $null
  if (-not [string]::IsNullOrWhiteSpace($NodeLicensePath)) {
    $licenseSource = Assert-File $NodeLicensePath 'Node.js LICENSE'
  }
  $distributionSource = Resolve-InstalledNodeDistribution $nodeVersion
  $distributionSigner = $null
  $distributionHash = $null
  if ($distributionSource) {
    $extension = [IO.Path]::GetExtension($distributionSource)
    if ($extension -ine '.msi') { throw "Node.js 官方分发包必须是 .msi：$distributionSource" }
    $distributionSigner = Get-ValidSignature $distributionSource 'OpenJS Foundation'
    $distributionHash = (Get-FileHash -LiteralPath $distributionSource -Algorithm SHA256).Hash
  }
  if (-not $licenseSource -and -not $distributionSource) {
    throw '缺少 Node.js 许可证材料。请提供 -NodeLicensePath，或提供同版本 OpenJS 官方签名 MSI（-NodeDistributionPath）。'
  }

  $temporaryInputRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexTotalManager-build-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $temporaryInputRoot -Force | Out-Null
  try {
    $stagedCliProxy = Copy-BuildInput $cliProxySource 'cli-proxy-api.exe'
    $stagedNode = Copy-BuildInput $nodeSource 'node.exe'
    $stagedLicense = if ($licenseSource) { Copy-BuildInput $licenseSource 'NODE-LICENSE.txt' } else { $null }
    $stagedDistribution = if ($distributionSource) { Copy-BuildInput $distributionSource 'NODE-OFFICIAL-DISTRIBUTION.msi' } else { $null }

    if (Test-Path -LiteralPath $outFull) { Remove-Item -LiteralPath $outFull -Recurse -Force }
    New-Item -ItemType Directory -Path $outFull -Force | Out-Null
    $publishModeArgs = if ($DetachedOnly) { @('-p:DetachedOnly=true') } else { @() }
    & $dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained true -o $outFull --nologo `
      @msbuildVersionArgs "-p:CliProxyApiArtifactPath=$stagedCliProxy" @publishModeArgs `
      '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw '自包含发布失败。' }

    $nodeRuntimeRoot = Join-Path $outFull 'Resources\CodexDreamSkin\runtime\node'
    New-Item -ItemType Directory -Path $nodeRuntimeRoot -Force | Out-Null
    Copy-Item -LiteralPath $stagedNode -Destination (Join-Path $nodeRuntimeRoot 'node.exe') -Force
    if ($stagedLicense) {
      Copy-Item -LiteralPath $stagedLicense -Destination (Join-Path $nodeRuntimeRoot 'LICENSE.txt') -Force
    }
    if ($stagedDistribution) {
      Copy-Item -LiteralPath $stagedDistribution -Destination (Join-Path $nodeRuntimeRoot 'NODE-OFFICIAL-DISTRIBUTION.msi') -Force
    }

    $packageDeployment = Join-Path $outFull 'deployment'
    New-Item -ItemType Directory -Path $packageDeployment -Force | Out-Null
    foreach ($name in @('Open-New-Manager-ControlPanel.ps1', 'Launch-Manager-Hidden.vbs')) {
      Copy-Item -LiteralPath (Join-Path $repoRoot "deployment\$name") -Destination (Join-Path $packageDeployment $name) -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install-local-release.ps1') `
      -Destination (Join-Path $outFull 'install-local-release.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\uninstall-local-release.ps1') `
      -Destination (Join-Path $outFull 'uninstall-local-release.ps1') -Force

    Get-ChildItem -LiteralPath $outFull -Recurse -File -Filter '*.pdb' | Remove-Item -Force
    if (Test-Path -LiteralPath (Join-Path $outFull 'tools\mock-openai-server.mjs')) {
      throw '候选包错误地包含了测试用 mock-openai-server.mjs。'
    }

    $publishedExe = Join-Path $outFull 'CodexModelManager.exe'
    foreach ($required in @($publishedExe, (Join-Path $outFull 'coreclr.dll'), (Join-Path $outFull 'hostfxr.dll'),
        (Join-Path $outFull 'hostpolicy.dll'), (Join-Path $nodeRuntimeRoot 'node.exe'),
        (Join-Path $outFull 'Resources\CLIProxyAPI\cli-proxy-api.exe'))) {
      if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "自包含候选包缺少必要文件：$required" }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $nodeRuntimeRoot 'node.exe') -Algorithm SHA256).Hash -ine $nodeHash) {
      throw '候选包中的 Node.js 哈希与构建输入不一致。'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $outFull 'Resources\CLIProxyAPI\cli-proxy-api.exe') -Algorithm SHA256).Hash -ine $expectedCliProxySha256) {
      throw '候选包中的 CLIProxyAPI 哈希与锁定值不一致。'
    }
    $pdbCount = @(Get-ChildItem -LiteralPath $outFull -Recurse -File -Filter '*.pdb').Count
    $testToolPatterns = @(
      'tools/mock-openai-server.mjs',
      'CodexModelManager.IntegrationTests.dll',
      'CodexModelManager.SecurityTests.dll',
      'NativeProxySmoke.dll',
      'ExtensionTestPlugin.dll'
    )
    $testToolCount = @($testToolPatterns | Where-Object {
      $name = $_
      @(Get-ChildItem -LiteralPath $outFull -Recurse -File | Where-Object {
        $_.FullName.Substring($outFull.Length + 1).Replace('\', '/').EndsWith($name, [StringComparison]::OrdinalIgnoreCase)
      }).Count -gt 0
    }).Count
    if ($pdbCount -ne 0 -or $testToolCount -ne 0) {
      throw "候选包仍包含调试符号或测试工具：pdb=$pdbCount testTools=$testToolCount"
    }
    $publishedInfo = (Get-Item -LiteralPath $publishedExe).VersionInfo
    $publishedProductVersion = [string]$publishedInfo.ProductVersion
    if (-not ($publishedProductVersion -eq $productVersion -or
        $publishedProductVersion.StartsWith("$productVersion+", [StringComparison]::Ordinal))) {
      throw "发布 EXE 版本 $publishedProductVersion 与项目版本 $productVersion 不一致。"
    }

    $manifestPath = Join-Path $outFull 'payload-manifest.json'
    $payloadFiles = Get-ChildItem -LiteralPath $outFull -Recurse -File |
      Where-Object { $_.FullName -ne $manifestPath } |
      Sort-Object FullName |
      ForEach-Object {
        [ordered]@{
          path = $_.FullName.Substring($outFull.Length + 1).Replace('\', '/')
          bytes = $_.Length
          sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
      }
    $decision = if ($securityTestsPassed -and $integrationTestsPassed) {
      'READY_FOR_EXTERNAL_BUSINESS_VALIDATION'
    } else { 'CANDIDATE_ONLY' }
    [ordered]@{
      schemaVersion = 2
      product = 'CodexTotalManager'
      productVersion = $productVersion
      fileVersion = $publishedInfo.FileVersion
      assemblyProductVersion = $publishedProductVersion
      gitCommit = $null
      gitDirty = $null
      gitDirtyFileCount = $null
      gitStatusSha256 = $null
      sourceRevision = 'LOCAL_WORKSPACE_NO_GIT'
      sourceControlPolicy = 'Git was intentionally not queried or used.'
      generatedAt = (Get-Date).ToUniversalTime().ToString('o')
      releaseDecision = $decision
      platform = [ordered]@{
        os = 'windows'
        architecture = 'x64'
        runtimeIdentifier = $RuntimeIdentifier
        targetFramework = 'net10.0-windows'
        dotnetSelfContained = $true
      }
      isolation = [ordered]@{
        codexMode = if ($DetachedOnly) { 'DETACHED_ONLY' } else { 'USER_CONTROLLED_DEFAULT_OFF' }
        realCodexAccess = -not $DetachedOnly
        gatewayCommandEnabled = -not $DetachedOnly
        defaultConnected = $false
        connectionRequiresInAppConfirmation = -not $DetachedOnly
        externalStatusConnectionsDefault = $true
      }
      dependencies = [ordered]@{
        dotnet = [ordered]@{ selfContained = $true; targetMajor = 10 }
        node = [ordered]@{
          version = $nodeVersion
          minimumMajor = 22
          path = 'Resources/CodexDreamSkin/runtime/node/node.exe'
          bytes = (Get-Item -LiteralPath $nodeSource).Length
          sha256 = $nodeHash
          signature = 'Valid'
          signer = $nodeSigner
          licenseMaterial = if ($licenseSource) { 'LICENSE.txt' } else { 'OpenJS signed official MSI' }
          distributionSha256 = $distributionHash
          distributionSigner = $distributionSigner
        }
        cliProxyApi = [ordered]@{
          version = $expectedCliProxyVersion
          path = 'Resources/CLIProxyAPI/cli-proxy-api.exe'
          bytes = (Get-Item -LiteralPath $cliProxySource).Length
          sha256 = $expectedCliProxySha256
          signature = 'Unsigned; exact SHA-256 required'
          securitySoftwareNotice = 'Security software may quarantine this unsigned executable; restore only after the exact SHA-256 is confirmed.'
        }
      }
      package = [ordered]@{
        installerIncluded = $true
        uninstallerIncluded = $true
        pdbCount = $pdbCount
        testToolCount = $testToolCount
      }
      verification = [ordered]@{
        buildPassed = $true
        securityTestsPassed = $securityTestsPassed
        integrationTestsPassed = $integrationTestsPassed
        externalBusinessValidation = 'PENDING_ON_SEPARATE_TEST_COMPUTER'
      }
      files = @($payloadFiles)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Host "`n==== 验证候选包安装回路（不安装） ====" -ForegroundColor Cyan
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass `
      -File (Join-Path $outFull 'install-local-release.ps1') `
      -PublishDirectory $outFull -IsolatedTestMachine -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw '候选包安装回路验证失败。' }
    Write-Host "候选包清单: $manifestPath" -ForegroundColor Green
    Write-Host "候选包目录: $outFull" -ForegroundColor Green
    Write-Host "发布决定: $decision（没有安装）" -ForegroundColor Yellow
  }
  finally {
    if ($temporaryInputRoot -and (Test-Path -LiteralPath $temporaryInputRoot)) {
      Remove-Item -LiteralPath $temporaryInputRoot -Recurse -Force
    }
  }
}

Write-Host "`n构建完成；没有安装，也没有启动 Codex。" -ForegroundColor Green
