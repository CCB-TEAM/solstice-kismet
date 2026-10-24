# solstice-kismet

从**平铺节点模型**生成 UE 蓝图函数的 Kismet 字节码，并写回 `.uasset`。

依赖 [UAssetAPI](https://github.com/atenfyr/UAssetAPI)（读写资产与表达式序列化），生成逻辑本身是本仓库的。

## 能生成什么

| 节点 | 指令 |
|---|---|
| `AssignNode` | `EX_Let` |
| `AssignBoolNode` | `EX_LetBool` |
| `CallNode` | `EX_FinalFunction` |
| `ContextCallNode` | `EX_Context` + `EX_FinalFunction` |
| `VirtualCallNode` | `EX_LocalVirtualFunction` |
| `IfNode` | `EX_JumpIfNot` + `EX_Jump` |
| `LoopNode` | `EX_JumpIfNot` + `EX_Jump`（回跳） |
| `ReturnNode` | `EX_Return` + `EX_EndOfScript` |

## 用法

```csharp
using Solstice.Kismet;

// 1. 从字节打开资产（内部会写临时文件，因为 UAssetAPI 只能按路径构造）
var ws = KismetWorkspace.Open(uassetBytes, uexpBytes, usmapBytes, "/tmp/ws");

// 2. 可选：载入函数签名表（jmap 产物），用于自动构造插销与推断调用形态
ws.AttachCatalog(FunctionCatalog.Load(functionsJson));

// 3. 描述逻辑
var nodes = new List<KNode>
{
    new AssignNode("EntryPoint", new IntArg(7)),
    new IfNode(new LocalArg("EntryPoint"), [
        new VirtualCallNode("ExecuteUbergraph_card_brawl_test1", [new LocalArg("EntryPoint")]),
        new ReturnNode()
    ]),
    new ReturnNode()
};

// 4. 预览（不写回）或写回
var (opcodes, hex, bytes) = ws.Preview(nodes, "ExecuteUbergraph_card_brawl_test1");
var (ops2, hex2, _)    = ws.Generate(nodes, "ExecuteUbergraph_card_brawl_test1");
var saved = ws.Save("/tmp/ws");   // 返回 uasset + uexp 两半，缺一不可
```

## 几个必须知道的编码事实

这些都是实测出来的，猜错会导致生成的字节码引擎读不了。

### 1. 跳转操作数是**字节偏移**，不是表达式下标

```
EX_Jump        = token(1) + uint32 偏移          共 5 字节
EX_JumpIfNot   = token(1) + uint32 偏移 + 内嵌条件表达式
EX_EndOfScript = token(1)                        值 0x53
```

条件表达式**内嵌**在 `EX_JumpIfNot` 里，不是数组里的独立一项。

所以跳转目标必须两遍算：先按每条指令的实际序列化长度累加得到字节偏移，再回填。
`CompileToExpressions` 接受一个 `measure` 回调（`Func<KismetExpression, uint>`）来做这件事。

### 2. `EX_Context.Offset` = `ContextExpression` 的序列化长度

它是「对象为 null 时要跳过的字节数」，不是 vtable 偏移。构造时直接取子表达式的 `GetSize`。

### 3. `KismetPropertyPointer` 在 UE5.6 必须用 `New`（`FFieldPath`）形态

```csharp
new KismetPropertyPointer(new FFieldPath
{
    Path = [new FName(asset, "EntryPoint")],
    ResolvedOwner = new FPackageIndex(3)   // 正数=本包导出，负数=Import
});
```

用 `new KismetPropertyPointer(new FPackageIndex(...))`（`Old` 形态）会 `NullReferenceException`，
因为 UE5.6 走 `ShouldSerializeNew` 分支，序列化时读的是 `New`。

### 4. `EX_Let` 需要两份，`EX_LetBool` 只要一份

| | 变量表示 | 赋值源 |
|---|---|---|
| `EX_Let` | `Value`（指针）**和** `Variable`（表达式）都要给 | `Expression` |
| `EX_LetBool` | 只有 `VariableExpression` | `AssignmentExpression`（`EX_True`/`EX_False`） |

### 5. Import 链是三级，索引 1 基负数

```
Package   cls=Package   outer=0
Class     cls=Class     outer=包
Function  cls=Function  outer=类
```

新增条目时**克隆同类现有导入**，只替换 `ObjectName` 与 `OuterIndex`——不要猜
`ClassPackage` / `PackageName` 的值。

### 6. 调用形态由签名推断，不需要调用方指定

| 条件 | 指令 |
|---|---|
| 函数是本资产导出的 | `EX_LocalVirtualFunction`（按 FName） |
| 签名含 `FUNC_Static` | `EX_FinalFunction`（StackNode 指向 Import） |
| 非静态 | `EX_Context`（需要一个对象表达式） |

## 验证方式

生成的字节码写回资产后**重新加载并比对**。本仓库的实现曾做到：

- 一个含 56 条表达式的函数，读出再写出与原 `.uexp` 中对应区段**逐字节相同**
- 生成的资产再加载、再写出，两半字节保持一致（确定性）
- 新增 Import 后重写仍稳定，原有导出数量不变

## 限制

- 只覆盖上表那几类指令；`EX_CallMath`、数组/结构体、`EX_ComputedJump` 分发等未实现
- 属性指针复用目标函数已有的变量；**新增变量**需要自己构造 `FFieldPath` 并确保名字在名字表里
- 函数名若不在资产名字表里，`new FName(asset, name)` 会分配新下标——能写出，但读回可能失败
- 只验证过 UE 5.6

## 许可

MIT
