# Kismet 字节码指令参考

> 口径：token 值取自 UAssetAPI 的 `EExprToken`（对应 UE 5.6）；操作数按各 `EX_*.Read()`
> 的读取顺序列出；「本库」列指 `src/Solstice.Kismet` 当前（`7d9b8a6`）的处理方式。
> 实测数据来自 `Blueprints/Logic` 下 6 个资产、131 个函数的**顶层**指令序列。

## 怎么读这张表

- 一条指令 = **1 字节 token** + 操作数。操作数里可以再嵌表达式，**没有长度前缀**，
  读完一条自然就知道下一条从哪开始。
- 调用参数以 `EX_EndFunctionParms`(0x16) 结束；容器常量以对应的 `EX_End*` 结束。
- 跳转操作数是**字节偏移，相对整个函数**，不是数组下标。
- 「本库」三态：

| 标记 | 含义 |
|---|---|
| **结构化** | 有专门的节点/参数类型，反编译可读、可改、可重建 |
| **Raw** | 走 `RawNode` / `RawArg` 原样保留：字节一致，但不可读、不可编辑 |
| **—** | 本库没有专门处理。出现在顶层会退化成 Raw（字节安全）；出现在**嵌套位置会抛异常**（这是刻意的显式失败） |

---

## 一、常量与字面量

| token | 指令 | 操作数（按序） | 作用 | 典型生成场景 | 本库 |
|---|---|---|---|---|---|
| `0x25` | `EX_IntZero` | 无 | 整数 0 | 编译器特化，省 4 字节 | 结构化 |
| `0x26` | `EX_IntOne` | 无 | 整数 1 | 同上 | 结构化 |
| `0x27` | `EX_True` | 无 | 布尔真 | 布尔字面量 / `Set X = true` | 结构化 |
| `0x28` | `EX_False` | 无 | 布尔假 | 同上 | 结构化 |
| `0x1D` | `EX_IntConst` | int32 | 32 位整数 | 任意整型字面量 | 结构化 |
| `0x2C` | `EX_IntConstByte` | uint8 | 0..255 的整数 | 小整数特化 | — |
| `0x24` | `EX_ByteConst` | uint8 | 字节值 | `byte` 类型字面量 | 结构化 |
| `0x35` | `EX_Int64Const` | int64 | 64 位整数 | `int64` 字面量 | 结构化 |
| `0x36` | `EX_UInt64Const` | uint64 | 64 位无符号 | `uint64` 字面量 | — |
| `0x1E` | `EX_FloatConst` | float32 | 单精度 | `float` 字面量 | 结构化 |
| `0x37` | `EX_DoubleConst` | float64 | 双精度 | `double` 字面量 | 结构化 |
| `0x1F` | `EX_StringConst` | ANSI 零结尾串 | ANSI 字符串 | `FString` 字面量 | 结构化 |
| `0x34` | `EX_UnicodeStringConst` | UTF-16 零结尾串 | 宽字符串 | 含非 ASCII 的字面量 | 结构化 |
| `0x21` | `EX_NameConst` | FName（int32 索引 + int32 编号） | 名字常量 | `FName` 字面量、`SwitchName` 的 case 值 | 结构化 |
| `0x29` | `EX_TextConst` | FScriptText（byte 类型 + 变体载荷） | 本地化文本 | `FText` 字面量 / `NSLOCTEXT` | 结构化 |
| `0x20` | `EX_ObjectConst` | FPackageIndex int32 | 对象引用 | 硬引用资源、类引用 | 结构化 |
| `0x2A` | `EX_NoObject` | 无 | 空对象 | `nullptr` 对象 / `Cast` 失败分支 | 结构化 |
| `0x2D` | `EX_NoInterface` | 无 | 空接口 | 接口变量未设置 | — |
| `0x67` | `EX_SoftObjectConst` | 内嵌表达式 | 软对象引用 | `SoftObjectPath` / 软引用字面量 | 结构化 |
| `0x23` | `EX_VectorConst` | 3×float（LWC 下 3×double） | 向量 | `MakeVector` / 向量字面量 | 结构化 |
| `0x41` | `EX_Vector3fConst` | 3×float（固定单精度） | 单精度向量 | UE5 LWC 之后的小向量特化 | — |
| `0x22` | `EX_RotationConst` | 3×float（LWC 下 3×double） | 旋转 | `MakeRotator` | 结构化 |
| `0x2B` | `EX_TransformConst` | FTransform | 变换 | `MakeTransform` | 结构化 |
| `0x17` | `EX_Self` | 无 | `this` | 成员访问 / 自身调用 | 结构化 |
| `0x5B` | `EX_SkipOffsetConst` | uint32 | 跳过量常量 | 配合 `EX_Skip` | 结构化 |
| `0x33` | `EX_PropertyConst` | 指针 | `FProperty*` 常量 | 反射类节点（`GetProperty`） | — |
| `0x6D` | `EX_FieldPathConst` | 内嵌表达式 | `FFieldPath` 常量 | 字段路径节点 | — |

