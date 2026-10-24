# 新增一次函数调用：数据流向

> 这份文档说明「在某个函数里插入一次调用」时，数据从哪来、经过哪些结构、最后怎么变成字节。
> 对应实现：`KismetWorkspace.ResolveFunction / CreateCall / InsertCall / Edit`。

## 一、总览

```
  用户输入                  解析                        落表                      生成                        写回
┌──────────┐   ┌──────────────────────┐   ┌──────────────────┐   ┌──────────────────┐   ┌────────────────┐
│ "Greater_│──▶│ ResolveFunction      │──▶│ Import 表（补条目）│──▶│ CreateCall       │──▶│ Generate       │
│  IntInt" │   │  ① 名字+归属 精确匹配 │   │  Function         │   │  CallNode /      │   │  → ScriptByte- │
│ 或 ref    │   │  ② 本资产导出         │   │   └ Class         │   │  ContextCallNode │   │    code 数组   │
└──────────┘   │  ③ Import 唯一同名    │   │      └ Package    │   │  （含 StackNode）│   │  → Save        │
               │  ④ jmap 签名补导入    │   └──────────────────┘   └──────────────────┘   └────────────────┘
               └──────────────────────┘            ▲                      ▲                     │
                        ▲                        │                      │                     ▼
                 ┌──────┴──────┐          ┌──────┴──────┐        ┌──────┴──────┐      ┌────────────────┐
                 │ functions.  │          │ 资产自身     │        │ 参数默认值   │      │ 重新打开 →     │
                 │ json (jmap) │          │ Import/Export│        │ DefaultArgs │      │ Decompile 校验 │
                 │ objects.json│          │ 表           │        │ (17 种 kind)│      └────────────────┘
                 └─────────────┘          └─────────────┘        └─────────────┘
```

## 二、四个数据源，各管什么

| 数据源 | 位置（本机） | 提供 | 不提供 |
|---|---|---|---|
| **资产本身**（`.uasset`+`.uexp`） | `D:\devenv\fmodel\Output\Exports\kards\...` | Import/Export 表、名字表、字节码 | 函数签名、结构体 schema |
| **jmap functions.json** | `D:\files\projects\jmap-out\functions.json`（13,064 函数，含引擎类）<br>`functions.kards.json`（1,416，只有游戏侧） | 函数归属类、`flags`（`FUNC_Static` 等）、参数（`name/type/dir`） | 入口偏移表、结构体成员 |
| **jmap objects.json** | `D:\files\projects\jmap-out\objects.json` | 对象全路径 → `type/outer/class/super`，用于把「类短名」解析成全路径 | 函数信息 |
| **usmap** | `D:\files\projects\kards\Content\Paks\kards-5.6.1-...usmap`（19,408 schema） | 结构体/类的成员与类型（`Vector.X : DoubleProperty`） | **蓝图资产定义的结构**（`S_*` 不在 usmap 里） |

## 三、解析：`ResolveFunction`

```csharp
var callee = ws.ResolveFunction("Greater_IntInt");                       // 只给名字
var callee = ws.ResolveFunction("Greater_IntInt", "KismetMathLibrary");  // 类短名（走 objects.json）
var callee = ws.ResolveFunction("GetSession", "/Script/kards.KardsSession"); // 全路径
```

产出 `FunctionRef`：

| 字段 | 来源 | 例 |
|---|---|---|
| `Name` | 用户 / 签名表 | `Greater_IntInt` |
| `OwnerPath` | ② 空（本资产）/ ③ 由导入链推 / ④ jmap `OwnerPath` | `/Script/Engine.KismetMathLibrary` |
| `Index` | Import 表条目（负数）或导出（正数） | `-124` |
| `IsExported` | 该名字是否在本资产 Export 表里 | `false` |
| `Signature` | jmap | `flags`、参数表 |

**解析顺序与失败行为**：

