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
| 自检 `tests/Solstice.Kismet.Verify` | **59/59** |
| 真实资产往返一致（`Logic` 28 资产 + `Cards` 2053 资产） | **8115 / 8115 (100%)** |
| 结构还原退化（6 资产口径 131 函数） | **3 个函数**（原 47 个） |
| CI | 绿 |

> **分母修正**：文档曾记 118/119，那是**错的分母**。探针列函数时用了 `.Take(30)`，
> `BPC_ChallengeAndRematch` 实际 48 个函数被截到 30 个。去掉截断后 6 资产是 131 个函数，
> 扩到 `Logic` 全部 28 个资产是 1328 个函数、再加 `Cards` 全部 2053 个资产是 6787 个函数，合计 **8115**。

测试样本：`D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic` 下**全部**资产
（其中 4 个没有带字节码的函数：`BP_HandCardRichTextCache`、`E_PreGameStates`、
`S_KnockoutStatus`、`S_SalvagedCardInfo`）。

已修复的两批问题：

1. **字节保真**：`BP_LevelManager.ExecuteUbergraph_BP_LevelManager` 的 `Context 37 vs 36`——
   `EX_Context.ObjectExpression` 里包着 `EX_InterfaceContext`，反编译把包装层脱掉了。
   新增 `InterfaceContextArg` 原样保留（见第四节）。
2. **可读性**：结构还原退化从 47 个函数降到 3 个，见第六节。
   顺带补齐了 8 类指令（`MetaCast` / `ClassContext` / `DefaultVariable` / `ArrayGetByRef` /
   `UnicodeStringConst` / `InstanceDelegate` / `TransformConst` / `MapConst`），
   这才让 28 资产全量跑通。

## 三、目录结构

```
src/Solstice.Kismet/
  KismetModel.cs       节点/KArg 模型、CallShape、AssignKind、PinType
  KismetCompiler.cs    节点 → KismetExpression
  KismetDecompiler.cs  KismetExpression → 节点（含 if/loop 结构还原）
  BlockGraph.cs        基本块图：树还原不了时的可读表示（显式 goto）
  UsmapCatalog.cs      结构体 schema 查询（成员名 → 类型），用于解析 EX_StructMemberContext
  KismetExprBuilder.cs 简单表达式构造
  ExpressionWalker.cs  变量索引（名字 → KismetPropertyPointer）
  ImportIndex.cs       Import 表 → 函数索引
  CompileContext.cs
  FunctionCatalog.cs   函数签名表（jmap functions.json）
  KismetWorkspace.cs   对外门面：加载资产、选函数、编译、回写
  UberGraph.cs         ubergraph 结构分析：调度头 / 尾声 / 基本块 / 终止符
tools/KismetProbe/     命令行探针（结构 / 反编译 / 回编译 / 结构对比 / 基本块）
tools/coverage.ps1     真实资产覆盖率（逐函数跑探针，分母取自探针总数）
tools/KismetCoverage/  同进程批量覆盖率：一次扫完整个目录，输出失败原因聚类
tests/Solstice.Kismet.Verify/  59 项自检
samples/Usage/         可运行的用法示例（列函数/反编译/重烘/引脚/插入写回）
docs/bytecode.md       每个字节码指令的 token / 操作数 / 作用 / 出现场景 / 本库支持度
docs/ubergraph.md      ubergraph 的形态实测、展开规则、以及重新烘焙的 Flag 契约
docs/adding-a-call.md  新增一次调用的数据流向（解析/落表/生成/写回/边界）
docs/mcp.md            MCP 服务端：启动方式、8 个工具、状态与边界
src/Solstice.Kismet.Mcp/  MCP 服务端（stdio JSON-RPC），CI 里跑 --selftest
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

所以任何结构还原的改动，只要扁平模式仍是 8115/8115，就不会破坏保真性。

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

### 可读性现状（全语料 2081 资产 / 8115 函数）

**扁平退化已归零**：每个函数现在都能拿到三种可读表示之一，且字节永远一致。

| 表示 | Logic (1328) | Cards (6787) | 合计 | 占比 |
|---|---:|---:|---:|---:|
| 树形结构化（if/loop/赋值…） | 1307 | 6694 | **8001** | 98.6% |
| 基本块图（显式 goto） | 21 | 93 | **114** | 1.4% |
| ubergraph 展开 | 12 | 0 | 12 | 含在上面的树形里 |
| 扁平退化 | 0 | 0 | **0** | 0% |

三处关键设计：

1. **底部测条件的循环**（UE 编译 `for` 的形态）：回跳目标是**循环体开头**而不是条件，
   旧模型套「条件在顶部」会把回跳补错位置。新增 `BottomLoopNode`。
2. **机会性结构化**：折叠对一部分函数有益（Logic 57→21）、对另一部分有害（Cards 126→143），
   所以按「折叠 → 不折叠 → 基本块图」逐级尝试，**用字节比对裁决**。
3. **基本块图兜底**（`BlockGraphNode`）：它是对同一指令流的**划分**，不重排任何指令，
   只把跳转换成块标号（`JumpIfNot -> #9`），所以字节仍然逐字节一致。
   树还原不了的共享尾部/状态机因此变成「可读但非树形」，而不是退化成纯指令流。

