# 真实资产覆盖率
#
# 逐个函数跑 KismetProbe：退出码 0 = 反编译 → 回编译逐字节一致。
# 「覆盖率必须核对分母」——探针列函数时会打印总数，这里直接用它，
# 不要手工挑几个函数就宣布通过（曾经 11/11 实为 64/77）。
#
# 用法:
#   .\tools\coverage.ps1 -AssetDir <资产目录> -Usmap <usmap 文件> [-Assets BP_Chat,BPMC_xxx]
# 不带 -Assets 时跑交接文档里的 6 个样本；-Assets * 跑目录下全部资产。

param(
    [Parameter(Mandatory)][string]$AssetDir,
    [Parameter(Mandatory)][string]$Usmap,
    [string[]]$Assets = @('BP_Chat', 'BP_MatchData', 'BPC_TurnTimer', 'BP_LevelManager', 'BP_NotificationManager', 'BPC_ChallengeAndRematch'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$probe = Join-Path $PSScriptRoot "KismetProbe\bin\$Configuration\net10.0\KismetProbe.dll"
if (-not (Test-Path $probe)) {
    throw "找不到探针 $probe —— 先 dotnet build tools\KismetProbe\KismetProbe.csproj"
}

if ($Assets -contains '*') {
    $Assets = Get-ChildItem $AssetDir -Filter *.uasset | ForEach-Object { $_.BaseName }
}

$total = 0
$pass = 0
$failures = @()
$missing = @()

foreach ($a in $Assets) {
    $uasset = Join-Path $AssetDir "$a.uasset"
    if (-not (Test-Path $uasset)) { $missing += $a; continue }

    $listOut = & dotnet $probe $uasset $Usmap 2>&1 | Out-String
    # 函数名可能带空格（如 `Get Unread Messages In Battle`），必须整行捕获，
    # 否则这些函数会被静默跳过——分母少算比失败更危险。
    $names = [regex]::Matches($listOut, '(?m)^\s+\d+\s+条\s+(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value }
    $declared = [regex]::Match($listOut, '\[probe\] (\d+) 个带字节码的函数').Groups[1].Value
    if ($declared -and [int]$declared -ne $names.Count) {
        Write-Host "[$a] 警告：探针声明 $declared 个函数，正则解析出 $($names.Count) 个"
    }
    if (-not $names) { $missing += "$a(无函数)"; continue }

    foreach ($n in $names) {
        $total++
        $null = & dotnet $probe $uasset $Usmap $n 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) {
            $pass++
        }
        else {
            $failures += "$a :: $n"
            Write-Host "  FAIL $a :: $n"
        }
    }
    Write-Host "[$a] 完成，累计 $pass/$total"
}

Write-Host ''
Write-Host "覆盖率: $pass / $total"
if ($missing.Count -gt 0) {
    Write-Host '未找到:'
    $missing | ForEach-Object { Write-Host "  $_" }
}
if ($failures.Count -gt 0) {
    Write-Host '失败列表:'
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}
exit 0
