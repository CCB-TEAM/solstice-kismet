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

## 二、最关键的边界：入口表不在字节码里

入口段起始偏移那张表（事件序号 → 字节偏移）**不在函数的字节码里**，
它由编辑器烘焙时写进 `UClass` 的事件引用。所以：

- 反编译只能靠**跳转目标**推出基本块（leader 算法），**推不出入口编号**。
  段数只是下界；真正有几个事件，字节码不告诉你。
- 因此「反编译时展开 → 编译时按 Flag 打回」是唯一诚实的路径：
  Flag 承载的就是这份字节码里没有的信息。

实测佐证：`BP_Chat` 里没有任何函数调用 `ExecuteUbergraph_BP_Chat` 并传常量偏移——
入口是从类的事件系统外部进来的。

## 三、反编译侧（已实现，只读）

```csharp
var uber = UberGraph.Analyze(bytecode, options);
// uber.DispatchExpression  调度用的表达式（通常是 LocalVariable EntryPoint）
// uber.DispatchIndex       调度头位置
// uber.PushesEpilogue      头部是否用 PushExecutionFlow 登记尾声
// uber.EpilogueOffset      尾声偏移
// uber.Sections            基本块（StartOffset / Nodes / Terminator / BranchTarget）
```

划分规则：**leader = 调度头之后第一条 ∪ 所有跳转目标**。
不要用「遇到终止符就切」——段尾连续两条 `Jump` 会切出 0 节点的假入口
（`BP_Chat` 的 `off=721`、`off=726` 就是这种蹦床跳转，按终止符切会多出 3 个假段）。

## 四、编译侧（设计，Flag 驱动）

```csharp
public sealed record UberGraphFlags(
    IReadOnlyList<uint> Entries,   // 事件顺序 → 入口偏移；这就是字节码里没有的那张表
    bool PushEpilogue = true,      // 头是否用 PushExecutionFlow 登记尾声
    bool EpilogueReturns = true);  // 尾声是 Return（实测 12/12 都是）
```

编译契约：

1. 按 `Entries` 的顺序烘入口段，段间的 `Jump`/`PopExecutionFlow` 目标**全部重算**；
2. 返回 `(expressions, newEntries)`——**偏移会变**，`newEntries` 必须写回 `UClass`
   的事件引用，否则运行时 `ComputedJump` 会跳到错误位置；
3. 段内容没变、顺序没变时，重新烘焙的结果必须与原始字节**逐字节相同**——
   这是它进入 `DecompileVerified` 校验的前提。

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
| 2 | `UberGraphNode` 进模型 + 编译器按 Flag 重新烘焙 | 待做 |
| 3 | 段命名：从段首的 `CallMulticastDelegate` / 导入名猜事件名 | 待做 |
| 4 | 把阶段 2 接入 `KismetWorkspace`，回写 `newEntries` | 待做 |

阶段 2 的验收门槛不变：**全量 1305/1305 逐字节一致**不掉，
且目前仍退化成扁平的 ubergraph 能输出「段 + 显式 goto」而不是一坨指令
（6 资产口径下 7 个退化函数里有 4 个是 ubergraph）。