副作用：`K2Node_SwitchName` 那种「8 连 `JumpIfNot` + 集中 case 体」现在在块图里一眼可读
（8 个块都对同一个 `CmpSuccess` 做 `NotEqual_NameName`，然后跳到不同 case 块）。

下一步可读性方向：把这 114 个块图函数里的常见形状**再提升回树形**，
优先 `SwitchName` 级联（可识别成 `SwitchNode`），而不是继续堆节点类型。

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

> 这个脚本的正则曾用 `(\S+)$` 抓函数名，**名字带空格的函数会被整行漏掉**
> （如 `Get Unread Messages In Battle`、`Has New Messages`）。被漏掉的函数不算失败，
> 所以看起来一切正常——这是最阴的一种分母错误。现已改成 `(.+?)$`，并核对探针声明的总数。

### 批量覆盖率 `tools/KismetCoverage`

逐函数起进程太慢（Cards 2000+ 资产、近 7000 函数要几小时），这个工具**一次进程扫完整个目录**：

```powershell
dotnet tools\KismetCoverage\bin\Debug\net10.0\KismetCoverage.dll <资产目录> <usmap> [--max N] [--verbose]
```

输出：资产数 / 函数数 / 逐字节一致数，以及**三种表示的分布**（树形 / 基本块图 / 扁平）
和失败原因聚类（`表达式 <op> 暂不支持`、`字节不一致 @offset 位于第 k 条 <op>`）。
扫一遍 `Cards` 约几分钟。

加 `--workspace` 时改为**走 `KismetWorkspace` 门面**（`Open` → `Decompile`），
验证主项目真正会用的那条路径（Import/Export 双向名字表、`CompileContext`、字节校验）。
实测 `函数 8115，成功 8115，失败 0`。这个模式是门面代码唯一的实测入口——
CI 机器上没有资产，自检跑不到它。

### SDK 便捷 API（`KismetWorkspace`）

调用方最常做的「读-改-写」，现在各一行：

| 方法 | 作用 |
|---|---|
| `Decompile(name)` | 反编译成节点（三种表示之一，已字节校验） |
| `Edit(name, transform)` | 反编译 → 交给委托改 → 编译写回 |
| `InsertCall(name, callee)` | 在末尾（Return 之前）插一次调用，参数按签名自动生成 |
| `ResolveFunction(name, owner?)` | **解析成引用**（名字 + 归属 + 包索引），同名歧义时报错并列出候选 |
| `CreateCall(ref)` | 只造节点，不写回；形态按签名选，Import 缺失时自动补 |
| `DefaultArgs(name)` | 看默认参数，`IsPlaceholder` 标出无法忠实合成的 |
| `PlanCall(name)` | 引脚级描述（编辑器 UI 用），同标占位 |
| `CreateCallArg(ref)` | 把调用当**值**用（赋值给变量 / 当参数） |
| `EnsureVariable(fn, name, instance)` | 取/构造变量指针：局部用 ResolvedOwner=函数导出，成员用 ResolvedOwner=本类导出 |
| `AssignCallResult(fn, ref, var)` | 调用并把返回值存进临时变量（等价 <c>Set CallFunc_XXX_ReturnValue</c>） |
| `BakeUbergraph(name)` | 重烘 ubergraph 并返回入口偏移表 |

`--plan` 模式暴露过一个真 bug：`NameProperty` 的默认值原本用空字符串建 `FName`，
而 `AddNameReference` **不接受空 FString**（`Cannot add an empty FString to the name map`）。
FName 引脚的默认值应该是 `None`——UE 里也是这个约定。

实测（`Logic` 24 个有函数的资产）：`Greater_IntInt`（2 int）、`Concat_StrStr`（2 str）、
`PrintString`（object/str/bool/struct/float/name 混合）各 **24/24**，
写回重读后参数个数与签名一致。

### 插入调用验证 `--insert`

```powershell
KismetCoverage.dll <资产目录> <usmap> --insert <函数名> --catalog <functions.json> [--max N]
```

对每个资产取**最小的函数**，走一遍编辑器的完整链路：
`Decompile → CreateCall（按 jmap 签名选形态）→ Generate 写回 → Save → 重新打开 → Decompile 确认新调用在`。

实测（`Logic` 24 个有函数的资产）：
静态零参 `KardsClearLaunchNotification` **24/24**；非静态 `ClearCardTexts`（走 `EX_Context`）**24/24**。

它暴露过两个真问题，都已修：

1. **新建 Context 调用没有 `RValuePointer`**：编译器原本直接抛异常。
   现在用**空 `FFieldPath`**（0 段路径 + owner 0）——UE 对无返回值调用的写法。
   注意 `XFER_PROP_POINTER` 的写入要求 `New` 非空，用 `Old` 形态必 NRE。
2. **接口类资产没有可克隆的 Import 模板**（`I_*` 资产里一条 `Function` 导入都没有）。
   `ImportIndex.FindTemplate` 现在会按 UE 固定约定合成（`ClassPackage = /Script/CoreUObject`）。

