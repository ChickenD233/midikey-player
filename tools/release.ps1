#Requires -Version 5.1
<#
.SYNOPSIS
    发一个新版本：版本号自动加一，写更新日志，构建、自检、提交、打 tag、上传。

.DESCRIPTION
    规则（用户 2026-09-16 定）：每次发 release 一律发新版本，版本号自动加一。
    已经发布的包不动、不覆盖、不重传。tag 已存在就直接停手。

    默认流程：
      1. 从 MidiKeyPlayer.csproj 读当前版本号，按 -Bump 加一（默认补丁号 +1）。
      2. 在 docs\更新日志.txt 最上面插入新版本一节。
         给了 -Notes 就用那份文本；不给就用上个 tag 以来的提交标题当草稿。
      3. 改 csproj 的 <Version> 与 更新说明.txt 第一行。
      4. 跑 build-win.sh 出包。zip 里是 exe 与 更新日志.txt 两项。
      5. 跑内置自检，退出码必须是 0。
      6. 跑主窗与键位窗的界面快照，确认能出图。
      7. 提交、推 main、打 tag、建 Release、上传 zip。

.PARAMETER Notes
    新版本日志一节的正文。可以是一段话，也可以是多行（每行前自己写「- 」）。

.PARAMETER Bump
    Patch（默认）/ Minor / Major。Patch 就是补丁号 +1。

.PARAMETER DryRun
    只打印会做什么，外加新版本日志一节的全文。不改文件、不构建、不推送。

.PARAMETER SkipPush
    构建与验证照做，但不提交、不推送、不发 Release。改过的文件留在工作区，供人工检查。

.PARAMETER Mandatory
    把这一版标成强制更新（Release 说明里写 [强制更新]）。旧版用户检查到它会先更新才能继续用。

.PARAMETER TrimParity
    额外构建一份不裁剪的 exe，把两张界面快照逐字节比对。
    动过裁剪设置或升级依赖之后跑一次。

.EXAMPLE
    pwsh -File tools\release.ps1 -DryRun

.EXAMPLE
    pwsh -File tools\release.ps1 -Notes "（这一段写这一版改了什么）"

.EXAMPLE
    pwsh -File tools\release.ps1            # 用提交标题当日志草稿，直接发
#>
[CmdletBinding()]
param(
    [string]$Notes = '',
    [ValidateSet('Patch', 'Minor', 'Major')][string]$Bump = 'Patch',
    [switch]$DryRun,
    [switch]$SkipPush,
    [switch]$TrimParity,
    # 强制更新：在 Release 说明里写上 [强制更新] 标记。装着旧版的用户检查更新时会被要求先更新
    # （跳过选项作废）。老版本里没有这段逻辑，所以标记只对 v1.0.30 起、带强制更新代码的版本有效。
    [switch]$Mandatory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Repo       = 'ChickenD233/midikey-player'
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Proj       = Join-Path $RepoRoot 'MidiKeyPlayer\MidiKeyPlayer.csproj'
$ChangeLog  = Join-Path $RepoRoot 'MidiKeyPlayer\docs\更新日志.txt'
$Manual     = Join-Path $RepoRoot 'MidiKeyPlayer\docs\更新说明.txt'
$BuildSh    = Join-Path $RepoRoot 'MidiKeyPlayer\build-win.sh'
$SelfTest   = Join-Path $PSScriptRoot 'run-selftest.ps1'
$SampleMidi = Join-Path $RepoRoot '示例MIDI\示例3-铃儿响叮当-三音轨.mid'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)