## 二、变量

变量类指令都只带一个 **FKismetPropertyPointer**（`FFieldPath`：`FName[]` 路径 +
`ResolvedOwner` 的 FPackageIndex），长度可变（取决于路径段数）。

| token | 指令 | 作用 | 典型生成场景 | 本库 |
|---|---|---|---|---|
| `0x00` | `EX_LocalVariable` | 读函数局部变量 | 函数内的临时变量存储、循环计数器 | 结构化 |
| `0x01` | `EX_InstanceVariable` | 读本对象成员 | `Set 成员 = ...` 的目标 | 结构化 |
| `0x02` | `EX_DefaultVariable` | 读类默认对象（CDO）上的默认值 | 构造默认值、`GetDefaultObject` | 结构化 |
| `0x48` | `EX_LocalOutVariable` | 读**输出参数**（按引用传入） | 带 out 参数的函数内部写入 | 结构化 |
| `0x6C` | `EX_ClassSparseDataVariable` | 稀疏类数据变量 | UE5 稀疏类数据 | — |

## 三、赋值（Let 家族）

⚠️ 这里有两种**不同形**的指令，写反了字节一定不对：

- `EX_Let` = `指针` + `变量表达式` + `值表达式`（三份信息）
- `EX_LetBase` 子类 = `变量表达式` + `赋值表达式`（两份）

| token | 指令 | 操作数 | 作用 | 典型生成场景 | 本库 |
|---|---|---|---|---|---|
| `0x0F` | `EX_Let` | 指针 + 变量表达式 + 值表达式 | 任意大小类型赋值 | 结构体/数组/`int` 赋值 | 结构化 |
| `0x14` | `EX_LetBool` | 变量表达式 + 赋值表达式 | 布尔赋值 | `Set bFlag = <bool>` | 结构化 |
| `0x5F` | `EX_LetObj` | 变量表达式 + 赋值表达式 | 对象引用赋值 | `Cast` 结果写回、`Set 对象变量` | 结构化 |
| `0x60` | `EX_LetWeakObjPtr` | 变量表达式 + 赋值表达式 | 弱对象指针赋值 | `TWeakObjectPtr` 赋值 | 结构化 |
| `0x44` | `EX_LetDelegate` | 变量表达式 + 赋值表达式 | 单播委托赋值 | `Bind Event` / 委托变量 | 结构化 |
| `0x43` | `EX_LetMulticastDelegate` | 变量表达式 + 赋值表达式 | 多播委托赋值 | 事件分发器变量赋值 | — |
| `0x64` | `EX_LetValueOnPersistentFrame` | 指针 + 赋值表达式 | 写入「持久帧」变量 | 跨帧保持的临时变量（`Delay`/Timeline 里常见） | 结构化 |
| `0x11` | `EX_BitFieldConst` | 指针 + byte | 位域常量赋值 | `bIsA` 这类 bit 属性的常量赋值 | — |

> `EX_Let` 的首个「指针」操作数只在 `VER_UE4_SERIALIZE_BLUEPRINT_EVENTGRAPH_FASTCALLS_IN_UFUNCTION`
> 之后才有；更老的资产是「变量表达式 + 值表达式」两份。UE5.6 的资产一律是三份。

## 四、函数调用

**六种调用形态必须区分**，它们决定字节怎么读，不能从函数签名反推：

