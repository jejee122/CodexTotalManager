[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$InstallRoot,
    [switch]$IsolatedTestMachine,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = if (Test-Path -LiteralPath (Join-Path $scriptDirectory 'payload-manifest.json')) {
        $scriptDirectory
    } else {
        Join-Path $repoRoot 'out\publish'
    }
}
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexTotalManager'
}
$publishFull = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\')

function Resolve-PayloadPath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { throw '清单包含空路径。' }
    $relative = $RelativePath.Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relative)) { throw "清单包含绝对路径：$relative" }
    if ($relative.IndexOf(':', [StringComparison]::Ordinal) -ge 0) {
        throw "清单路径包含不安全的驱动器或备用数据流语法：$relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $publishFull $relative))
    if (-not $path.StartsWith($publishFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "清单路径越过候选包目录：$relative"
    }
    return $path
}

function Assert-ValidOpenJsSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $subject = [string]$signature.SignerCertificate.Subject
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $subject -notlike '*OpenJS Foundation*') {
        throw "Node.js 数字签名无效或签名者不正确：$Path"
    }
}

function Write-JsonAtomically([object]$Value, [string]$Path, [int]$Depth = 6) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $Value | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Assert-NoReparsePointBelow([string]$BasePath, [string]$TargetPath, [string]$Label) {
    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd('\')
    $targetFull = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    if (-not ($targetFull + '\').StartsWith($baseFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside its trusted base directory: $targetFull"
    }
    $relative = $targetFull.Substring($baseFull.Length).TrimStart('\')
    $cursor = $baseFull
    foreach ($part in @($relative -split '\\' | Where-Object { $_ })) {
        $cursor = Join-Path $cursor $part
        if (-not (Test-Path -LiteralPath $cursor)) { break }
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a symbolic link, junction, or other reparse point: $cursor"
        }
    }
}

function Assert-TreeHasNoReparsePoints([string]$RootPath, [string]$Label) {
    if (-not (Test-Path -LiteralPath $RootPath)) { return }
    $rootItem = Get-Item -LiteralPath $RootPath -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label is a symbolic link, junction, or other reparse point: $RootPath"
    }
    $reparse = @(Get-ChildItem -LiteralPath $RootPath -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1)
    if ($reparse.Count -gt 0) {
        throw "$Label contains a symbolic link, junction, or other reparse point: $($reparse[0].FullName)"
    }
}

function New-ManagerShortcut(
    [string]$Path,
    [string]$TargetPath,
    [string]$Arguments,
    [string]$WorkingDirectory,
    [string]$IconLocation) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $IconLocation
    $shortcut.Description = 'AI 中转站总管家'
    $shortcut.Save()
}

function Remove-ManagerShortcutIfOwned(
    [string]$Path,
    [string]$OwnedInstallRoot) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        $targetPath = [string]$shortcut.TargetPath
        $arguments = [string]$shortcut.Arguments
        $owned = ($targetPath -and $targetPath.StartsWith($OwnedInstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) -or
                 ($arguments -and $arguments.IndexOf($OwnedInstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0)
        if ($owned) { Remove-Item -LiteralPath $Path -Force }
    } catch {
        Write-Warning "无法核对旧快捷方式，已保留：$Path"
    }
}