⚠️ 新建调用里 `ContextKind` 固定为 `Normal`、`InnerShape` 固定为 `Final`，
空 `RValuePointer` 只验证过**字节可读回**，没有在 UE 运行时验证过。

### 参数自动填充（jmap 的第二种用法）

`CreateCall(name)` 不给参数时按签名自动生成（`DefaultArgs`）。jmap 里的类型是
**Property kind**（17 种），映射如下：

| jmap 类型 | 生成 | 忠实? |
|---|---|---|
| `BoolProperty` | `BoolArg(false)` | ✅ |
| `IntProperty` / `ByteProperty` / `EnumProperty` | `IntArg(0)` / `ByteArg(0)` | ✅ |
| `FloatProperty` / `DoubleProperty` | `FloatArg(0)` / `DoubleArg(0)` | ✅ |
| `StrProperty` / `NameProperty` | `StringArg("")` / `NameArg(FName)` | ✅ |
| `ObjectProperty` / `ClassProperty` / `DelegateProperty` / `SoftObjectProperty` | `NoObjectArg()`（空引用） | ✅ |
| `TextProperty` / `StructProperty` / `ArrayProperty` / `MapProperty` / `SetProperty` | `NoObjectArg()` | ❌ **占位** |

占位的会带 `IsPlaceholder = true`，调用方必须替换——它们是字节合法但语义不全的。

实测：`Greater_IntInt`（2 个 int 参）、`Concat_StrStr`（2 个 str 参）各 **24/24**，
且写回重读后**参数个数与签名一致**（`--insert` 会校验这一点）。

> 两个 jmap 文件不要混：`functions.json`（4.9MB，13,064 函数）含**引擎类**；
> `functions.kards.json`（474KB，1,416 函数）只有**游戏侧类**。
> 插引擎函数（`PrintString`、`Greater_IntInt`）必须用前者。

用它测出的真实口径（2026-10）：

| 目录 | 资产 | 函数 | 结果 |
|---|---:|---:|---|
| `Blueprints/Logic` | 28 | 1328 | **1328/1328** |
| `Blueprints/Cards` | 2053 | 6787 | **6787/6787** |
| **合计** | **2081** | **8115** | **8115/8115** |

Cards 那一轮第一次跑出 2 个失败，都指向 `EX_SoftObjectConst`（软引用常量），
补齐后 100% 通过——**扩大语料的价值就在这**：28 个 Logic 资产里根本没出现过这个指令。

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
dotnet run --project tests\Solstice.Kismet.Verify     # 必须 59/59

.\tools\coverage.ps1 `
  -AssetDir 'D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\Logic' `
  -Usmap    'D:\devenv\fmodel\Output\.data\kards1.60_No_UE4SS.jmap.3f2e5a28df2c1f777a1e80.usmap'
# 必须 8115 / 8115
```

**提交前必须两项都绿**：自检 59/59 + 真实资产覆盖率不掉。
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

**这条又复发过两次**：

1. 探针列函数时带 `.Take(30)`，`BPC_ChallengeAndRematch` 实际 48 个函数被截成 30 个，
   于是文档里的「118/119」分母也是错的（真实分母 131）。截断已去掉。
2. `coverage.ps1` 用 `(\S+)$` 抓函数名，**名字带空格的函数整行漏掉**
   （`Get Unread Messages In Battle`、`Has New Messages`）。被漏掉的函数不报失败，
   所以「1305/1305 全绿」看起来毫无问题——真实分母是 1328。
   现在改用 `(.+?)$`，并与探针声明的函数总数交叉核对。

**教训**：分母错误有两种形态，一种是「多算」（截断、重复），一种是「少算」（漏行）——
后者更危险，因为它表现为全绿。任何覆盖率脚本都要把「声明的总数」和「实际跑过的数」对账。

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

### 8. 前向引用不能提前补丁（ubergraph 重新烘焙）

跨段跳转可能指向**排在后面**的段。发射 `段 #81` 时若直接用 `sectionStart[86]` 打补丁，
那时 86 还没发射，取到的是 `0`——字节数完全一样、内容全错
（表现为 `✗ [937.CodeOffset] 取值不同：35796 vs 0`）。
必须先把 `(jump, 目标段序号)` 收集起来，等所有段发射完再统一补。
探针现在会额外列出「未打补丁的跳转」（CodeOffset 仍为 0 的），这类问题一眼可见。

### 9. 启发式判定要严格，且会被自检反噬

ubergraph 判定最初允许 `ComputedJump` 出现在前 4 条内，结果把自检里合成的
「执行流」用例误判成 ubergraph，自检直接挂（测试反过来说明实现太宽）。
收紧到「必须在前两条之内」才与 UE 的实际布局一致。
**形状识别这类启发式，宁可漏判（退化成普通还原）也不要误判。**

## 十一、下一步（按收益排序）

### 1. 剩下 3 个普通函数的共享尾部还原

`setTimedOutAfterReconnect`、`TryToGoHome`、`ParseNotification`——它们不是 ubergraph，
但同样是「Push/Pop 执行流 + 共享尾部」，线性配对规则表达不了。
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