function Write-Step([string]$text) { Write-Host ">> $text" }

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    # git 把进度写到 stderr（push 与 fetch 都写）。在 $ErrorActionPreference='Stop' 下，
    # 用 2>&1 收这些行会被 PowerShell 当成错误直接抛出，所以这里临时放宽，只看退出码。
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $RepoRoot @GitArgs 2>&1
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $saved }
    if ($code -ne 0) { throw "git $($GitArgs -join ' ') 失败（退出码 $code）：`n$($out -join "`n")" }
    return $out
}

function Get-CurrentVersion {
    $xml = [System.IO.File]::ReadAllText($Proj, [System.Text.Encoding]::UTF8)
    $m = [regex]::Match($xml, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "从 $Proj 读不到 <Version>。" }
    return $m.Groups[1].Value.Trim()
}

function Get-NextVersion([string]$current, [string]$bump) {
    $parts = $current.Split('.')
    if ($parts.Count -lt 2) { throw "版本号格式不对：$current" }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = 0
    if ($parts.Count -ge 3) { $patch = [int]$parts[2] }
    switch ($bump) {
        'Major' { $major++; $minor = 0; $patch = 0 }
        'Minor' { $minor++; $patch = 0 }
        default { $patch++ }
    }
    return "$major.$minor.$patch"
}

function Get-LastTag {
    $tags = @(& git -C $RepoRoot tag --list 'v*' --sort=-v:refname)
    if ($tags.Count -eq 0) { return '' }
    return $tags[0].Trim()
}

function Get-CommitDraft([string]$lastTag) {
    $range = 'HEAD'
    if ($lastTag -ne '') { $range = "$lastTag..HEAD" }
    $subjects = @(& git -C $RepoRoot log $range --pretty=format:%s)
    $bullets = New-Object System.Collections.Generic.List[string]
    foreach ($s in $subjects) {
        $line = $s.Trim()
        if ($line.Length -gt 0) { [void]$bullets.Add("- $line") }
    }
    return $bullets
}

function New-SectionText([string]$version, [string]$date, [string[]]$lines) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('======================================================================')
    [void]$sb.AppendLine("v$version    $date")
    [void]$sb.AppendLine('======================================================================')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【本次改动】')
    foreach ($line in $lines) { [void]$sb.AppendLine($line) }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('【链接】')
    [void]$sb.AppendLine('https://github.com/ChickenD233/midikey-player')
    [void]$sb.AppendLine()
    return $sb.ToString()
}

function Add-ChangeLogSection([string]$version, [string]$section) {
    $raw = [System.IO.File]::ReadAllText($ChangeLog, [System.Text.Encoding]::UTF8)
    if ($raw -match "(?m)^v$([regex]::Escape($version))\s") {
        throw "更新日志里已经有 v$version 这一节，停手。"
    }
    $pattern = "(?m)^=+\r?\n(v\d+\.\d+\.\d+[^\r\n]*)\r?\n=+\r?\n"
    $m = [regex]::Match($raw, $pattern)
    if (-not $m.Success) { throw "更新日志里找不到任何版本小节（形如 v1.0.1）。" }
    $head = $raw.Substring(0, $m.Index)
    $tail = $raw.Substring($m.Index)
    [System.IO.File]::WriteAllText($ChangeLog, $head + $section + $tail, $Utf8NoBom)
}

function Set-ProjectVersion([string]$version) {
    $xml = [System.IO.File]::ReadAllText($Proj, [System.Text.Encoding]::UTF8)
    $xml = [regex]::Replace($xml, '<Version>[^<]+</Version>', "<Version>$version</Version>")
    [System.IO.File]::WriteAllText($Proj, $xml, $Utf8NoBom)

    $txt = [System.IO.File]::ReadAllText($Manual, [System.Text.Encoding]::UTF8)
    $re = New-Object System.Text.RegularExpressions.Regex('^MIDI 按键播放器 v[\d.]+')
    if (-not $re.IsMatch($txt)) { throw "更新说明.txt 第一行不是「MIDI 按键播放器 vX.Y.Z」。" }
    $txt = $re.Replace($txt, "MIDI 按键播放器 v$version", 1)
    [System.IO.File]::WriteAllText($Manual, $txt, $Utf8NoBom)
}

function Add-DotnetToPath {
    $candidates = @(
        (Join-Path $RepoRoot '.tools\dotnet'),
        (Join-Path (Split-Path -Parent $RepoRoot) '.tools\dotnet'),
        'C:\Program Files\dotnet'
    )
    foreach ($dir in $candidates) {
        $exe = Join-Path $dir 'dotnet.exe'
        if (Test-Path -LiteralPath $exe) {
            $env:PATH = "$dir;$env:PATH"
            return $exe
        }
    }
    $onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -ne $onPath) { return $onPath.Source }
    throw '找不到 dotnet。装 .NET 8 SDK，或把它放到 .tools\dotnet 下。'
}

function Invoke-Build {
    $bash = Get-Command bash.exe -ErrorAction SilentlyContinue
    if ($null -eq $bash) { throw '找不到 bash.exe（需要 Git for Windows）。' }
    [void](Add-DotnetToPath)
    Push-Location (Join-Path $RepoRoot 'MidiKeyPlayer')
    try {
        & $bash.Source 'build-win.sh'
        if ($LASTEXITCODE -ne 0) { throw "build-win.sh 退出码 $LASTEXITCODE。" }
    }
    finally { Pop-Location }
}

function Get-ReleaseExe { Join-Path $RepoRoot 'MidiKeyPlayer\release\win-x64\MidiKeyPlayer.exe' }
function Get-ReleaseZip([string]$version) { Join-Path $RepoRoot "MidiKeyPlayer\release\MidiKeyPlayer-win-x64-$version.zip" }

function Invoke-SelfTest([string]$exe) {
    & $SelfTest -ExePath ([System.IO.Path]::GetFullPath($exe)) -TimeoutSeconds 180
    if ($LASTEXITCODE -ne 0) { throw "内置自检没有通过，退出码 $LASTEXITCODE。" }
}

function Invoke-Snapshot([string]$exe, [string]$tag) {
    $main = Join-Path $env:TEMP "midikey-release-$tag-main.png"
    $keymap = Join-Path $env:TEMP "midikey-release-$tag-keymap.png"
    Remove-Item $main, $keymap -ErrorAction SilentlyContinue

    $env:MIDIKEY_UI_SNAPSHOT = $main
    $env:MIDIKEY_UI_SNAPSHOT_MIX = 'all'
    if (Test-Path -LiteralPath $SampleMidi) { $env:MIDIKEY_UI_SNAPSHOT_MIDI = $SampleMidi }
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT_KEYMAP -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $exe -PassThru
    [void]$p.WaitForExit(180000)
    if ($p.ExitCode -ne 0) { throw "主窗快照退出码 $($p.ExitCode)。" }

    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT -ErrorAction SilentlyContinue
    $env:MIDIKEY_UI_SNAPSHOT_KEYMAP = $keymap
    $q = Start-Process -FilePath $exe -PassThru
    [void]$q.WaitForExit(180000)
    if ($q.ExitCode -ne 0) { throw "键位窗快照退出码 $($q.ExitCode)。" }
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT_KEYMAP -ErrorAction SilentlyContinue

    # 高级设置窗口：探针自己会走一遍「开 → 关 → 再开」，顺带验证内容归属来回搬是干净的
    $advanced = Join-Path $env:TEMP "midikey-release-$tag-advanced.png"
    Remove-Item $advanced -ErrorAction SilentlyContinue
    $env:MIDIKEY_UI_SNAPSHOT_ADVANCED = $advanced
    $r = Start-Process -FilePath $exe -PassThru
    [void]$r.WaitForExit(180000)
    if ($r.ExitCode -ne 0) { throw "高级设置窗口快照退出码 $($r.ExitCode)。" }
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT_ADVANCED -ErrorAction SilentlyContinue

    # 赞助页：设置窗口的第三页（爱发电置顶 + B 站 / GitHub）
    $sponsor = Join-Path $env:TEMP "midikey-release-$tag-sponsor.png"
    Remove-Item $sponsor -ErrorAction SilentlyContinue
    $env:MIDIKEY_UI_SNAPSHOT_SPONSOR = $sponsor
    $sp = Start-Process -FilePath $exe -PassThru
    [void]$sp.WaitForExit(180000)
    if ($sp.ExitCode -ne 0) { throw "赞助页快照退出码 $($sp.ExitCode)。" }
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT_SPONSOR -ErrorAction SilentlyContinue

    # 深色皮肤：拍图前用设置里那个下拉框切到「深色（黑）」，验证不重启也能换色。
    # 探针只切不落盘，不会改掉跑脚本这台机器的皮肤档位。
    $dark = Join-Path $env:TEMP "midikey-release-$tag-dark.png"
    Remove-Item $dark -ErrorAction SilentlyContinue
    $env:MIDIKEY_UI_SNAPSHOT = $dark
    $env:MIDIKEY_UI_SNAPSHOT_MIX = 'all'
    $env:MIDIKEY_UI_SNAPSHOT_THEME = '2'
    $s = Start-Process -FilePath $exe -PassThru
    [void]$s.WaitForExit(180000)
    if ($s.ExitCode -ne 0) { throw "深色皮肤快照退出码 $($s.ExitCode)。" }
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT, Env:\MIDIKEY_UI_SNAPSHOT_THEME -ErrorAction SilentlyContinue

    foreach ($f in @($main, $keymap, $advanced, $sponsor, $dark)) {
        if (-not (Test-Path -LiteralPath $f)) { throw "快照没出图：$f" }
        if ((Get-Item -LiteralPath $f).Length -lt 10000) { throw "快照太小，可能是空白：$f" }
    }
    Write-Host ("   主窗 " + (Get-Item $main).Length + " 字节；键位窗 " + (Get-Item $keymap).Length + " 字节；高级窗 " + (Get-Item $advanced).Length + " 字节；赞助页 " + (Get-Item $sponsor).Length + " 字节；深色主窗 " + (Get-Item $dark).Length + " 字节")
    return @{ Main = $main; Keymap = $keymap; Advanced = $advanced; Sponsor = $sponsor; Dark = $dark }
}

# 文件夹曲目卡换歌回归：连续点三首，每步都要换过去，列表行数不能塌。
# 用户报过的「选了一首之后别的点不动」就是这条链路坏了，所以每次发版都跑。
function Invoke-FolderProbe([string]$exe) {
    $folder = Join-Path $RepoRoot '示例MIDI'
    if (-not (Test-Path -LiteralPath $folder)) { throw "缺少回归素材目录：$folder" }
    $report = Join-Path $env:TEMP 'midikey-release-folder-probe.txt'
    Remove-Item $report -ErrorAction SilentlyContinue

    $env:MIDIKEY_UI_SNAPSHOT_FOLDER = $folder
    $env:MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT = $report
    $p = Start-Process -FilePath $exe -PassThru
    [void]$p.WaitForExit(180000)
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT_FOLDER, Env:\MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $report) {
        foreach ($line in (Get-Content -LiteralPath $report -Encoding UTF8)) { Write-Host "   $line" }
    }
    if ($p.ExitCode -ne 0) {
        throw "文件夹换歌回归失败，退出码 $($p.ExitCode)。报告：$report"
    }
}

function Get-GitHubToken {
    # 这台机器上管道喂不进 git credential fill：PowerShell 的 `|` 与 .NET 的 RedirectStandardInput
    # 都试过，git 一律报「refusing to work with credential missing protocol field」，
    # 只有文件重定向可行。所以走临时文件，读完立刻删；令牌只在 %TEMP% 停留一瞬间。
    $req = Join-Path $env:TEMP 'midikey-cred-req.txt'
    $res = Join-Path $env:TEMP 'midikey-cred-out.txt'
    [System.IO.File]::WriteAllText($req, "protocol=https`nhost=github.com`n", (New-Object System.Text.ASCIIEncoding))
    Remove-Item $res -ErrorAction SilentlyContinue

    & cmd.exe /c "git credential fill < `"$req`" > `"$res`"" | Out-Null

    $token = ''
    if (Test-Path -LiteralPath $res) {
        foreach ($line in (Get-Content -LiteralPath $res)) {
            if ($line -like 'password=*') { $token = $line.Substring(9).Trim() }
        }
    }
    Remove-Item $req, $res -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($token)) { throw '取不到 GitHub 令牌（git credential fill 没返回 password）。' }
    return $token
}