| token | 指令 | 操作数 | 语义 | 什么时候用它 | 本库 |
|---|---|---|---|---|---|
| `0x1C` | `EX_FinalFunction` | FPackageIndex + 参数表 | 已知确切目标，不走虚表 | native 函数、prebound 调用 | 结构化 |
| `0x46` | `EX_LocalFinalFunction` | 同 `EX_FinalFunction` | 同上，但目标限定在本资产/local 空间 | 同资产内的 final 脚本函数 | 结构化 |
| `0x68` | `EX_CallMath` | 同 `EX_FinalFunction` | 纯函数调用，**不需要对象上下文** | 纯数学/纯工具函数（`Add_IntInt`、`Concat_StrStr`） | 结构化 |
| `0x1B` | `EX_VirtualFunction` | FName + 参数表 | 按名字动态派发，可被覆盖 | 虚函数、蓝图事件 | 结构化 |
| `0x45` | `EX_LocalVirtualFunction` | FName + 参数表 | 按名字调用，限定本资产 | 调本资产的脚本函数（最常见） | 结构化 |
| `0x63` | `EX_CallMulticastDelegate` | FPackageIndex + 委托表达式 + 参数表 | 调用多播委托 | `Call 事件分发器` | 结构化 |
| `0x16` | `EX_EndFunctionParms` | 无 | 参数表终止符 | 每条调用后面都有 | 结构化（隐式） |
| `0x15` | `EX_EndParmValue` | 无 | 可选参数默认值结束 | 带默认值的可选参数 | — |
| `0x4B` | `EX_InstanceDelegate` | FName | 引用函数/委托实例 | `CreateEvent` 的结果 | 结构化 |

## 五、上下文与类型转换

`EX_Context` 是「在某个对象上做某事」的包装层，**内层可以是任意指令**，
不一定都是函数调用——这一点是反编译的常见坑。

| token | 指令 | 操作数 | 语义 | 什么时候用它 | 本库 |
|---|---|---|---|---|---|
| `0x19` | `EX_Context` | 对象表达式 + uint32 `Offset` + 指针 + 上下文表达式 | 在对象上下文里执行内层 | `对象.函数(...)` / `对象.成员` | 结构化 |
| `0x12` | `EX_ClassContext` | 同 `EX_Context` | 上下文是**类默认对象** | 访问 CDO 上的默认值 | 结构化 |
| `0x1A` | `EX_Context_FailSilent` | 同 `EX_Context` | 上下文为空时**静默跳过** | 只用于无返回值/无输出参数的函数 | — |
| `0x51` | `EX_InterfaceContext` | 值表达式 | 接口变量上下文（包一层） | 通过接口变量调用方法 | 结构化 |
| `0x2E` | `EX_DynamicCast` | FPackageIndex + 目标表达式 | 安全动态转换（产出 `bSuccess`） | `Cast To X` 节点 | 结构化 |
| `0x13` | `EX_MetaCast` | FPackageIndex + 目标 | 元类（类引用）转换 | `Cast To Class` | 结构化 |
| `0x52` | `EX_ObjToInterfaceCast` | FPackageIndex + 目标 | 对象 → 接口 | 把对象赋给接口变量 | 结构化 |
| `0x54` | `EX_CrossInterfaceCast` | FPackageIndex + 目标 | 接口 → 另一接口 | 接口之间转换 | — |
| `0x55` | `EX_InterfaceToObjCast` | FPackageIndex + 目标 | 接口 → 对象 | 接口转回对象 | — |
| `0x38` | `EX_PrimitiveCast` | byte 转换码 + 目标表达式 | 基础类型转换 | `int`→`float`、`bool`→`int` 等隐式转换 | 结构化 |
| `0x42` | `EX_StructMemberContext` | 指针 + 结构表达式 | 访问结构体成员 | `Break Struct` / 成员读取 | 结构化 |

`EX_Context.Offset` 的语义是「对象为空时要跳过多少字节」，等于内层表达式的序列化长度，
**不是** vtable 偏移。重建时要用 `GetSize` 现算。

## 六、控制流与执行流

