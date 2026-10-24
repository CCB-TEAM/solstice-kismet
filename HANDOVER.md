# Solstice.Kismet 交接文档

> 写给接手的新对话/新开发者。读完这份文档应该能直接上手，不需要重读历史对话。

## 一、项目定位

从 UE5 蓝图资产的字节码做**双向**处理：

- **反编译**：`KismetExpression[]` → 编辑器节点模型（`KNode`）
- **编译**：节点模型 → `KismetExpression[]`
- **回写**：经 UAssetAPI 序列化回 `.uasset`/`.uexp`

核心指标是**往返逐字节一致**（decompile → compile → 序列化，与原始字节完全相同）。

## 二、当前状态

| 项 | 结果 |
|---|---|
| 自检 `tests/Solstice.Kismet.Verify` | **40/40** |
| 真实资产往返一致（`Blueprints/Logic` 全部 28 个资产） | **1305 / 1305 (100%)** |
| 结构还原退化（6 资产口径 131 函数） | **7 个函数**（原 47 个） |
| CI | 绿 |

> **分母修正**：文档曾记 118/119，那是**错的分母**。探针列函数时用了 `.Take(30)`，
> `BPC_ChallengeAndRematch` 实际 48 个函数被截到 30 个。去掉截断后 6 资产是 131 个函数，
> 扩到 `Logic` 全部 28 个资产是 **1305** 个函数。这正是第十节第 4 条的复发。

测试样本：`D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic` 下**全部**资产
（其中 4 个没有带字节码的函数：`BP_HandCardRichTextCache`、`E_PreGameStates`、
`S_KnockoutStatus`、`S_SalvagedCardInfo`）。

已修复的两批问题：

1. **字节保真**：`BP_LevelManager.ExecuteUbergraph_BP_LevelManager` 的 `Context 37 vs 36`——
   `EX_Context.ObjectExpression` 里包着 `EX_InterfaceContext`，反编译把包装层脱掉了。
   新增 `InterfaceContextArg` 原样保留（见第四节）。
2. **可读性**：结构还原退化从 47 个函数降到 7 个，见第六节。
   顺带补齐了 8 类指令（`MetaCast` / `ClassContext` / `DefaultVariable` / `ArrayGetByRef` /
   `UnicodeStringConst` / `InstanceDelegate` / `TransformConst` / `MapConst`），
   这才让 28 资产全量跑通。

## 三、目录结构

```
src/Solstice.Kismet/
  KismetModel.cs       节点/KArg 模型、CallShape、AssignKind、PinType
  KismetCompiler.cs    节点 → KismetExpression
  KismetDecompiler.cs  KismetExpression → 节点（含 if/loop 结构还原）
  KismetExprBuilder.cs 简单表达式构造
  ExpressionWalker.cs  变量索引（名字 → KismetPropertyPointer）
  ImportIndex.cs       Import 表 → 函数索引
  CompileContext.cs
  FunctionCatalog.cs   函数签名表（jmap functions.json）
  KismetWorkspace.cs   对外门面：加载资产、选函数、编译、回写
tools/KismetProbe/     命令行探针（结构 / 反编译 / 回编译 / 结构对比 / 基本块）
tools/coverage.ps1     真实资产覆盖率（逐函数跑探针，分母取自探针总数）
tests/Solstice.Kismet.Verify/  40 项自检
docs/bytecode.md       每个字节码指令的 token / 操作数 / 作用 / 出现场景 / 本库支持度
docs/ubergraph.md      ubergraph 的形态实测、展开规则、以及重新烘焙的 Flag 契约
```

## 四、最关键的设计原则

### 1. 「保留」而不是「推断」

凡是反编译时能看到的信息，**一律原样保留**，不要回编译时重新推导。

已保留的字段：

