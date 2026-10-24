# 蓝图字节码操作包装脚本（可移植）
#
# 用法：
#   .\blueprint.ps1 -Repo <solstice-kismet 仓库> -Usmap <xxx.usmap> -Action struct -Name Vector
#   .\blueprint.ps1 -Repo <repo> -Usmap <u> -Action list     -Asset <a.uasset>
#   .\blueprint.ps1 -Repo <repo> -Usmap <u> -Action decompile -Asset <a.uasset> -Name <函数名>
#   .\blueprint.ps1 -Repo <repo> -Usmap <u> -Action resolve  -Asset <a.uasset> -Name <函数> [-Class <类>] -Catalog <functions.json>
#   .\blueprint.ps1 -Repo <repo> -Usmap <u> -Action plan     -Asset <a.uasset> -Name <函数> -Catalog <functions.json>
#   .\blueprint.ps1 -Repo <repo> -Usmap <u> -Action insert   -AssetDir <目录> -Name <被调函数> -Catalog <functions.json>
#   .\blueprint.ps1 -Repo <repo> -Usmap <u> -Action coverage -AssetDir <目录>
#
# 退出码：0 = 成功；非 0 = 失败（可直接用于脚本判断）。

param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Usmap,
    [Parameter(Mandatory)]
    [ValidateSet('list', 'decompile', 'resolve', 'plan', 'insert', 'coverage', 'struct')]
    [string]$Action,

    [string]$Asset,
    [string]$AssetDir,
    [string]$Name,
    [string]$Class,
    [string]$Catalog,
    [string]$Configuration = 'Debug',
    [int]$Max = 0
)

$ErrorActionPreference = 'Stop'

$probe = Join-Path $Repo "tools\KismetProbe\bin\$Configuration\net10.0\KismetProbe.dll"
$coverage = Join-Path $Repo "tools\KismetCoverage\bin\$Configuration\net10.0\KismetCoverage.dll"

function Require([string]$value, [string]$label) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw "缺少参数 -$label" }
    return $value
}

switch ($Action) {
    'list' {
        & dotnet (Require $probe 'probe 未构建：先 dotnet build tools/KismetProbe') (Require $Asset 'Asset') $Usmap
        exit $LASTEXITCODE
    }

    'decompile' {
        & dotnet (Require $probe 'probe 未构建') (Require $Asset 'Asset') $Usmap (Require $Name 'Name')
        exit $LASTEXITCODE
    }

    'struct' {
        & dotnet (Require $coverage 'coverage 未构建：先 dotnet build tools/KismetCoverage') x $Usmap --struct (Require $Name 'Name')
        exit $LASTEXITCODE
    }

    'resolve' {
        $args = @((Require $coverage 'coverage'), (Require $Asset 'Asset'), $Usmap, '--imports', (Require $Name 'Name'))
        if ($Class)   { $args += @('--class', $Class) }
        if ($Catalog) { $args += @('--catalog', (Require $Catalog 'Catalog')) }
        & dotnet @args
        exit $LASTEXITCODE
    }

    'plan' {
        # --plan 需要真实资产（要解析 Import 表）
        & dotnet (Require $coverage 'coverage') (Require $Asset 'Asset') $Usmap --plan (Require $Name 'Name') --catalog (Require $Catalog 'Catalog')
        exit $LASTEXITCODE
    }

    'insert' {
        $args = @((Require $coverage 'coverage'), (Require $AssetDir 'AssetDir'), $Usmap,
                  '--insert', (Require $Name 'Name'), '--catalog', (Require $Catalog 'Catalog'))
        if ($Max -gt 0) { $args += @('--max', $Max) }
        & dotnet @args
        exit $LASTEXITCODE
    }

    'coverage' {
        & dotnet (Require $coverage 'coverage') (Require $AssetDir 'AssetDir') $Usmap
        exit $LASTEXITCODE
    }
}