| token | 指令 | 操作数 | 语义 | 典型生成场景 | 本库 |
|---|---|---|---|---|---|
| `0x04` | `EX_Return` | 返回表达式 | 函数返回 | 每个函数末尾 | 结构化 |
| `0x06` | `EX_Jump` | uint32 偏移 | 无条件跳转 | if 的 then 结束、循环回边 | 结构化 |
| `0x07` | `EX_JumpIfNot` | uint32 偏移 + 条件表达式 | 条件为假则跳 | if/while/for 的退出边 | 结构化 |
| `0x4E` | `EX_ComputedJump` | 目标表达式 | 跳到**算出来的**地址 | 分发器：按整数跳到对应代码块 | 结构化 |
| `0x4C` | `EX_PushExecutionFlow` | uint32 地址 | 把地址压执行流栈，**继续顺序执行** | 异步/延迟节点开始 | 结构化 |
| `0x4D` | `EX_PopExecutionFlow` | 无 | 跳到栈顶地址 | 异步节点恢复 | 结构化 |
| `0x4F` | `EX_PopExecutionFlowIfNot` | 条件表达式 | 条件不成立才跳栈顶 | `Delay` 等待、`IsFaderActive` 轮询 | 结构化 |
| `0x18` | `EX_Skip` | uint32 偏移 + 表达式 | 条件性跳过一段 | 编辑器调试 / 延迟求值 | — |
| `0x69` | `EX_SwitchValue` | uint16 分支数 + uint32 `EndGotoOffset` + 索引表达式 + 每个分支（终值、uint32 `NextOffset`、case 体）+ 默认体 | switch 跳转表 | `Switch on Int` 节点 | Raw |
| `0x0B` | `EX_Nothing` | 无 | 空操作 / 占位 | 编译器的跳转落点占位 | 结构化 |
| `0x0C` | `EX_NothingInt32` | int32 | 带参数的 nop | 反汇编时便于对齐阅读 | — |
| `0x09` | `EX_Assert` | uint16 行号 + bool 调试模式 + 表达式 | 断言 | `Assert` 节点 | — |
| `0x53` | `EX_EndOfScript` | 无 | 脚本结束 | 每个函数最后一条 | 结构化 |

执行流栈（`Push`/`Pop`）是「协程式」的控制转移，和 `Jump` 不是一回事：
`Push` 不改变当前执行位置，只是把「以后回到哪里」记下来。

## 七、容器与结构体

| token | 指令 | 操作数 | 作用 | 本库 |
|---|---|---|---|---|
| `0x2F` | `EX_StructConst` | FPackageIndex + int32 大小 + 元素…`0x30` | 结构体字面量 | 结构化 |
| `0x30` | `EX_EndStructConst` | 无 | 结构体字面量结束 | 结构化 |
| `0x65` | `EX_ArrayConst` | 指针 + int32 数量 + 元素…`0x66` | 数组字面量 | 结构化 |
| `0x66` | `EX_EndArrayConst` | 无 | 数组字面量结束 | 结构化 |
| `0x3D` | `EX_SetConst` | 指针 + int32 + 元素…`0x3E` | Set 字面量 | — |
| `0x3E` | `EX_EndSetConst` | 无 | Set 字面量结束 | — |
| `0x3F` | `EX_MapConst` | 两个指针 + int32 + 元素…`0x40` | Map 字面量 | 结构化 |
| `0x40` | `EX_EndMapConst` | 无 | Map 字面量结束 | 结构化 |
| `0x31` | `EX_SetArray` | 目标表达式 + FPackageIndex + 元素…`0x32` | **给数组赋值** | 结构化 |
| `0x32` | `EX_EndArray` | 无 | 数组赋值结束 | 结构化 |
| `0x39` | `EX_SetSet` | 表达式 + int32 + 元素…`0x3A` | 给 Set 赋值 | — |
| `0x3A` | `EX_EndSet` | 无 | Set 赋值结束 | — |
| `0x3B` | `EX_SetMap` | 表达式 + int32 + 元素…`0x3C` | 给 Map 赋值 | — |
| `0x3C` | `EX_EndMap` | 无 | Map 赋值结束 | — |
| `0x6B` | `EX_ArrayGetByRef` | 数组表达式 + 索引表达式 | 按引用取数组元素 | 结构化 |

`EX_ArrayConst` 是**字面量**（数组本身作为常量），`EX_SetArray` 是**赋值动作**
（把元素写进已有的数组变量）——实测 `EX_SetArray` 在样本里出现 8 次，是最常见的未结构化指令。

## 八、委托

