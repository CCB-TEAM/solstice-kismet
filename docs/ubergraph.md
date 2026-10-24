# Ubergraph：展开与重新烘焙

> 结论先行：`ExecuteUbergraph_*` **不是**可以还原成 if/loop 的嵌套控制流，
> 它是一张**带共享尾部和跨段跳转的基本块图**。所以正确的做法是「展开」成入口段，
> 而不是硬套结构；重新生成时必须由调用方提供入口表（Flag）。

## 一、实测形态（`Blueprints/Logic` 里全部 12 个 ubergraph）

```
[调度头]  ComputedJump(EntryPoint)          ← 把入口号当字节偏移直接跳
              ↑ 有的前面还有一条 PushExecutionFlow(尾声地址)
[入口段]  每个事件一段，段尾是 Jump(尾声) 或 PopExecutionFlow
[尾声]    Return + EndOfScript             ← 12/12 完全一致
```

| 特征 | 实测结果 |
|---|---|
| 尾声形态 | **12/12 都是 `EX_Return` + `EX_EndOfScript`** |
| 调度头 | 2 种：直接 `ComputedJump`（如 `BP_Chat`）；先 `PushExecutionFlow` 再 `ComputedJump`（如 `BPC_TurnTimer`、`BP_LevelManager`） |
| 段尾终止符 | `Jump(尾声)`、`PopExecutionFlow`、`PopExecutionFlowIfNot`、跳向另一段的 `Jump` |
| 规模 | 57 ~ 1535 条表达式；`BPC_TurnTimer` 有 59 个基本块 |

`BP_Chat` 的实际分析结果（`tools/KismetProbe` 会直接打印）：

```
调度头  : [0] ComputedJump EntryPoint
尾声    : off=1565（段尾直接 Jump 过来）
入口段  : 4 个
  #0 [ 1..] off=10    结束=JumpToEpilogue -> 1565
  #1 [ 5..] off=83    结束=JumpToEpilogue -> 1565
  #2 [22..] off=648   结束=JumpToOtherSection -> 83
  #3 [45..] off=1267  结束=FallsThrough
```

## 二、最关键的边界：入口表不在字节码里，而在**调用点**里

入口偏移**不在** ubergraph 自己的字节码里——这一点是对的。但它也**不在 `UClass` 的事件引用里**
（本机实测 UE 5.6：`FunctionExport` 尾部那 8 字节 `EventGraphFunction` + `EventGraphCallOffset`
在全部函数上都是 0）。它被烘进了**调用点**：每个事件存根都是

```
LocalFinal ExecuteUbergraph_BP_Chat(726)      ← 726 就是入口偏移（EX_IntConst）
```

所以：

- 反编译只能靠**跳转目标**推出基本块（leader 算法），**推不出入口编号**——段数只是下界；
  一个段可以有多个入口（事件从段中间进入），段起始偏移 ≠ 入口表。
- 想拿到真正的入口表，扫全资产找「调用该 ubergraph 且首参是 `EX_IntConst`」的调用点，
  见 `KismetWorkspace.UbergraphCallSites()`。
- 改 ubergraph 时唯一的硬约束：**这些常量指向的偏移必须仍然落在同一条指令上**。
  往段尾追加（工具支持的唯一改法）不会移动任何入口；改段内布局会整体错位，
  `KismetWorkspace` 会回滚编辑并拒绝写盘（`CheckUbergraphEntryPoints` / `VerifyEditedUbergraphs`）。

实测佐证（`BP_Chat`）：4 个段（10 / 83 / 648 / 1267），但有 6 个入口点
（`StartNUIChat`@107、`CloseChat`@721、`MinimizeChat`@726、`Completed_142D…`@731、
`Completed_E1C5…`@938、`ReceiveMessage`@1295）。

## 三、反编译侧（已实现）

```csharp
var uber = UberGraph.Analyze(bytecode, options);
// uber.DispatchExpression  调度用的表达式（通常是 LocalVariable EntryPoint）
// uber.DispatchIndex       调度头位置
// uber.PushesEpilogue      头部是否用 PushExecutionFlow 登记尾声
// uber.EpilogueOffset      尾声偏移
// uber.Sections            基本块（StartOffset / Nodes / Terminator / BranchTarget）
```

`Decompile` 在函数开头（`baseOffset == 0`、未强制扁平）先尝试 ubergraph 形状，
命中就直接产出（段名是从段首调用猜出来的）：

```
UberGraph dispatch=EntryPoint push  4 段
  Section @10 OnChatMinimized__DelegateSignature JumpToEpilogue
  Section @83 OnChatCreated__DelegateSignature JumpToEpilogue
  Section @648 OnChatClosed__DelegateSignature JumpToOtherSection -> #1
  Section @1267 UpdateMessageNotifications FallsThrough
Return
```

### 段命名

段首通常就是一次 `CallMulticastDelegate`，它的目标（`StackNode`）就是**本类的事件属性**，
所以名字基本就是事件名。两个前提：

- 包索引要**两个方向都能解析**：负数 = Import，正数 = 本包 Export。
  只解析 Import 的话事件属性会显示成 `<17>`，段就没法命名（探针曾漏了这个）。
- 猜不出来就不编：段首是普通赋值/调用时，只退化成 `Section @offset`。

实测 12/12 ubergraph 的 100% 段都拿到了名字（共 667 段），
`BP_Chat` 的四段直接是 `OnChatMinimized/OnChatCreated/OnChatClosed` 加 `UpdateMessageNotifications`。