| 字段 | 所在节点 | 为什么 |
|---|---|---|
| `StackNode` | `CallNode`/`LocalFinalCallNode`/`MathCallNode`/`CallArg` | `EX_LocalFinalFunction` 的 StackNode 可能指向 **Export**（正数），按名字查 Import 索引查不到 |
| `PointerArg` | 各种赋值目标 / 参数 | `FFieldPath` 多段路径压平成名字会丢信息 |
| `RValuePointer` | `ContextCallNode`/`CallArg` | UE 里允许为 `nullptr`，重建容易 NRE |
| `CallShape` | 调用节点 | UE 的判定是 6 个条件的合取，其中 `GetCallerTargetsUnsupportedByCallee` 在 jmap 里**不存在**，无法复现 |
| `RawArg` / `RawNode` | 任意 | 结构复杂的指令（如 `EX_SwitchValue` 含相对偏移）原样发回 |
| `InterfaceContextArg` | `EX_Context.ObjectExpression` | `EX_InterfaceContext` 是**一层包装**，脱掉后回编译每条少 1 字节（曾致 37 vs 36） |

### 2. 「静默降级」是最大的敌人

这个项目里每一次覆盖率跃升，都源于把一处静默兜底改成显式失败：

| 兜底 | 改成 | 换来的进展 |
|---|---|---|
| `EmitArg` default → 空 | 抛异常 | 发现 `EX_LocalOutVariable` |
| `IfNode` 内联 switch `_` → `EX_Nothing` | 走 `ToExpr` | 条件不再丢失 |
| `ToExpr` default → `EX_Nothing` | 抛异常 | 发现 `EX_Self`/`ObjectConst`/`StringConst`… |
| `ToArg(...) ?? new IntArg(0)` | `RequireArg()` 抛异常 | **一次暴露全部 4 种缺失指令** |
| 结构还原不校验 | `DecompileVerified` 自校验 + 自动退化 | 保真性永不丢失 |

**结论：任何 `?? 默认值` / `_ => 空指令` 都是技术债。**

### 3. 字节一致性是硬要求，可读结构是附加价值

`DecompileVerified(bytecode, originalBytes, options, recompile)` 会：
1. 结构化反编译
2. 回编译
3. 与原始字节比对
4. **不符就退回扁平模式**（`options.Flatten = true`，纯指令、不还原 if/loop）

所以任何结构还原的改动，只要扁平模式仍是 1305/1305，就不会破坏保真性。

## 五、必须知道的 UAssetAPI 陷阱

### 1. 继承关系导致 case 顺序敏感

以下类型**都继承自 `EX_FinalFunction`**，`switch` 里必须排在它之前，否则永远匹配不到（编译器会报 `CS8120 该 case 不可访问`）：

```
EX_LocalFinalFunction
EX_CallMath
EX_CallMulticastDelegate
```

`EX_LetObj` / `EX_LetWeakObjPtr` / `EX_LetDelegate` / `EX_LetBool` 都继承自 `EX_LetBase`（`VariableExpression` + `AssignmentExpression`），与 `EX_Let`（`Value` + `Variable` + `Expression`）**不同形**。

### 2. `KismetPropertyPointer` 必须用 `New`（`FFieldPath`）形态

UE5.6 走 `ShouldSerializeNew` 分支。用 `new KismetPropertyPointer(new FPackageIndex(0))` 只填了 `Old`，`New` 为 null → **写回时 NRE**。

### 3. 跳转操作数是**字节偏移**，不是数组下标

`EX_Jump.CodeOffset` / `EX_JumpIfNot.CodeOffset` 是相对**整个函数**的绝对字节偏移。

递归反编译切片时**必须传 `baseOffset`**，否则 `BuildOffsets` 从 0 重算，目标全错。

## 六、反编译器结构还原的现状

`BuildBlock` 目前用「线性扫描 + 配对规则」：

| 情形 | 判据 | 结果 |
|---|---|---|
| 循环 | 体内有 `EX_Jump` 目标 ≤ start | `LoopNode` |
| if/else | 体内有 `EX_Jump` 目标 ≥ exit | `IfNode` + Else |
| 无 else 的 if | 没有配对 `EX_Jump` | `IfNode`，body 到 exit |
| 结构结束点 | `Jump` 目标 > `JumpIfNot` 落点时取 `Jump` 目标 | 避免整段代码丢失 |

**已知局限**：`K2Node_SwitchName` 编译出的「8 连 `JumpIfNot` + 集中 case 体」无法还原成 switch 结构。

`EX_SwitchValue` 目前走 `RawArg` 原样保留，**没有做结构化还原**。

> 每条指令的 token、操作数顺序、语义与出现场景，见 **[docs/bytecode.md](docs/bytecode.md)**。
> 改反编译器/编译器之前先查这张表，尤其第十节「最容易混的几组」。

