---
name: blueprint-read-modify
description: 'Use when reading or editing Unreal Engine blueprint bytecode (Kismet) with the Solstice.Kismet toolchain: list a function, decompile it to readable nodes, insert a function call, or rebake an ubergraph. Covers the library API and the bundled MCP server, the three inputs (asset, usmap, jmap), the byte-fidelity contract, and the traps that silently corrupt edits.'
---

# 读 / 改 UE 蓝图字节码（Kismet）

本技能基于 [Solstice.Kismet](https://github.com/CCB-TEAM/solstice-kismet)：把蓝图函数的
`KismetExpression[]` 反编译成可读节点、改完再编译回字节。

**核心契约**：反编译 → 编译 → 序列化必须与原字节**逐字节一致**。
任何"看着对"的改动都要用字节比对证明，而不是靠肉眼。

## 0. 准备三样输入

| 输入 | 是什么 | 从哪来 |
|---|---|---|
| **资产** | `.uasset` + 同名 `.uexp`（**缺一不可**） | FModel 导出目录、或 pak 解包结果 |
| **usmap** | UE 映射文件（结构体/类 schema） | FModel 的 `Mappings.usmap`、CUE4Parse 的 mappings dump |
| **jmap** | 函数签名表（`functions.json` + `objects.json`） | [solstice-jmap](https://github.com/CCB-TEAM/solstice-jmap) 从 usmap + 可执行文件生成 |

jmap **只在「新增调用」时需要**（函数归属、`FUNC_Static`、参数与返回类型）；
纯读 / 纯反编译不需要它。

下面用变量代表它们，按你的环境替换：

```powershell
$assets  = '<资产目录或单个 .uasset>'
$usmap   = '<xxx.usmap>'
$catalog = '<functions.json>'      # 可选
$repo    = '<solstice-kismet 仓库路径>'
```

> 找不到这些文件时先问用户，不要猜路径。`$assets` 通常长得像
> `.../Exports/<Game>/Content/Blueprints/...`。

## 1. 快速开始（用技能自带脚本）

技能目录下有 `scripts/blueprint.ps1`，把常用动作包成一条命令（不用记 bin 路径）：

```powershell
$bp = '<技能目录>/scripts/blueprint.ps1'

& $bp -Repo $repo -Usmap $usmap -Action struct    -Name Vector
& $bp -Repo $repo -Usmap $usmap -Action list      -Asset $asset
& $bp -Repo $repo -Usmap $usmap -Action decompile -Asset $asset -Name <函数名>
& $bp -Repo $repo -Usmap $usmap -Action resolve   -Asset $asset -Name <函数> [-Class <类>] -Catalog $catalog
& $bp -Repo $repo -Usmap $usmap -Action plan      -Asset $asset -Name <函数> -Catalog $catalog
& $bp -Repo $repo -Usmap $usmap -Action insert    -AssetDir <目录> -Name <被调函数> -Catalog $catalog
& $bp -Repo $repo -Usmap $usmap -Action coverage  -AssetDir <目录>
```

退出码非 0 即失败。工具没构建时先 `dotnet build tools/KismetProbe` 与 `dotnet build tools/KismetCoverage`。

## 2. 两种使用方式

### 方式 A：库 API

```xml
<PackageReference Include="Solstice.Kismet" Version="*" />
<!-- 或项目引用：<ProjectReference Include=".../src/Solstice.Kismet/Solstice.Kismet.csproj" /> -->
```

```csharp
using Solstice.Kismet;

var ws = KismetWorkspace.Open(
    File.ReadAllBytes(assetPath),                                    // .uasset
    File.ReadAllBytes(Path.ChangeExtension(assetPath, ".uexp")),
    File.ReadAllBytes(usmapPath),
    workDir);                                                        // 必须可写

ws.AttachCatalog(FunctionCatalog.Load(File.ReadAllText(catalogPath)));  // 可选
ws.AttachObjects(ObjectCatalog.Load(File.ReadAllText(objectsPath)));    // 可选
ws.AttachUsmap(UsmapCatalog.Load(usmapPath));                           // 可选
```

### 方式 B：MCP 服务端

```powershell
dotnet $repo/src/Solstice.Kismet.Mcp/bin/Release/net10.0/Solstice.Kismet.Mcp.dll
```

stdio + JSON-RPC 2.0。工具：`kismet_list_functions`、`kismet_decompile`、
`kismet_resolve_function`、`kismet_plan_call`、`kismet_insert_call`、
`kismet_bake_ubergraph`、`kismet_struct_members`、`kismet_save`。

MCP 客户端配置：

```json
{
  "mcpServers": {
    "solstice-kismet": {
      "command": "dotnet",
      "args": ["<repo>/src/Solstice.Kismet.Mcp/bin/Release/net10.0/Solstice.Kismet.Mcp.dll"]
    }
  }
}
```

## 3. 读：列函数 → 反编译 → 看关键事实

```powershell
# 列函数（名字 + 表达式条数）
dotnet $repo/tools/KismetProbe/bin/Debug/net10.0/KismetProbe.dll $asset $usmap

# 反编译单个函数：结构 / 基本块 / 节点树 / 回编译比对 / 结构对比
dotnet $repo/tools/KismetProbe/bin/Debug/net10.0/KismetProbe.dll $asset $usmap "<函数名>"
```

节点树**一定是三种表示之一**：

| 表示 | 含义 |
|---|---|
| 树形（`If` / `Loop` / `Set` / `Call`…） | 正常结构化 |
| `UberGraph N 段` | `ExecuteUbergraph_*` 展开成「调度头 + 入口段 + 尾声」，段名是事件名 |
| `BlockGraph N 块` | 树还原不了（共享尾部 / 状态机），用显式 goto 表示 |

后两种**不是失败**——字节仍然一致，只是不呈树形。只有 `逐字节一致: False` 才是问题。

看变量指针与结构体成员（局部变量 `resolvedOwner` = 函数导出，成员变量 = 类导出）：

```powershell
dotnet $repo/tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll $asset $usmap --vars "<函数名>"
```

## 4. 改：五步，顺序不能乱

### ① 解析成引用（别只给名字）

```powershell
dotnet $repo/tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll $asset $usmap `
  --imports <函数名> [--class <类短名或全路径>] --catalog $catalog
```

只给名字在**同名函数跨类**时会报错并列出候选——这时必须用 `--class`。
库 API 对应 `ws.ResolveFunction(name, owner?)`，得到 `FunctionRef`（名字 + 归属 + 包索引）。

### ② 看引脚（哪些参数只是占位）

```powershell
dotnet $repo/tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll x $usmap `
  --plan <函数名> --catalog $catalog
```

`??` 标记的是**占位参数**（text / struct / array / map / set 无法忠实合成），插入前必须替换。

### ③ 插入调用

```powershell
# 末尾插一次调用，参数按签名自动生成；走完整链路并回读校验
dotnet $repo/tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll $assets $usmap `
  --insert <被调函数> --catalog $catalog [--max N]
```

输出 `资产 N，成功 N，失败 N`，**失败为 0 才算过**。

需要「结果存进变量」时用库 API：`ws.InsertCallWithResult(function, callee)`，
变量名按 UE 约定 `CallFunc_<函数>_<返回引脚名>`，重复从 `_1` 起。

### ④ 显式写回

```csharp
var saved = ws.Save(outDir);      // 返回 .uasset + .uexp 两半，缺一不可
```

MCP 下是 `kismet_save`。**不调用就不落盘**（刻意设计，避免 agent 悄悄写坏资产）。

### ⑤ 验证

1. 重开保存结果：`KismetWorkspace.Open(saved.UAsset, saved.UExp, usmap, dir)` 不抛异常
2. 反编译目标函数：新调用出现在节点里，参数个数与签名一致
3. 批量回归：`KismetCoverage <目录> <usmap>` 全绿

## 5. 硬规则（违反会静默出错）

1. **不猜调用形态**。由 jmap `flags`（`FUNC_Static`）+ 是否本资产导出决定；没有签名表就报错，不要 fallback。
2. **不按名字查 Import**。节点应带 `FunctionRef`（含包索引）；只有索引为 0 的手写节点才按名字解析，且必须唯一。
3. **`EX_Context.RValuePointer` 不能省**。新建 Context 调用用**空 `FFieldPath`**（0 段路径 + owner 0）；用 `Old` 形态会 NRE。
4. **`KismetPropertyPointer` 必须 `New`（`FFieldPath`）形态**，UE5.x 走 `ShouldSerializeNew` 分支。
5. **`EX_Context.Offset`** = 内层表达式的序列化长度（`GetSize` 现算），不是 vtable 偏移。
6. **跳转是字节偏移**，不是数组下标；递归切片必须传 `baseOffset`。
7. **`EX_Let` 与 `EX_LetBool` 不同形**：前者 = 指针 + 变量表达式 + 值表达式；后者 = 变量表达式 + 赋值表达式。
8. **工作目录必须可写**：`Open` 会把字节落到磁盘再按路径构造资产（UAssetAPI 的限制）。别用 `%TEMP%`（部分环境对子进程拒绝写入）。

## 6. 失败排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `没有 X 的签名` | catalog 未载入，或用了只含游戏侧的 jmap 查引擎函数 | 载入含引擎类的 `functions.json` |
| `X 在 Import 表里有 N 个归属` | 同名函数跨类 | 用 `--class` / `ResolveFunction(name, owner)` |
| `结构化还原结果与原始字节不符` | 结构还原失败 → 自动退扁平 | 正常退化，字节仍一致，不是 bug |
| `表达式 XxxConst 暂不支持` | 未覆盖的指令（嵌套位置会抛） | 补 `ToArg` / `ToExpr` 分支 |
| `字节不一致 @N 位于第 k 条` | 某个数值字段（偏移 / 指针）不对 | 看探针的 `✗ [路径.字段]` 定位 |
| `Cannot add an empty FString to the name map` | 用空串构造 `FName` | 用 `"None"` |
| `UnauthorizedAccessException` 指向 `Usmap.PathToStream` | UAssetAPI 用 `File.Open(ReadWrite)` 打开 usmap | 把 usmap 复制到可写位置，或放宽权限 |

## 7. 边界（别过度承诺）

- **字节可读回**已验证（数千函数逐字节一致）；**UE 运行时行为未验证**——改完的资产没在游戏里跑过
- `StructProperty` 的类型在 jmap 里 `class = null`：只知道"是结构体 + 字节大小"，不知道具体结构
- **蓝图资产定义的结构（`S_*`）不在 usmap** 里，成员查不到
- `EX_CallMath` 形态未推断：纯函数也可能被编成 `FinalFunction`
- 只在 UE 5.6 上实测；更老版本有 `EX_Let` 少一个指针、`KismetPropertyPointer` 用 `Old` 形态等差异

## 8. 最小可运行示例

```csharp
using Solstice.Kismet;

var ws = KismetWorkspace.Open(
    File.ReadAllBytes(asset), File.ReadAllBytes(Path.ChangeExtension(asset, ".uexp")),
    File.ReadAllBytes(usmap), workDir);

ws.AttachCatalog(FunctionCatalog.Load(File.ReadAllText(catalog)));

// 读
foreach (var node in ws.Decompile("SomeFunction"))
{
    Console.WriteLine(node.Title);
}

// 改
var callee = ws.ResolveFunction("PrintString")!;
ws.InsertCall("SomeFunction", callee);

// 写回
var saved = ws.Save(outDir);
```

## 参考

仓库 `docs/` 下：`adding-a-call.md`（新增调用的完整数据流向）、`bytecode.md`（指令表）、
`ubergraph.md`（ubergraph 展开与重烘）、`mcp.md`（MCP 工具表）。