function New-ReleaseBody([string]$version, [string]$section, [bool]$mandatory = $false) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# MidiKeyPlayer v$version")
    [void]$sb.AppendLine()
    if ($mandatory) { [void]$sb.AppendLine('[强制更新] 这一版必须更新：低于 v' + $version + ' 的程序会先更新才能继续用。'); [void]$sb.AppendLine() }
    [void]$sb.AppendLine($section.TrimEnd())
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 下载与运行')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("1. 下载下面的 ``MidiKeyPlayer-win-x64-$version.zip``。")
    [void]$sb.AppendLine('2. 解压。里面有 `MidiKeyPlayer.exe` 与 `更新日志.txt`。')
    [void]$sb.AppendLine('3. 双击 `MidiKeyPlayer.exe`。')
    [void]$sb.AppendLine('4. 弹出 SmartScreen「未知发布者」时，选「更多信息」，再选「仍要运行」。')
    [void]$sb.AppendLine('5. 杀毒软件可能误报「模拟按键」。请把它加入白名单。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('系统要求：Windows 10 / 11（64 位）。单文件 exe，不需要安装 .NET。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 注意')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('1. 目标窗口用窗口化或无边框窗口化。全屏独占收不到模拟按键。')
    [void]$sb.AppendLine('2. 播放前点一下目标窗口，让它在前台。')
    [void]$sb.AppendLine('3. 输入法切到英文。')
    [void]$sb.AppendLine('4. 目标程序以管理员运行时，本程序也要提权（自检卡的「以管理员重启」一键搞定）；普通程序不需要提权。')
    [void]$sb.AppendLine('5. 鼠标或键盘失控时，狂按 F6。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 免责声明')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('虚拟输入可能违反第三方软件的使用规则，有账号封禁风险。请只在练习、测试或单机场景使用。后果由使用者承担。')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 免费声明与反馈')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('本程序完全免费、开源，没有收费版本，作者也从没卖过它。')
    [void]$sb.AppendLine('**如果你是「购买」的此软件，立刻退款，你被骗了。**')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('- 作者 B 站：https://space.bilibili.com/28440883')
    [void]$sb.AppendLine('- 反馈 QQ 群：1042477909')
    [void]$sb.AppendLine('- 源码与全部版本：https://github.com/ChickenD233/midikey-player')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('每个版本改了什么，见发布包里的 `更新日志.txt`，或仓库的 `MidiKeyPlayer/docs/更新日志.txt`。')
    return $sb.ToString()
}