| token | 指令 | 操作数 | 作用 | 本库 |
|---|---|---|---|---|
| `0x61` | `EX_BindDelegate` | FName + 委托表达式 + 对象表达式 | 把「对象 + 函数名」绑成委托 | Raw |
| `0x5C` | `EX_AddMulticastDelegate` | 委托表达式 + 待加表达式 | 追加多播委托目标 | Raw |
| `0x62` | `EX_RemoveMulticastDelegate` | 委托表达式 + 待删表达式 | 移除多播委托目标 | Raw |
| `0x5D` | `EX_ClearMulticastDelegate` | 委托表达式 | 清空多播委托 | — |

## 九、调试与元数据

这些在打包后的资产里通常不出现，或表现为空操作；遇到时**原样保留**最安全。

| token | 指令 | 操作数 | 作用 | 本库 |
|---|---|---|---|---|
| `0x5E` | `EX_Tracepoint` | 无 | 调试跟踪点 | — |
| `0x5A` | `EX_WireTracepoint` | 无 | 蓝图连线断点 | — |
| `0x50` | `EX_Breakpoint` | 无 | 断点 | — |
| `0x6A` | `EX_InstrumentationEvent` | byte 事件类型 + FName | 埋点事件（进入/退出等） | — |
| `0x4A` | `EX_DeprecatedOp4A` | 无 | 历史遗留占位 | — |
| `0x70` | `EX_AutoRtfmTransact` | — | AutoRTFM 事务开始 | — |
| `0x71` | `EX_AutoRtfmStopTransact` | — | 事务中止/中断 | — |
| `0x72` | `EX_AutoRtfmAbortIfNot` | — | 条件为假则中止事务 | — |

`0x70`–`0x72` 只在 `EExprToken` 里有枚举，**UAssetAPI 1.1.0 没有对应实现类**；
目前没有任何资产走到这里，一旦遇到会直接失败而不是静默丢字节。

## 十、最容易混的几组

1. **`FinalFunction` / `LocalFinalFunction` / `CallMath`**
   三者操作数完全一样（FPackageIndex + 参数表），区别只在「目标怎么找」：
   `CallMath` 不需要对象上下文（纯函数）；`LocalFinalFunction` 目标在本资产内；
   `FinalFunction` 是其余情况。字节上必须按原样保留，不能靠签名反推。

2. **`VirtualFunction` / `LocalVirtualFunction`**
   都用 FName 寻址，不带 StackNode。区别同上：local 限定本资产。
   注意 `EX_LocalFinalFunction` 的 StackNode **可能指向 Export（正数）**，
   所以「按名字查 Import 表」会查不到，必须用保留的 StackNode。

3. **`Context` / `ClassContext` / `Context_FailSilent`**
   操作数完全相同，差别是上下文怎么求值：
   普通对象 / 类默认对象 / 允许空上下文且静默跳过。
   `FailSilent` 只用于**没有返回值也没有输出参数**的函数，所以「有没有返回值」
   反过来能帮判断形态。

4. **`Jump` / `ComputedJump` / `Push+PopExecutionFlow`**
   `Jump` 是常量偏移；`ComputedJump` 目标来自表达式（switch 分发）；
   `Push`/`Pop` 是执行流栈，用于异步恢复。三者不能互相替代。

5. **`Let` vs `LetBool` vs `LetObj`**
   `Let` 吃三份信息（指针 + 变量 + 值），`LetBase` 家族只吃两份（变量 + 值）。
   `Let` 用于任意大小类型，`LetBool` 只表示布尔，`LetObj` 表示对象引用。

6. **`EX_Nothing` 的双重身份**
   既表示「真的什么都不做」，也被编译器当作**跳转落点占位**。
   所以结构还原里跳过 `EX_Nothing` 是对的，但**不能丢弃**它们——字节上它们各占 1 字节。

7. **常量特化**
   `IntZero`/`IntOne`/`True`/`False`/`IntConstByte` 都是 `IntConst`/`ByteConst` 的省字节形式。
   重建时若把 `IntZero` 写成 `IntConst(0)`，语义相同但**字节多 4 个**——必须保留原形态。

## 十一、实测频次

6 个样本资产、131 个函数、1883 条**顶层**指令，共出现 22 种：

