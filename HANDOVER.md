# Solstice.Kismet 交接文档

> 写给接手的新对话/新开发者。读完这份文档应该能直接上手，不需要重读历史对话。

## 一、项目定位

从 UE5 蓝图资产的字节码做**双向**处理：

- **反编译**：`KismetExpression[]` → 编辑器节点模型（`KNode`）
- **编译**：节点模型 → `KismetExpression[]`
- **回写**：经 UAssetAPI 序列化回 `.uasset`/`.uexp`

核心指标是**往返逐字节一致**（decompile → compile → 序列化，与原始字节完全相同）。

## 二、当前状态（提交 `c498dd4` 之后）

| 项 | 结果 |
|---|---|
| 自检 `tests/Solstice.Kismet.Verify` | **19/19** |
| 真实资产往返一致 | **131 / 131 (100%)** |
| CI | 绿 |

测试样本：`D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic` 下 6 个资产的**全部**函数
（`BP_Chat`、`BP_MatchData`、`BPC_TurnTimer`、`BP_LevelManager`、`BP_NotificationManager`、`BPC_ChallengeAndRematch`）。

> **分母修正**：文档此前记的 118/119 是**错的分母**。探针列函数时用了 `.Take(30)`，
> `BPC_ChallengeAndRematch` 实际有 48 个函数，被截到 30 个，于是 6 个函数从未被测过。
> 去掉截断后全量是 131 个函数，全部通过。这正是第十节第 4 条「覆盖率必须核对分母」的复发。

原先唯一的失败 `BP_LevelManager.ExecuteUbergraph_BP_LevelManager`（`Context 长度 37 vs 36`）
已修复：`EX_Context.ObjectExpression` 里包着的是 **`EX_InterfaceContext`**，
反编译把包装层脱掉了，回编译只剩里面的 `EX_LocalVariable`，每条少 1 字节。
修法是新增 `InterfaceContextArg` 原样保留（见第四节）。

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
tests/Solstice.Kismet.Verify/  19 项自检
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

所以任何结构还原的改动，只要扁平模式仍是 131/131，就不会破坏保真性。

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

**已知局限**：`K2Node_SwitchName` 编译出的「8 连 `JumpIfNot` + 集中 case 体」无法还原成 switch 结构。这类函数靠 `DecompileVerified` 的自动退化保证字节正确，但可读性差。

`EX_SwitchValue` 目前走 `RawArg` 原样保留，**没有做结构化还原**。

**残留的结构还原 bug**：`BP_LevelManager.ExecuteUbergraph_BP_LevelManager` 现在字节一致了，
但结构化模式仍会在 `[17] JumpIfNot 落点无效：exit=0 start=17 target=15` 处失败
（`4249 vs 4195`），最后靠自动退化走扁平模式。这是 `EX_ComputedJump` 分发处的
回跳目标（15）落在 `[0..1]` 之外导致的，纯可读性问题，不影响字节。

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
dotnet run --project tests\Solstice.Kismet.Verify     # 必须 19/19

.\tools\coverage.ps1 `
  -AssetDir 'D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic' `
  -Usmap    'D:\devenv\fmodel\Output\.data\kards1.60_No_UE4SS.jmap.3f2e5a28df2c1f777a1e80.usmap'
# 必须 131 / 131
```

**提交前必须两项都绿**：自检 19/19 + 真实资产覆盖率不掉。
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

这个项目里有三对平行节点，改一个漏一个会留下隐患：

| 平行对 | 漏改次数 |
|---|---|
| `AssignNode` / `AssignBoolNode` | 1 |
| `ContextCallNode` / `CallArg` | 1 |

改模型前先 `grep` 出同后缀的 record，一次性对齐。

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

## 十一、下一步（按收益排序）

### 1. 修结构还原的 `EX_ComputedJump` 回跳（可读性）

`BP_LevelManager.ExecuteUbergraph_BP_LevelManager` 字节已一致，但结构化模式仍失败：
`[17] JumpIfNot 落点无效：exit=0 start=17 target=15`，回编译 4249 vs 4195，最后退化成扁平。

根因在 `BuildBlock` 处理 `EX_ComputedJump` / `EX_PushExecutionFlow` 的跳转目标落点：
`target=15` 落在首块 `[0..1]` 的边界外，`Block` 的 end 推导把它算成 0。
修好之后这个函数能从扁平升级成可读的 if/loop 结构。

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

### 4. 扩大测试样本

当前只测了 `Blueprints/Logic` 下 6 个资产（131 个函数全部通过）。建议扩展到：

- `Blueprints/Logic` 全部 28 个资产 → `.\tools\coverage.ps1 ... -Assets *`
- `Blueprints/Cards` 下的卡牌蓝图（2,000+ 个，含 `LetValueOnPersistentFrame` 等特殊形态）

跑法见第七节。

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