### 结构还原的两条硬规则（都是踩出来的）

**规则一：跳转落点越出当前片段就放弃结构化。** 共享尾部 / 状态机的跳转目标会指向外层代码，
用嵌套 if 硬表达只会让补丁偏移错位。现在 `BuildBlock` 检测到 `exit >= expressions.Count`
就返回 null，把这一条 `JumpIfNot` 原样保留，外层继续照常还原。

**规则二：落点占位 `EX_Nothing` 按原始字节补，不能无条件补。** 反编译器把 `EX_Nothing` 全部跳过，
所以「原本有没有这个占位字节」必须记进 `IfNode.EndsWithNothing` / `LoopNode.EndsWithNothing`，
否则回编译要么多 1 字节、要么少 1 字节。同理，**无 else 的 if 绝对不能再补 `EX_Jump`**
（UE 的条件为假时直接落到 then 之后）——补了就是 5 个多余字节。

### 可读性现状（6 资产 131 函数）

| 指标 | 修前 | 现在 |
|---|---|---|
| 结构还原退化（整函数退回扁平） | 47 | **7** |
| Raw 节点总数 | 260 | 190 |

剩下 7 个都是 `ExecuteUbergraph_*` 这类状态机（`BPC_TurnTimer`、`BP_NotificationManager`、
`BP_Chat`、`BPC_ChallengeAndRematch`、`BP_LevelManager.TryToGoHome`、
`BPC_ChallengeAndRematch.ParseNotification`、`BPC_TurnTimer.setTimedOutAfterReconnect`）。
它们字节完全一致，只是嵌套结构还还原不出来——下一步该做的是按跳转目标建基本块图
（探针已有基本块分析代码可参考），而不是继续加节点类型。

## 七、工具用法

### 探针 `tools/KismetProbe`

```powershell
# 列出资产里所有带字节码的函数
dotnet tools/KismetProbe/bin/Debug/net10.0/KismetProbe.dll <asset.uasset> <usmap>

# 单个函数：打印结构 / 基本块 / 反编译节点 / 回编译比对 / 结构对比
dotnet tools/KismetProbe/bin/Debug/net10.0/KismetProbe.dll <asset.uasset> <usmap> "<函数名>"

# 环境变量
$env:KISMET_FLATTEN='1'   # 强制扁平模式（不还原 if/loop）
```

退出码：0 = 逐字节一致，1 = 不一致。

探针会把资产两半和 **usmap** 都复制到临时目录再打开——UAssetAPI 用
`File.Open(ReadWrite)` 打开文件，只读位置（只读目录、受沙箱限制的工作区外路径）
会抛 `UnauthorizedAccessException`，堆栈指向 `Usmap.PathToStream`，极易误判成权限问题。

### 覆盖率 `tools/coverage.ps1`

```powershell
# 6 个样本资产（交接文档口径）
.\tools\coverage.ps1 -AssetDir <资产目录> -Usmap <usmap>

# 目录下全部资产
.\tools\coverage.ps1 -AssetDir <资产目录> -Usmap <usmap> -Assets *
```

逐个函数跑探针，分母取自探针列出的函数总数，最后打印 `覆盖率: N / N`。
**不要手工挑函数统计**——分母错了比失败更危险。

### 数据位置

```
资产：D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic
usmap：D:\devenv\fmodel\Output\.data\kards1.60_No_UE4SS.jmap.3f2e5a28df2c1f777a1e80.usmap
UAssetAPI 源码参考：D:\files\projects\ref-uapi
UE 引擎源码（CCB-TEAM fork，release 分支）：D:\files\projects\ue-src\KismetCompilerVMBackend.cpp
```

## 八、构建与验证

```powershell
$env:DOTNET_CLI_HOME='D:\files\projects\.dotnet_home'
$env:NUGET_PACKAGES='D:\files\projects\.nuget_packages'
Set-Location D:\files\projects\out-repos\solstice-kismet

dotnet build tools\KismetProbe\KismetProbe.csproj
dotnet run --project tests\Solstice.Kismet.Verify     # 必须 40/40

.\tools\coverage.ps1 `
  -AssetDir 'D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic' `
  -Usmap    'D:\devenv\fmodel\Output\.data\kards1.60_No_UE4SS.jmap.3f2e5a28df2c1f777a1e80.usmap'
# 必须 1305 / 1305
```