| 次数 | 指令 | 说明 |
|---:|---|---|
| 486 | `EX_Let` | 赋值主力 |
| 245 | `EX_LetBool` | 布尔赋值 |
| 189 | `EX_Context` | 跨对象调用/取成员 |
| 131 | `EX_Return` | 每函数一条 |
| 131 | `EX_EndOfScript` | 每函数一条 |
| 130 | `EX_JumpIfNot` | 分支 |
| 110 | `EX_LocalVirtualFunction` | 调本资产函数 |
| 96 | `EX_Jump` | 无条件跳转 |
| 94 | `EX_PopExecutionFlow` | 异步恢复 |
| 52 | `EX_CallMath` | 纯函数 |
| 50 | `EX_LetObj` | 对象赋值 |
| 40 | `EX_LocalFinalFunction` | 本地 final 调用 |
| 31 | `EX_PushExecutionFlow` | 异步开始 |
| 24 | `EX_LetValueOnPersistentFrame` | 持久帧变量 |
| 21 | `EX_PopExecutionFlowIfNot` | 条件等待 |
| 16 | `EX_CallMulticastDelegate` | 多播委托调用 |
| 9 | `EX_BindDelegate` | 委托绑定 |
| 8 | `EX_SetArray` | 数组赋值 |
| 7 | `EX_VirtualFunction` | 虚函数调用 |
| 7 | `EX_AddMulticastDelegate` | 委托追加 |
| 5 | `EX_ComputedJump` | 计算跳转 |
| 1 | `EX_RemoveMulticastDelegate` | 委托移除 |

注意：这张表只统计**顶层**。字面量（`IntConst`、`ObjectConst`、`StringConst`…）
绝大多数嵌在 `Let` / 调用的参数里，不出现在顶层——它们没上榜不代表没用到。
想统计含嵌套的全量，需要递归 `ExpressionWalker.Walk`。

## 十二、本库覆盖度汇总

- **结构化（可读可改）**：常量（含 `Int64` / `Transform` / `UnicodeString`）、变量（含
  `EX_DefaultVariable`）、`Let` 家族、六种调用形态 + `EX_InstanceDelegate`、`Context` /
  `ClassContext` / `InterfaceContext`、`DynamicCast` / `MetaCast` / `ObjToInterfaceCast` /
  `PrimitiveCast`、`StructMemberContext`、`StructConst` / `ArrayConst` / `SetArray` / `MapConst`、
  `ArrayGetByRef`、`SoftObjectConst`、`Jump` / `JumpIfNot` / `ComputedJump` / `PushExecutionFlow` /
  `PopExecutionFlow` / `PopExecutionFlowIfNot` / `Return` / `Nothing` / `EndOfScript`
- **Raw 原样保留（字节安全，不可读）**：`EX_SwitchValue`、`EX_BindDelegate`、
  `EX_AddMulticastDelegate` / `EX_RemoveMulticastDelegate`、`EX_Context_FailSilent`（少见），
  以及**跳过片段边界**的 `EX_JumpIfNot`（目标落在更外层代码里，见下）
- **未覆盖**：其余（`SetSet` / `SetMap`、`ClassSparseDataVariable`、`EndParmValue`、`Skip`、
  `Assert`、`IntConstByte`、`PropertyConst`、`FieldPathConst`、
  `Vector3fConst`、调试类、AutoRTFM）

结构还原有一条重要边界：`EX_JumpIfNot` 的落点如果**越出当前片段**（共享尾部 / 状态机），
反编译器会放弃把这一条还原成 `IfNode`，改为原样保留。硬猜写成 if 会让跳转偏移错位，
而这恰恰是字节不一致的主要来源——先例见 HANDOVER 第十节。

未覆盖不等于坏：出现在顶层会退化成 Raw（字节仍然一致）。失败边界更细一点：

- 嵌套在**调用参数**里 → `ToArgs` 直接抛异常（显式失败，一眼看得见）
- 嵌套在**其他位置**（如 `EX_Context.ObjectExpression`）→ 可能退化，但
  `DecompileVerified` 会把回编译结果与原始字节比对，不符就整体退回扁平模式

所以最终产出的字节永远是安全的，代价只是可读性。这条边界正是 **8115/8115** 逐字节一致的原因——
宁可退化，也不猜。