function Read-RegistrySnapshot([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $record = Get-ItemProperty -LiteralPath $Path
    $snapshot = [ordered]@{}
    foreach ($property in @($record.PSObject.Properties | Where-Object {
        $_.Name -notmatch '^PS(Path|ParentPath|ChildName|Drive|Provider)$'
    })) {
        $snapshot[$property.Name] = $property.Value
    }
    return $snapshot
}

function Restore-RegistrySnapshot([string]$Path, [object]$Snapshot) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    if ($null -eq $Snapshot) { return }
    New-Item -Path $Path -Force | Out-Null
    foreach ($entry in $Snapshot.GetEnumerator()) {
        $kind = if ($entry.Value -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $Path -Name ([string]$entry.Key) -Value $entry.Value -PropertyType $kind -Force | Out-Null
    }
}

function Assert-SupportedCodexIsolation([object]$Manifest) {
    $mode = [string]$Manifest.isolation.codexMode
    $realCodexAccess = [bool]$Manifest.isolation.realCodexAccess
    $gatewayCommandEnabled = [bool]$Manifest.isolation.gatewayCommandEnabled
    $defaultConnected = [bool]$Manifest.isolation.defaultConnected
    $requiresConfirmation = [bool]$Manifest.isolation.connectionRequiresInAppConfirmation

    if ($mode -eq 'DETACHED_ONLY') {
        if ($realCodexAccess -or $gatewayCommandEnabled -or $defaultConnected -or $requiresConfirmation) {
            throw '永久隔离候选包的 Codex 权限声明互相矛盾，禁止安装。'
        }
        return
    }

    if ($mode -eq 'USER_CONTROLLED_DEFAULT_OFF') {
        if (-not $realCodexAccess -or -not $gatewayCommandEnabled -or $defaultConnected -or -not $requiresConfirmation) {
            throw '用户控制候选包必须默认断开，并且连接真实 Codex 前必须在应用内确认。'
        }
        return
    }

    throw "候选包声明了不受支持的 Codex 隔离模式：$mode"
}

if (-not (Test-Path -LiteralPath $publishFull -PathType Container)) {
    throw "候选包目录不存在：$publishFull"
}
$publishPathRoot = [IO.Path]::GetPathRoot($publishFull).TrimEnd('\')
if ($publishFull.Equals($publishPathRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "候选包目录不能是磁盘根目录：$publishFull"
}
Assert-TreeHasNoReparsePoints $publishFull '候选包目录'
$manifestPath = Join-Path $publishFull 'payload-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw '候选包缺少 payload-manifest.json。' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.product -ne 'CodexTotalManager') {
    throw '候选包身份或清单版本不受支持。'
}
if (@($manifest.files).Count -le 0) { throw '候选包清单没有文件记录。' }
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows) -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw '这个候选包只允许安装到 Windows x64。'
}
if ($manifest.platform.runtimeIdentifier -ne 'win-x64' -or -not $manifest.platform.dotnetSelfContained) {
    throw '候选包不是 win-x64 自包含版本；不会依赖目标电脑另装 .NET。'
}
Assert-SupportedCodexIsolation $manifest
if (-not $manifest.package.installerIncluded -or -not $manifest.package.uninstallerIncluded -or
    [int]$manifest.package.pdbCount -ne 0 -or [int]$manifest.package.testToolCount -ne 0) {
    throw '候选包内容不完整，或混入了调试/测试文件。'
}
if (-not $manifest.verification.buildPassed -or
    -not $manifest.verification.securityTestsPassed -or
    -not $manifest.verification.integrationTestsPassed) {
    throw '这个候选包还没有在测试电脑通过构建、安全测试和集成自检，禁止安装。'
}
if ([bool]$manifest.gitDirty) {
    throw '这个候选包来自有未提交改动的源码，不能可靠复现，禁止安装。'
}
$approvalPath = Join-Path $publishFull 'DEPLOYABLE.json'
$approval = $null
if (Test-Path -LiteralPath $approvalPath -PathType Leaf) {
    try { $approval = Get-Content -LiteralPath $approvalPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "正式批准文件不是合法 JSON：$($_.Exception.Message)" }
}
if ($IsolatedTestMachine) {
    if ($manifest.releaseDecision -ne 'READY_FOR_EXTERNAL_BUSINESS_VALIDATION') {
        throw "候选包还没达到独立测试机安装门槛：$($manifest.releaseDecision)"
    }
} else {
    if ($null -eq $approval -or [int]$approval.schemaVersion -ne 1 -or
        [string]$approval.decision -cne 'DEPLOYABLE' -or
        [string]$approval.product -cne 'CodexTotalManager' -or
        [string]$approval.productVersion -cne [string]$manifest.productVersion -or
        $approval.externalAcceptance.valid -ne $true) {
        throw '这个包没有通过与本包绑定的专用测试电脑真实验收，禁止安装到正式电脑。'
    }
    $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $approvedSha256 = ([string]$approval.externalAcceptance.candidateManifestSha256).ToUpperInvariant()
    if ($approvedSha256 -notmatch '^[0-9A-F]{64}$' -or $approvedSha256 -cne $manifestSha256) {
        throw '正式批准文件绑定的是另一个候选包，禁止安装。'
    }
}

$manifestEntries = @{}
foreach ($file in @($manifest.files)) {
    $relative = ([string]$file.path).Replace('/', '\')
    if ($manifestEntries.ContainsKey($relative)) { throw "清单包含重复路径：$relative" }
    $path = Resolve-PayloadPath $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "候选包缺少文件：$relative" }
    $item = Get-Item -LiteralPath $path
    if ([long]$item.Length -ne [long]$file.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine [string]$file.sha256) {
        throw "候选包文件校验失败：$relative"
    }
    $manifestEntries[$relative] = $true
}
$extras = @(Get-ChildItem -LiteralPath $publishFull -Recurse -File |
    Where-Object { $_.FullName -ne $manifestPath -and $_.FullName -ne $approvalPath } |
    Where-Object { -not $manifestEntries.ContainsKey($_.FullName.Substring($publishFull.Length + 1)) })
