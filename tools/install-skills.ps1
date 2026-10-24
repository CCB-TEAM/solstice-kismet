# 把仓库里的 skills/* 同步到 <projectRoot>/.dsh/skills/
#
# DSH 的技能发现规则里有一条：<projectRoot>/.dsh/skills/<name>/SKILL.md。
# 仓库里保留一份可版本管理的源，这个脚本负责安装到发现路径。
#
# 用法：
#   .\tools\install-skills.ps1                       # 装到 D:\files\projects\.dsh\skills
#   .\tools\install-skills.ps1 -ProjectRoot D:\x     # 装到别的项目根

param(
    [string]$ProjectRoot = 'D:\files\projects'
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot '..\skills'
if (-not (Test-Path $source)) {
    throw "找不到技能源目录: $source"
}

$target = Join-Path $ProjectRoot '.dsh\skills'
New-Item -ItemType Directory -Force $target | Out-Null

$count = 0
foreach ($skill in Get-ChildItem $source -Directory) {
    $dest = Join-Path $target $skill.Name
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $skill.FullName '*') $dest -Recurse -Force
    Write-Host "  安装 $($skill.Name) -> $dest"
    $count++
}

Write-Host "完成：$count 个技能已同步到 $target"