**提交前必须两项都绿**：自检 40/40 + 真实资产覆盖率不掉。
（曾经因为跳过自检提交，导致 CI 挂掉，只能 `git reset --hard` 回滚。）

## 九、Git

```
remote sshpush = ssh://git@ssh.github.com:443/CCB-TEAM/solstice-kismet.git
```

`github.com:443` 走代理不稳（`TLS unexpected eof`），**用 SSH 走 443 端口**：

```powershell
$key = Join-Path $HOME '.ssh/id_ed25519_solstice'
git config core.sshCommand "ssh -i `"$key`" -o IdentitiesOnly=yes"
git push sshpush master
```

## 十、踩过的坑（务必避免重复）

### 1. 改 C# 代码只能用 `edit` 工具

PowerShell 的 `.Replace()` 不匹配时**静默返回原字符串**，看不出失败。这个项目里因此翻车至少 6 次：

| 错误形态 | 后果 |
|---|---|
| `Replace` 未命中却不检查 | 以为改了，实际没改，编译报「找不到成员」 |
| 单引号字符串里写 `` `n `` | 插进 C# 变成字面反引号 |
| here-string 终止符 `'@` 不在行首 | 吞掉后续所有内容，文件损坏 |
| 正则分组捕获写错 | 产出语法错误的代码 |

`edit` 工具会明确报「找不到」或「匹配多处」，**不会静默失败**。

### 2. 平行结构必须同步改

这个项目里有四对平行节点，改一个漏一个会留下隐患：

| 平行对 | 漏改次数 |
|---|---|
| `AssignNode` / `AssignBoolNode` | 1 |
| `ContextCallNode` / `CallArg` | 2 |
| `ContextMemberArg` / `CallArg`（`ContextKind`） | 1 |
| `PointerArg` 的三个形态标志（`Instance`/`Out`/`Default`） | 1 |

改模型前先 `grep` 出同后缀的 record，一次性对齐。

### 2.1 结构还原曾经有一个「必错」的 bug（2026-10 修）

编译器 `IfNode` 分支把 `JumpIfNot` 的补丁目标取成 `result.Count`（= **then 体开头**），
而不是 then 体之后的 else 起点。也就是说**所有带 then 体的 if 结构重建都是错的**，
只是被 `DecompileVerified` 的自动退化掩盖成「扁平」——这正是 47 个函数退化的主因。
当时的代码：

```csharp
var elseIdx = result.Count;   // ❌ 此时还指向 then 体，之后再没更新
foreach (var n in f.Then) EmitNode(n);
```

教训：**结构化模式的输出不会直接跑到用户面前**（退化会兜住），所以它的 bug 极难被发现。
判断可读性有没有真的变好，不能只看「字节一致」，要统计「退化函数数」。

### 2.2 `StackNode` 必须在每个调用节点上都保留

`ContextCallNode` 一度没有 `StackNode` 字段，回编译只能按函数名查 Import 表——
同名导入有多个时就会取错索引（实测 `-353` 变成 `-337`，结构完全一致但字节不同）。
`CallArg` 早就保留了，`ContextCallNode` 漏了。这正是第 2 条「平行结构」的又一次翻车。

### 3. 探针输出为空时，先怀疑进程崩溃

曾连续三轮把 `TryToGoHome` 的失败误判成「结构太复杂」，实际是 `IndexOutOfRangeException` 被管道吞掉。用 `Out-String` 捕获再匹配关键字，或直接看退出码。

### 4. 覆盖率必须核对分母

曾经只测 `BP_LevelManager` 的 11 个函数就报「11/11 全部通过」，实际该资产有 17 个函数，全量是 64/77。**报告覆盖率必须说明样本范围**。

**这条又复发过一次**：探针列函数时带 `.Take(30)`，`BPC_ChallengeAndRematch` 实际 48 个函数被截成 30 个，
于是文档里的「118/119」分母也是错的（真实分母 131）。现在截断已去掉，且分母一律从探针的总数取。

### 5. 「权限不足」先分清是沙箱还是 open 模式