if ($extras.Count -gt 0) { throw '候选包目录里存在未写入清单的多余文件。' }
if (@(Get-ChildItem -LiteralPath $publishFull -Recurse -File -Filter '*.pdb').Count -gt 0 -or
    (Test-Path -LiteralPath (Join-Path $publishFull 'tools\mock-openai-server.mjs'))) {
    throw '候选包混入了 PDB 或 mock 测试工具。'
}

$nodeRelative = [string]$manifest.dependencies.node.path
$nodePath = Resolve-PayloadPath $nodeRelative
$nodeVersion = [string]$manifest.dependencies.node.version
if ($nodeVersion -notmatch '^\d+\.\d+\.\d+$' -or [int]($nodeVersion -split '\.')[0] -lt 22) {
    throw "Node.js 版本不满足 Dream Skin 要求：$nodeVersion"
}
if ((Get-FileHash -LiteralPath $nodePath -Algorithm SHA256).Hash -ine [string]$manifest.dependencies.node.sha256) {
    throw 'Node.js 哈希与依赖清单不一致。'
}
Assert-ValidOpenJsSignature $nodePath
$nodeReportedVersion = ((& $nodePath -p 'process.versions.node') -join '').Trim()
if ($LASTEXITCODE -ne 0 -or $nodeReportedVersion -ne $nodeVersion) {
    throw "Node.js 运行时自报版本与清单不一致：$nodeReportedVersion / $nodeVersion"
}
$licenseMaterial = [string]$manifest.dependencies.node.licenseMaterial
if ($licenseMaterial -eq 'LICENSE.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $nodePath) 'LICENSE.txt') -PathType Leaf)) {
        throw 'Node.js LICENSE.txt 缺失。'
    }
} elseif ($licenseMaterial -eq 'OpenJS signed official MSI') {
    $distribution = Join-Path (Split-Path -Parent $nodePath) 'NODE-OFFICIAL-DISTRIBUTION.msi'
    if (-not (Test-Path -LiteralPath $distribution -PathType Leaf) -or
        (Get-FileHash -LiteralPath $distribution -Algorithm SHA256).Hash -ine [string]$manifest.dependencies.node.distributionSha256) {
        throw 'Node.js 官方分发包缺失或哈希不一致。'
    }
    Assert-ValidOpenJsSignature $distribution
} else {
    throw 'Node.js 许可证材料类型不受支持。'
}

$cliProxyPath = Resolve-PayloadPath ([string]$manifest.dependencies.cliProxyApi.path)
if ((Get-FileHash -LiteralPath $cliProxyPath -Algorithm SHA256).Hash -ine [string]$manifest.dependencies.cliProxyApi.sha256 -or
    [string]$manifest.dependencies.cliProxyApi.sha256 -ine '0A8FFC52DFB2A466BAA1B006341B350BDB1F76FC70B6CC80375BB99AFDFF697B') {
    throw 'CLIProxyAPI 没有通过固定 SHA-256 校验。它没有数字签名，不能放宽检查。'
}

if ($ValidateOnly) {
    Write-Output "LOCAL_RELEASE_VALIDATED version=$($manifest.productVersion) mode=$($manifest.isolation.codexMode) files=$(@($manifest.files).Count)"
    exit 0
}

$installFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$localDataFull = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData')).TrimEnd('\')
if ($installFull.Equals($localDataFull, [StringComparison]::OrdinalIgnoreCase) -or
    -not ($installFull + '\').StartsWith($localDataFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "安装目录必须严格位于 LocalApplicationData 下面：$installFull"
}
Assert-NoReparsePointBelow $localDataFull $installFull '安装目录'
Assert-TreeHasNoReparsePoints $installFull '现有安装目录'

$version = [string]$manifest.productVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "版本号不安全：$version" }
$releaseRoot = Join-Path $installFull 'releases'
$destination = Join-Path $releaseRoot $version
$staging = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$rollbackRoot = Join-Path $installFull ('.install-rollback-' + [Guid]::NewGuid().ToString('N'))
$runtimeRoot = Join-Path $installFull 'runtime-v3'
$pointerPath = Join-Path $runtimeRoot 'active-release.json'
$previousPointerPath = Join-Path $runtimeRoot 'active-release.previous.json'
$acceptancePath = Join-Path $runtimeRoot 'deployment-acceptance-v1.json'
$managedRootFiles = @(
    'Open-New-Manager-ControlPanel.ps1',
    'Launch-Manager-Hidden.vbs',
    'Uninstall-Manager.vbs',
    'uninstall-local-release.ps1'
)
$managedStateFiles = @($pointerPath, $previousPointerPath, $acceptancePath)
$programsRoot = [Environment]::GetFolderPath('Programs')
$desktopRoot = [Environment]::GetFolderPath('DesktopDirectory')
$startMenuFolder = Join-Path $programsRoot 'AI 中转站总管家'
$legacyStartMenuFolder = Join-Path $programsRoot 'Codex 总管家'
$managedShortcutPaths = @(
    (Join-Path $startMenuFolder 'AI 中转站总管家.lnk'),
    (Join-Path $startMenuFolder '卸载 AI 中转站总管家.lnk'),
    (Join-Path $desktopRoot 'AI 中转站总管家.lnk'),
    (Join-Path $legacyStartMenuFolder 'Codex 总管家.lnk'),
    (Join-Path $legacyStartMenuFolder '卸载 Codex 总管家.lnk'),
    (Join-Path $desktopRoot 'Codex 总管家.lnk')
)
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexTotalManager'
$runRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallRegistrySnapshot = Read-RegistrySnapshot $uninstallRegistryPath
$runValueSnapshot = if (Test-Path -LiteralPath $runRegistryPath) {
    [string](Get-ItemPropertyValue -LiteralPath $runRegistryPath -Name 'CodexTotalManager' -ErrorAction SilentlyContinue)
} else { '' }
$startMenuFolderExisted = Test-Path -LiteralPath $startMenuFolder
$destinationCreated = $false
$installRootExisted = Test-Path -LiteralPath $installFull
$releaseRootExisted = Test-Path -LiteralPath $releaseRoot
$runtimeRootExisted = Test-Path -LiteralPath $runtimeRoot

if (Test-Path -LiteralPath $destination) { throw "同版本目录已经存在：$destination" }