function Publish-Release([string]$version, [string]$zip, [string]$body) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $token = Get-GitHubToken
    $headers = @{
        Authorization = "Bearer $token"
        Accept        = 'application/vnd.github+json'
        'User-Agent'  = 'midikey-release'
    }

    $payload = @{
        tag_name         = "v$version"
        target_commitish = 'main'
        name             = "v$version"
        body             = $body
        draft            = $false
        prerelease       = $false
    } | ConvertTo-Json -Depth 4

    Write-Step "建 Release v$version ..."
    $rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" `
        -Headers $headers -ContentType 'application/json; charset=utf-8' `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))

    Write-Step "上传 $(Split-Path -Leaf $zip) ..."
    $asset = "MidiKeyPlayer-win-x64-$version.zip"
    $bytes = [System.IO.File]::ReadAllBytes($zip)
    $upload = "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=$asset"
    $up = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType 'application/zip' -Body $bytes
    Write-Host "   已上传：$($up.name) $($up.size) 字节"

    # 上传后立刻校验：远端资产的 sha256 必须与本地 zip 相同。
    # 先用上传响应里的 digest；没有就回读资产列表；再没有才下载回来算。
    $localHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()
    $remoteHash = ''
    if ($null -ne $up.digest -and $up.digest -match '^sha256:(.+)$') { $remoteHash = $Matches[1].ToLower() }
    if ([string]::IsNullOrWhiteSpace($remoteHash)) {
        $assets = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$Repo/releases/$($rel.id)/assets" -Headers $headers
        foreach ($a in $assets) {
            if ($a.name -eq $asset -and $null -ne $a.digest -and $a.digest -match '^sha256:(.+)$') {
                $remoteHash = $Matches[1].ToLower()
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($remoteHash)) {
        Write-Host '   远端没给 sha256，下载回来自己算 ...'
        $probe = Join-Path $env:TEMP "midikey-verify-$version.zip"
        $lastError = ''
        for ($i = 1; $i -le 5; $i++) {
            try {
                Invoke-WebRequest -Uri $up.browser_download_url -OutFile $probe -UseBasicParsing
                $remoteHash = (Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash.ToLower()
                $lastError = ''
                break
            }
            catch { $lastError = $_.Exception.Message; Start-Sleep -Seconds 3 }
        }
        if (-not [string]::IsNullOrWhiteSpace($lastError)) { throw "下载校验失败：$lastError" }
        Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
    }
    if ($remoteHash -ne $localHash) { throw "上传的包与本地不一致。本地 $localHash，远端 $remoteHash。" }
    Write-Host "   校验通过：sha256 $localHash"
    return $rel.html_url
}

# ---------------------------------------------------------------- 主流程

Push-Location $RepoRoot
try {
    Write-Step '前置检查'
    $dirty = @(& git -C $RepoRoot status --porcelain)
    if ($dirty.Count -gt 0) { throw "工作区不干净，先提交或还原：`n$($dirty -join "`n")" }
    $branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { throw "当前分支是 $branch，发版要在 main 上。" }
    [void](Invoke-Git fetch origin --quiet)

    $current = Get-CurrentVersion
    $next = Get-NextVersion $current $Bump
    $tag = "v$next"
    $existingTags = @(& git -C $RepoRoot tag --list $tag)
    if ($existingTags.Count -gt 0) { throw "$tag 已存在。规则是每次都发新版本，不改已发布的包。" }
    $remoteTag = @(& git -C $RepoRoot ls-remote --tags origin "refs/tags/$tag")
    if ($remoteTag.Count -gt 0) { throw "远端已有 $tag。" }

    Write-Host "   当前版本 $current → 新版本 $next"

    $lines = @()
    if (-not [string]::IsNullOrWhiteSpace($Notes)) {
        $lines = @($Notes -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    }
    else {
        $lastTag = Get-LastTag
        Write-Host "   日志草稿来自 $lastTag..HEAD 的提交标题"
        $lines = @(Get-CommitDraft $lastTag)
        if ($lines.Count -eq 0) { throw "上个 tag 以来没有提交，也没有给 -Notes，日志会是空的。停手。" }
    }

    $date = Get-Date -Format 'yyyy-MM-dd'
    $section = New-SectionText $next $date $lines

    if ($DryRun) {
        Write-Host ''
        Write-Host '---- 新版本日志一节（DryRun，没有写文件）----'
        Write-Host $section
        Write-Host '--------------------------------------------'
        Write-Host "会做：插入日志 → 改 csproj 与 更新说明 → 构建 → 自检 → 快照 → 提交 → tag $tag → 发 Release"
        return
    }

    Write-Step "写更新日志：新增 v$next 一节"
    Add-ChangeLogSection $next $section
    Write-Step '改版本号：csproj 与 更新说明.txt'
    Set-ProjectVersion $next

    Write-Step '构建发布包'
    Invoke-Build
    $exe = Get-ReleaseExe
    $zip = Get-ReleaseZip $next
    if (-not (Test-Path -LiteralPath $exe)) { throw "没有产出 $exe。" }
    if (-not (Test-Path -LiteralPath $zip)) { throw "没有产出 $zip。" }
    Write-Host "   exe $((Get-Item $exe).Length) 字节；zip $((Get-Item $zip).Length) 字节"

    Write-Step '内置自检'
    Invoke-SelfTest $exe

    Write-Step '界面快照'
    $shots = Invoke-Snapshot $exe 'trim'

    Write-Step '文件夹曲目卡换歌回归'
    Invoke-FolderProbe $exe

    if ($TrimParity) {
        Write-Step '裁剪比对：另建一份不裁剪的 exe，逐字节比快照'
        $plainOut = Join-Path $env:TEMP "midikey-plain-$next"
        if (Test-Path -LiteralPath $plainOut) { Remove-Item -Recurse -Force $plainOut }
        $dotnet = Add-DotnetToPath
        & $dotnet publish $Proj -c Release -o $plainOut -p:PublishTrimmed=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "不裁剪构建失败，退出码 $LASTEXITCODE。" }
        $plainShots = Invoke-Snapshot (Join-Path $plainOut 'MidiKeyPlayer.exe') 'plain'
        foreach ($k in @('Main', 'Keymap', 'Advanced', 'Dark')) {
            $a = (Get-FileHash $shots[$k] -Algorithm SHA256).Hash
            $b = (Get-FileHash $plainShots[$k] -Algorithm SHA256).Hash
            if ($a -ne $b) { throw "裁剪版与不裁剪版的 $k 快照不一致：$a / $b" }
        }
        Write-Host '   两张快照逐字节相同'
        Remove-Item -Recurse -Force $plainOut -ErrorAction SilentlyContinue
    }

    if ($SkipPush) {
        Write-Host ''
        Write-Host "已构建并验证 v$next。按 -SkipPush 的要求，没有提交、没有推送。"
        Write-Host "改动留在工作区：$ChangeLog、$Proj、$Manual。"
        return
    }

    Write-Step "提交并推送"
    [void](Invoke-Git add -A)
    $commitMsg = "发布 v$next`n`n" + ($lines -join "`n")
    $msgFile = Join-Path $env:TEMP "midikey-commit-$next.txt"
    # 不带 BOM：git 默认按 UTF-8 读提交信息。带 BOM 会在标题最前面留一个零宽字符。
    [System.IO.File]::WriteAllText($msgFile, $commitMsg, $Utf8NoBom)
    [void](Invoke-Git commit -F $msgFile)
    [void](Invoke-Git push origin main)
    [void](Invoke-Git tag -a $tag -m "MidiKeyPlayer v$next")
    [void](Invoke-Git push origin $tag)

    Write-Step '发 Release'
    $body = New-ReleaseBody $next $section $Mandatory.IsPresent
    try {
        $url = Publish-Release $next $zip $body
    }
    catch {
        # 到这里提交与 tag 已经推上去了。发 Release 失败只差最后一步，不用重跑整条流程：
        # 修好原因后手工建 Release 并传 zip，或删掉 tag 重跑。
        Write-Host ''
        Write-Host "发 Release 失败：$($_.Exception.Message)"
        Write-Host "代码已经推到 main，tag $tag 也已经推上去。"
        Write-Host "修好之后手工建 Release 并上传：$zip"
        Write-Host "或者删掉 tag（git push origin :refs/tags/$tag）后重跑本脚本。"
        throw
    }
    Write-Host ''
    Write-Host "完成：$url"
}
finally {
    Pop-Location
    Remove-Item Env:\MIDIKEY_UI_SNAPSHOT, Env:\MIDIKEY_UI_SNAPSHOT_KEYMAP, Env:\MIDIKEY_UI_SNAPSHOT_MIX, `
        Env:\MIDIKEY_UI_SNAPSHOT_MIDI, Env:\MIDIKEY_UI_SNAPSHOT_REPORT, Env:\MIDIKEY_UI_SNAPSHOT_THEME, `
        Env:\MIDIKEY_UI_SNAPSHOT_ADVANCED, Env:\MIDIKEY_UI_SNAPSHOT_SPONSOR, `
        Env:\MIDIKEY_UI_SNAPSHOT_FOLDER, Env:\MIDIKEY_UI_SNAPSHOT_FOLDER_REPORT -ErrorAction SilentlyContinue
}