`UnauthorizedAccessException` 指向 `Usmap.PathToStream` / `Usmap..ctor` 时，先怀疑
**UAssetAPI 用 `File.Open(path, FileMode.Open)` 打开了只读文件**（该重载等于 `ReadWrite`），
而不是文件真的没读权限。判别方法：用 `[IO.File]::OpenRead` 读同一路径，能读就是 open 模式问题。
探针已把 usmap 一并复制到临时目录，正常路径不会再遇到。

判断是否受沙箱限制时还要注意：**pwsh 自身的文件操作可能成功，而 `dotnet` 子进程被拒**，
所以「我用 pwsh 能读」不能推出「探针能读」。

### 6. `StructuralDiff` 曾漏比标量字段

它只递归比对子表达式，**跳过了偏移、指针、包索引、常量值**。于是会出现最难查的一种情况：
输出「结构完全一致」，但字节就是不同。补上标量比对后，一步就定位到
`ContextExpression.StackNode: -353 vs -337`。现在它会直接打印 `✗ [路径.字段] 取值不同`。

### 7. 测试数据造假会掩盖真 bug

自检里 loop 用例曾把 `JumpIfNot.CodeOffset` 写成 `20`，而整个合成函数的偏移表只到 `17`——
真实字节码不会出现这种越界落点。结果「目标越界就放弃结构化」这条正确规则反而让测试挂了。
**合成测试的偏移必须和 `Measure` 的尺寸表算出来的一致**，否则测的是幻觉。

## 十一、下一步（按收益排序）

### 1. 剩下 7 个状态机函数的嵌套还原

`ExecuteUbergraph_*` 这类函数用「Push/Pop 执行流 + 共享尾部」组织代码，线性配对规则表达不了。
可行方向：先按跳转目标建基本块图（探针的「基本块」段已有现成代码），
在块图上做支配关系分析，再把有唯一支配者的块合成 if/loop。
不要继续加节点类型——类型已经够用，缺的是控制流分析。

### 2. `EX_SwitchValue` 结构化还原（提升可读性）

目前走 `RawArg` 原样保留，字节正确但不可读。字段：

```csharp
public uint EndGotoOffset;
public KismetExpression IndexTerm;
public FKismetSwitchCase[] Cases;   // { CaseIndexValueTerm, NextOffset, CaseTerm }
public KismetExpression DefaultTerm;
```

难点：`NextOffset` 是相对偏移，重建时必须按新布局重算。

### 3. `K2Node_SwitchName` 级联分支识别

「8 连 `JumpIfNot` + 集中 case 体」的模式，可识别成 `SwitchNode`。需要按跳转目标建基本块图（探针已有基本块分析代码可参考）。

### 4. 扩大到 `Blueprints/Cards`

`Blueprints/Logic` 全部 28 个资产（1305 个函数）已全部通过。下一个战场是
`Blueprints/Cards` 下的卡牌蓝图（2,000+ 个，含 `LetValueOnPersistentFrame` 等特殊形态）：

```powershell
.\tools\coverage.ps1 -AssetDir <Cards 目录> -Usmap <usmap> -Assets *
```

按经验，每扩一次样本都会掉出一批缺失指令——上一轮 28 资产就暴露出 8 类
（`MetaCast`/`ClassContext`/`DefaultVariable`/`ArrayGetByRef`/`UnicodeStringConst`/
`InstanceDelegate`/`TransformConst`/`MapConst`）。诊断流程：

1. `coverage.ps1` 拿到失败清单
2. 对每个失败跑探针，看是 `异常: 表达式 XXX 暂不支持` 还是 `✗ 字段不同`
3. 前者补 `ToArg`/`ToExpr` 分支，后者先看 `StructuralDiff` 的标量比对

## 十二、相关仓库

| 仓库 | 内容 |
|---|---|
| `CCB-TEAM/solstice-kismet` | **本仓库**，独立的 Kismet 库 |
| `CCB-TEAM/Solstice` | 主项目（Blazor WASM 编辑器），引用本库 |
| `CCB-TEAM/solstice-pak` | pak 读写（v3 变体，已实现逐字节复现） |
| `CCB-TEAM/solstice-bin` | 二进制/本地化处理 |
| `CCB-TEAM/solstice-assetregistry` | AssetRegistry.bin 字节级写回 |
| `CCB-TEAM/solstice-jmap` | 从 usmap 生成函数签名表 |

本库**独立维护，不向主仓库同步**。