try {
    New-Item -ItemType Directory -Path $installFull -Force | Out-Null
    New-Item -ItemType Directory -Path $rollbackRoot -Force | Out-Null
    foreach ($name in $managedRootFiles) {
        $current = Join-Path $installFull $name
        if (Test-Path -LiteralPath $current -PathType Leaf) {
            Copy-Item -LiteralPath $current -Destination (Join-Path $rollbackRoot ('root-' + $name)) -Force
        }
    }
    foreach ($statePath in $managedStateFiles) {
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            Copy-Item -LiteralPath $statePath -Destination (Join-Path $rollbackRoot ('state-' + (Split-Path -Leaf $statePath))) -Force
        }
    }
    for ($shortcutIndex = 0; $shortcutIndex -lt $managedShortcutPaths.Count; $shortcutIndex++) {
        $shortcutPath = $managedShortcutPaths[$shortcutIndex]
        if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
            Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $rollbackRoot ("shortcut-$shortcutIndex.lnk")) -Force
        }
    }

    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Get-ChildItem -LiteralPath $publishFull -Force | Copy-Item -Destination $staging -Recurse -Force
    foreach ($file in @($manifest.files)) {
        $copied = Join-Path $staging (([string]$file.path).Replace('/', '\'))
        if ((Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash -ine [string]$file.sha256) {
            throw "复制后的文件校验失败：$($file.path)"
        }
    }
    Move-Item -LiteralPath $staging -Destination $destination
    $destinationCreated = $true

    $installedManifest = Join-Path $destination 'payload-manifest.json'
    $installedExe = Join-Path $destination 'CodexModelManager.exe'
    $installedInfo = (Get-Item -LiteralPath $installedExe).VersionInfo
    if ($installedInfo.ProductName -ne 'AI 中转站总管家') {
        throw "程序名称不符合预期：$($installedInfo.ProductName)"
    }
    if (-not ($installedInfo.ProductVersion -eq $version -or
        $installedInfo.ProductVersion.StartsWith($version + '+', [StringComparison]::Ordinal))) {
        throw "程序版本 $($installedInfo.ProductVersion) 与清单 $version 不一致。"
    }

    foreach ($name in $managedRootFiles) {
        $source = if ($name -eq 'uninstall-local-release.ps1') {
            Join-Path $publishFull $name
        } else {
            Join-Path $publishFull "deployment\$name"
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "安装控制文件缺失：$name" }
        $temporary = Join-Path $installFull ('.new-' + [Guid]::NewGuid().ToString('N') + '-' + $name)
        Copy-Item -LiteralPath $source -Destination $temporary -Force
        Move-Item -LiteralPath $temporary -Destination (Join-Path $installFull $name) -Force
    }

    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    $installedAt = (Get-Date).ToUniversalTime().ToString('o')
    $pointer = [ordered]@{
        schemaVersion = 2
        product = 'CodexTotalManager'
        productVersion = $version
        fileVersion = [string]$installedInfo.FileVersion
        sourceRevision = [string]$manifest.sourceRevision
        relativeExecutable = $installedExe.Substring($installFull.Length + 1).Replace('\', '/')
        payloadManifest = $installedManifest.Substring($installFull.Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
        payloadManifestSha256 = (Get-FileHash -LiteralPath $installedManifest -Algorithm SHA256).Hash
        payloadFileCount = @($manifest.files).Count
        runtimeIdentifier = [string]$manifest.platform.runtimeIdentifier
        codexIsolationMode = [string]$manifest.isolation.codexMode
        installedAt = $installedAt
    }
    $acceptance = [ordered]@{
        schemaVersion = 3
        product = 'CodexTotalManager'
        productVersion = $version
        sourceRevision = [string]$manifest.sourceRevision
        decision = if ($IsolatedTestMachine) { 'ISOLATED_TEST_INSTALL' } else { 'LOCAL_RELEASE_ACCEPTED' }
        productionStatus = [string]$manifest.releaseDecision
        installedAt = $installedAt
        verification = [ordered]@{
            payloadManifestVerified = $true
            executableVersionVerified = $true
            architectureVerified = $true
            selfContainedDotnetVerified = $true
            bundledNodeVerified = $true
            cliProxyHashVerified = $true
            codexIsolationVerified = $true
            securityTestsPassed = [bool]$manifest.verification.securityTestsPassed
            integrationTestsPassed = [bool]$manifest.verification.integrationTestsPassed
            externalBusinessValidation = [string]$manifest.verification.externalBusinessValidation
        }
    }

    if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
        Copy-Item -LiteralPath $pointerPath -Destination ($previousPointerPath + '.new') -Force
        Move-Item -LiteralPath ($previousPointerPath + '.new') -Destination $previousPointerPath -Force
    }
    Write-JsonAtomically $acceptance $acceptancePath 6
    Write-JsonAtomically $pointer $pointerPath 5

    $stableLauncher = Join-Path $installFull 'Launch-Manager-Hidden.vbs'
    $friendlyUninstaller = Join-Path $installFull 'Uninstall-Manager.vbs'
    $uninstallScript = Join-Path $installFull 'uninstall-local-release.ps1'
    $quotedLauncher = '"' + $stableLauncher + '"'
    $quotedUninstaller = '"' + $uninstallScript + '"'
    New-ManagerShortcut $managedShortcutPaths[0] 'wscript.exe' $quotedLauncher $installFull $installedExe
    New-ManagerShortcut $managedShortcutPaths[1] 'wscript.exe' `
        ('"' + $friendlyUninstaller + '"') $installFull $installedExe
    New-ManagerShortcut $managedShortcutPaths[2] 'wscript.exe' $quotedLauncher $installFull $installedExe
    foreach ($legacyShortcut in $managedShortcutPaths[3..5]) {
        Remove-ManagerShortcutIfOwned $legacyShortcut $installFull
    }
    if ((Test-Path -LiteralPath $legacyStartMenuFolder -PathType Container) -and
        (Get-ChildItem -LiteralPath $legacyStartMenuFolder -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $legacyStartMenuFolder -Force
    }

    if (Test-Path -LiteralPath $uninstallRegistryPath) {
        Remove-Item -LiteralPath $uninstallRegistryPath -Recurse -Force
    }
    New-Item -Path $uninstallRegistryPath -Force | Out-Null
    $uninstallCommand = 'wscript.exe "' + $friendlyUninstaller + '"'
    $quietUninstallCommand = 'powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ' + $quotedUninstaller
    $estimatedSizeKb = [int][Math]::Min(
        [int]::MaxValue,
        [Math]::Ceiling((Get-ChildItem -LiteralPath $destination -Recurse -File | Measure-Object Length -Sum).Sum / 1KB))
    $versionParts = $version.Split('-', 2)[0].Split('.')
    $registration = [ordered]@{
        DisplayName = 'AI 中转站总管家'
        DisplayVersion = $version
        Publisher = 'jejee122'
        DisplayIcon = $installedExe
        InstallLocation = $installFull
        UninstallString = $uninstallCommand
        QuietUninstallString = $quietUninstallCommand
        URLInfoAbout = 'https://github.com/jejee122/CodexTotalManager'
        NoModify = 1
        NoRepair = 1
        EstimatedSize = $estimatedSizeKb
        VersionMajor = [int]$versionParts[0]
        VersionMinor = [int]$versionParts[1]
    }
    foreach ($entry in $registration.GetEnumerator()) {
        $propertyType = if ($entry.Value -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $uninstallRegistryPath -Name ([string]$entry.Key) `
            -Value $entry.Value -PropertyType $propertyType -Force | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($runValueSnapshot) -and
        $runValueSnapshot.IndexOf($installFull, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        New-Item -Path $runRegistryPath -Force | Out-Null
        New-ItemProperty -Path $runRegistryPath -Name 'CodexTotalManager' `
            -Value ('wscript.exe ' + $quotedLauncher) -PropertyType String -Force | Out-Null
    }

    Remove-Item -LiteralPath $rollbackRoot -Recurse -Force
    Write-Output "LOCAL_RELEASE_INSTALLED version=$version path=$destination mode=$(if ($IsolatedTestMachine) { 'isolated-test' } else { 'deployable' })"
}
catch {
    $failure = $_
    foreach ($name in $managedRootFiles) {
        $current = Join-Path $installFull $name
        $backup = Join-Path $rollbackRoot ('root-' + $name)
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Copy-Item -LiteralPath $backup -Destination $current -Force
        } elseif (Test-Path -LiteralPath $current) {
            Remove-Item -LiteralPath $current -Force
        }
    }
    foreach ($statePath in $managedStateFiles) {
        $backup = Join-Path $rollbackRoot ('state-' + (Split-Path -Leaf $statePath))
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Copy-Item -LiteralPath $backup -Destination $statePath -Force
        } elseif (Test-Path -LiteralPath $statePath) {
            Remove-Item -LiteralPath $statePath -Force
        }
    }
    for ($shortcutIndex = 0; $shortcutIndex -lt $managedShortcutPaths.Count; $shortcutIndex++) {
        $shortcutPath = $managedShortcutPaths[$shortcutIndex]
        $backupShortcut = Join-Path $rollbackRoot ("shortcut-$shortcutIndex.lnk")
        if (Test-Path -LiteralPath $backupShortcut -PathType Leaf) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $shortcutPath) -Force | Out-Null
            Copy-Item -LiteralPath $backupShortcut -Destination $shortcutPath -Force
        } elseif (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath -Force
        }
    }
    if (-not $startMenuFolderExisted -and (Test-Path -LiteralPath $startMenuFolder) -and
        (Get-ChildItem -LiteralPath $startMenuFolder -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $startMenuFolder -Force
    }
    Restore-RegistrySnapshot $uninstallRegistryPath $uninstallRegistrySnapshot
    New-Item -Path $runRegistryPath -Force | Out-Null
    if ([string]::IsNullOrWhiteSpace($runValueSnapshot)) {
        Remove-ItemProperty -LiteralPath $runRegistryPath -Name 'CodexTotalManager' -ErrorAction SilentlyContinue
    } else {
        New-ItemProperty -Path $runRegistryPath -Name 'CodexTotalManager' `
            -Value $runValueSnapshot -PropertyType String -Force | Out-Null
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    if ($destinationCreated -and (Test-Path -LiteralPath $destination)) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    if (Test-Path -LiteralPath $rollbackRoot) { Remove-Item -LiteralPath $rollbackRoot -Recurse -Force }
    if (-not $releaseRootExisted -and (Test-Path -LiteralPath $releaseRoot) -and
        (Get-ChildItem -LiteralPath $releaseRoot -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $releaseRoot -Force
    }
    if (-not $runtimeRootExisted -and (Test-Path -LiteralPath $runtimeRoot) -and
        (Get-ChildItem -LiteralPath $runtimeRoot -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $runtimeRoot -Force
    }
    if (-not $installRootExisted -and (Test-Path -LiteralPath $installFull) -and
        (Get-ChildItem -LiteralPath $installFull -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $installFull -Force
    }
    throw $failure
}