划分规则：**leader = 调度头之后第一条 ∪ 所有跳转目标**。
不要用「遇到终止符就切」——段尾连续两条 `Jump` 会切出 0 节点的假入口
（`BP_Chat` 的 `off=721`、`off=726` 就是这种蹦床跳转，按终止符切会多出 3 个假段）。

判定必须严格：调度头只认**前两条之内**的 `EX_ComputedJump`。
放宽到前 4 条会把普通函数里的 `ComputedJump` 误判成 ubergraph——
自检「执行流」用例就是这么挂的，等于测试反过来救了实现。

## 四、编译侧（已实现）

编译 `UberGraphNode` 时按 `Sections` 顺序重新烘焙：调度头 → 各段（段体 + 段尾）→ 尾声，
所有 `Jump` / `PushExecutionFlow` 目标按新布局重算。

```csharp
// 模型（src/Solstice.Kismet/KismetModel.cs）
public sealed record UberGraphNode(KArg DispatchExpression, bool PushesEpilogue, IReadOnlyList<UberSectionNode> Sections);
public sealed record UberSectionNode(uint StartOffset, IReadOnlyList<KNode> Nodes, UberTerminator Terminator, int? TargetOrdinal, KArg? Condition);
```

实测：**12/12 ubergraph 展开后重新烘焙，逐字节一致**（`BP_Logic` 1535 条、`BP_VisualController` 110 段也在内）。

### 一个必须知道的坑：前向段引用

跨段跳转可能指向**排在后面**的段。如果在发射 `段 #81` 的时候就用
`sectionStart[86]` 去补丁，那时 86 还没发射，取到的是 `0`——字节数一样、内容全错，
表现为 `✗ [937.CodeOffset] 取值不同：35796 vs 0`。

正确做法：先把 `(jump, 目标段序号)` 收集起来，等所有段发射完、`sectionStart` 填满后再统一打补丁。
探针现在会在差异报告里额外列出「未打补丁的跳转」（CodeOffset 仍为 0 的），这类问题一眼可见。

### Flag 契约（外部重新烘焙用）

```csharp
public sealed record UberGraphFlags(
    IReadOnlyList<uint> Entries,   // 事件顺序 → 入口偏移；这就是字节码里没有的那张表
    bool PushEpilogue = true,      // 头是否用 PushExecutionFlow 登记尾声
    bool EpilogueReturns = true);  // 尾声是 Return（实测 12/12 都是）
```

按 `Entries` 顺序烘段时偏移会变，必须让**调用点**里那些 `EX_IntConst` 跟着改，
否则运行时 `ComputedJump` 会跳到错误位置。段内容与顺序不变时，结果应与原始字节完全相同。

实际的 API 是 `KismetCompiler.BakeUbergraph`——它直接**返回**重新烘焙的结果：

```csharp
var baked = KismetCompiler.BakeUbergraph(nodes, context);
// baked.Expressions    重新烘焙出的表达式
// baked.EntryOffsets   段顺序的段起始偏移（注意：这是**段**的起始，不是入口表）
// baked.EpilogueOffset 尾声偏移
```

真正的入口表用 `KismetWorkspace.UbergraphCallSites(name)` 从调用点扫出来；
两者本就可以不相等（一个段可以有多个入口）。写盘前用
`CheckUbergraphEntryPoints` / `VerifyEditedUbergraphs` 校验入口点没被移动。

验证：12/12 ubergraph 在内容不变时，`EntryOffsets` 与原始段起始偏移**逐项相等**，
且字节逐字节一致。这条由自检锁住（`ubergraph 烘焙: 入口表与原表一致`）。

## 五、控制流为什么难

四类模式都不是树：

1. **共享尾部**：多个段的正常出口都跳到同一个尾声（`Return`）。
   树里只有一个出口，这里 N 个段共用一个。
2. **跨段跳转**：段 A 可以直接跳到段 B 的开头（`Jump -> 83`），
   树里没有「兄弟节点之间跳转」这种边。
3. **蹦床**：单独一条 `Jump -> 某段`（`[26] Jump->648`），既不是段也不是尾声，
   纯粹是跳板。
4. **执行流栈**：`Push`/`Pop` 是协程式转移，`Pop` 的目标由运行时栈决定，
   静态上是一条「不知道去哪」的边；`PopExecutionFlowIfNot` 更把它变成条件边。

所以对 ubergraph 的产出目标是**基本块图 + 显式 goto**，而不是伪造的 if/loop 树。
对普通函数（非 uber），现有 `BuildBlock` 的嵌套还原仍然有效——两者走不同通道。

## 六、分阶段计划

| 阶段 | 内容 | 状态 |
|---|---|---|
| 1 | 只读分析：调度头 / 尾声 / 基本块 / 终止符 | **已完成** |
| 2 | `UberGraphNode` 进模型 + 编译器按段重新烘焙 | **已完成**（12/12 逐字节一致） |
| 3 | 段命名：从段首的 `CallMulticastDelegate` 还原事件名 | **已完成**（667 段全部命名） |
| 4 | `BakeUbergraph` 返回入口偏移表，供写回 UClass | **已完成**（12/12 与原表一致） |
| 5 | 真正按用户 Flag 改顺序/增删段 | 待做 |

阶段 1–4 的验收结果：**全量 1305/1305 逐字节一致**保持，
6 资产口径的结构退化从 7 个函数降到 **3 个**（12 个 ubergraph 全部展开）。
剩下 3 个是普通函数里的共享尾部状态机：`setTimedOutAfterReconnect`、
`TryToGoHome`、`ParseNotification`。

阶段 5 才是「按用户意图重新烘」：现在能忠实重烘并给出新偏移表，但还不能改段顺序，
因为入口表在外部、且改顺序会牵动 UClass 的事件引用——那一步需要与主项目联调。