| 步 | 依据 | 失败时 |
|---|---|---|
| ① | 给了归属 → `(名字, 归属)` 精确匹配 | 继续 |
| ② | 本资产导出的同名函数 | 继续 |
| ③ | Import 表里**唯一**同名 | **多个归属 → 抛异常并列出候选** |
| ④ | jmap 签名 → 补导入 | 无签名 → 抛异常（不猜形态） |

> 归属类是从**导入链**推的：`Function` 的 `outer` 是 `Class`，`Class` 的 `outer` 是 `Package`，
> 拼成 `/Script/Engine.KismetMathLibrary`。所以第 ③ 步不依赖任何外部表。

## 四、落表：Import 怎么补

`ImportIndex.EnsureFunction(asset, name, ownerPath)`：

1. **先找**：`(名字, 归属)` 已在 Import 表 → 直接复用（**不会**因为同名而取错别人的）
2. **否则补三级导入**，优先**克隆**同类现有条目：

```
-124:Function 'Greater_IntInt'  [clsPkg=/Script/CoreUObject]  outer=-123
-123:Class    'KismetMathLibrary'                            outer=-122
-122:Package  '/Script/Engine'                               outer=0
```

- 克隆时沿用模板的 `ClassPackage` / `PackageName` / `bImportOptional`，只换 `ObjectName` 与 `OuterIndex`
- 资产里**一条同类导入都没有**时（如 `I_*` 接口资产）按 UE 约定**合成**：
  `ClassPackage = /Script/CoreUObject`，`ClassName = Package / Class / Function`
- 实测两条路径产出的字段完全一致（`clsPkg=/Script/CoreUObject pkgName=` 空）

## 五、生成节点：形态怎么选

`CreateCall(ref)` 依据 `IsExported` 与签名的 `FUNC_Static`：

| 条件 | 节点 | 字节码 |
|---|---|---|
| `IsExported` | `VirtualCallNode` | `EX_LocalVirtualFunction`（按 FName 寻址） |
| `FUNC_Static` | `CallNode` | `EX_FinalFunction`（StackNode = Import） |
| 其余 | `ContextCallNode` | `EX_Context` + 内层 `EX_FinalFunction`，对象默认 `EX_Self` |

节点里一定带 `StackNode`（= `FunctionRef.Index`）。**编译器不再按名字回退**：

```
有 StackNode            → 用
给了 Owner              → 按 (名字, 归属) 精确查；查不到报错
无 Owner 且同名唯一      → 用
无 Owner 且同名多个      → 报错并列出候选
```

## 六、参数：`DefaultArgs`

jmap 的类型是 **Property kind**（17 种）：

| kind | 生成 | 忠实 |
|---|---|---|
| `BoolProperty` | `BoolArg(false)` | ✅ |
| `IntProperty` / `ByteProperty` / `EnumProperty` | `IntArg(0)` / `ByteArg(0)` | ✅ |
| `FloatProperty` / `DoubleProperty` | `FloatArg(0)` / `DoubleArg(0)` | ✅ |
| `StrProperty` | `StringArg("")` | ✅ |
| `NameProperty` | `NameArg(FName("None"))` | ✅（空字符串不能进名字表） |
| `ObjectProperty` / `ClassProperty` / `DelegateProperty` / `SoftObjectProperty` | `NoObjectArg()` | ✅ |
| `TextProperty` / `StructProperty` / `ArrayProperty` / `MapProperty` / `SetProperty` | `NoObjectArg()` | ❌ **占位**（`IsPlaceholder = true`） |

## 七、临时变量（`CallFunc_XXX_ReturnValue`）

UE 把「调用的返回值」存进**普通局部变量**，不是特殊机制。实测形态：

```
EX_LocalVariable   path=[CallFunc_GetChat_chat]   resolvedOwner=23   ← 23 = 该函数导出索引
EX_InstanceVariable path=[chatWindow]             resolvedOwner=1    ← 1  = 本类导出索引
```

| 用途 | 方法 | ResolvedOwner |
|---|---|---|
| 调用当语句（丢弃返回值） | `InsertCall(fn, ref)` | — |
| 调用当**值**（当参数/赋给变量） | `CreateCallArg(ref)` | — |
| 结果存进临时变量 | `AssignCallResult(fn, ref, "CallFunc_X_ReturnValue")` | 函数导出 |
| 自己取/造变量指针 | `EnsureVariable(fn, name, instance)` | 局部=函数导出，成员=类导出 |

**反编译侧是「保留」而非「生成」**：字节码里已有的指针原样存在 `PointerArg` 里，回编译发回同一个指针 → 逐字节一致。

## 八、编译：节点 → 字节

```
节点列表
  │  EmitNode / ToExpr
  ▼
KismetExpression[]（EX_*）
  │  ① 补丁：EX_Jump / EX_JumpIfNot / EX_PushExecutionFlow 的目标按**字节偏移**回填
  │     （需要 Measure = e.GetSize(asset)，因为 FName 宽度依赖资产）
  ▼
ExpressionSerializer.WriteExpression 逐条写出 → byte[]
```

要点：

- 跳转是**字节偏移**，不是下标；先按占位生成，再累加长度回填
- `EX_Context.Offset` = 内层表达式的序列化长度（用 `Measure` 现算）
- `KismetPropertyPointer` 必须用 `New`（`FFieldPath`）形态；新建无返回值调用时用**空 FFieldPath**（0 段路径 + owner 0）

## 九、写回与校验

```csharp
ws.InsertCall(target, callee);              // 反编译 → 改 → 编译 → 赋值 ScriptBytecode
var saved = ws.Save(workDir);               // uasset + uexp **两半**，缺一不可
var reopened = KismetWorkspace.Open(saved.UAsset, saved.UExp, usmap, dir2);
reopened.Decompile(target);                 // 能读回 = 结构自洽
```

`Edit/InsertCall` 走的是 `Decompile → transform → Generate`：反编译那一步已经过**字节比对**，
所以插入前的基线与原资产一致。

## 十、边界（重要）

| 项 | 状态 |
|---|---|
| 新增调用的**字节可读回** | 已验证：Cards 1,737/1,737 资产插入后重开成功 |
| 新增调用的**UE 运行时行为** | **未验证**（空 `RValuePointer`、`ContextKind=Normal`、`InnerShape=Final` 是"最可能形态"） |
| 蓝图资产定义的结构（`S_*`） | usmap 里没有，成员类型解析不出来 |
| `EX_CallMath` 形态 | 未推断：纯函数也一律按 `FStatic → Final`，可能与 UE 的 `CallMath` 不同 |
| 占位参数 | text/struct/array/map/set 需要真实载荷，必须由调用方替换 |

## 十一、完整示例

```csharp
var ws = KismetWorkspace.Open(uasset, uexp, usmap, workDir);
ws.AttachCatalog(FunctionCatalog.Load(File.ReadAllText("functions.json")));
ws.AttachObjects(ObjectCatalog.Load(File.ReadAllText("objects.json")));
ws.AttachUsmap(UsmapCatalog.Load(usmapPath));

var callee = ws.ResolveFunction("Greater_IntInt", "KismetMathLibrary");
ws.InsertCall("Completed_142D2C9D48C8AD6C227A309EC236561B", callee!);
ws.AssignCallResult("Completed_142D...", callee!, "CallFunc_Greater_IntInt_ReturnValue");
var saved = ws.Save(workDir);
```

实测输出（`BP_Chat`）：

```
引用: /Script/Engine.KismetMathLibrary.Greater_IntInt  idx=-124 exported=False
插入后表达式 4 -> 5，写回 uasset 16989 / uexp 20513 字节
读回节点：
  SetPersistent K2Node_CustomEvent_Result
  LocalFinal ExecuteUbergraph_BP_Chat
  Greater_IntInt
  Set CallFunc_Greater_IntInt_ReturnValue
  Return
```
