---
name: blueprint-read-modify
description: 'Use when reading or editing Unreal Engine blueprint bytecode with the Solstice.Kismet toolchain in D:\files\projects\out-repos\solstice-kismet — listing a function, decompiling it to readable nodes, inserting a call, or rebaking an ubergraph. Covers the MCP tools, the CLI tools, the three data sources (asset, usmap, jmap), the byte-fidelity contract, and the traps that silently corrupt edits.'
---

# 读 / 改蓝图字节码

本技能针对 `D:\files\projects\out-repos\solstice-kismet`（Solstice.Kismet 库）。
核心契约：**反编译 → 编译 → 序列化必须与原字节逐字节一致**；任何"看着对"的改动都要用字节比对证明。

## 什么时候用

- 要读某个蓝图函数到底做了什么（反编译成节点）
- 要往某个函数里插一次调用 / 把调用结果存进变量
- 要拿 ubergraph 的入口偏移表
- 要确认某个函数的往返是否仍然保真

## 先确认三条路径

| 用途 | 路径 |
|---|---|
| 资产 | `D:\devenv\fmodel\Output\Exports\kards\Content\Blueprints\...`（`.uasset` + 同名 `.uexp`，缺一不可） |
| usmap | `D:\files\projects\kards\Content\Paks\kards-5.6.1-0+++UE5+Release-5.6-Fork-2352d15b.usmap` |
| jmap | `D:\files\projects\jmap-out\functions.json`（含引擎类）／`functions.kards.json`（只有游戏侧）／`objects.json` |

> 插引擎函数（`PrintString`、`Greater_IntInt`）**必须**用 `functions.json`；
> `functions.kards.json` 里没有引擎类，会报"没有签名"。

## 读：三步

### 1. 列函数

```powershell
# CLI（探针）
dotnet tools/KismetProbe/bin/Debug/net10.0/KismetProbe.dll <asset.uasset> <usmap>

# MCP
{"name":"kismet_list_functions","arguments":{"asset":"...","usmap":"..."}}
```

### 2. 反编译

```powershell
dotnet tools/KismetProbe/bin/Debug/net10.0/KismetProbe.dll <asset> <usmap> "<函数名>"
```

输出里的 `=== 反编译 ===` 段是节点树。**结果一定是三种表示之一**：

| 表示 | 含义 |
|---|---|
| 树形（`If` / `Loop` / `Set` / `Call`…） | 正常结构化 |
| `UberGraph N 段` | ubergraph 展开成「调度头 + 入口段 + 尾声」，段名是事件名 |
| `BlockGraph N 块` | 树还原不了（共享尾部/状态机），用显式 goto 表示 |

后两种**不是失败**——字节仍然一致，只是不呈树形。

### 3. 看关键事实

- 结构对比段：`逐字节一致: True` 才说明这次反编译是保真的
- 变量指针：`--vars <函数>`（工具 `tools/KismetCoverage`）打印 `path=[名字] resolvedOwner=N`；
  局部变量 `resolvedOwner` = 函数导出，成员变量 = 本类导出
- 结构体成员类型：`--vars` 同时打印，类型来自 usmap

## 改：五步（顺序不能乱）

### 1. 解析成引用（不要只给名字）

```powershell
dotnet tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll <asset> <usmap> `
  --imports <函数名> --class <类名或全路径> --catalog <functions.json>
```

输出里的 `引用:` 行是权威结果（`名字 + 归属 + 包索引`）。

**只给名字会歧义**：同名函数可挂在不同类下，工具会报错并列出候选。此时必须用 `--class` 指定。

### 2. 看引脚（哪些参数是占位）

```powershell
dotnet tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll x <usmap> --plan <函数名> --catalog <functions.json>
```

`??` 标记的是**占位参数**（text / struct / array / map / set 无法忠实合成），
插入前必须换成真实值，否则字节合法但语义不全。

### 3. 插入

```powershell
# 末尾插一次调用（参数按签名自动生成）
dotnet tools/KismetCoverage/bin/Debug/net10.0/KismetCoverage.dll <资产目录> <usmap> `
  --insert <被调函数> --catalog <functions.json> [--max N]
```

这一步会走完整链路：`Decompile → CreateCall → Generate → Save → 重新打开 → Decompile 校验`，
输出 `资产 N，成功 N，失败 N`。**失败为 0 才算过**。

需要"结果存进临时变量"时用库 API（`ws.InsertCallWithResult`），
变量名按 UE 约定 `CallFunc_<函数>_<引脚名>`，重复从 `_1` 起。

### 4. 写回

写盘必须**显式**：`ws.Save(dir)` 产出 `.uasset` + `.uexp` 两半，缺一不可。
MCP 下是 `kismet_save`；不调用就不会落盘（刻意设计，避免 agent 悄悄写坏资产）。

### 5. 验证

- 重开：`KismetWorkspace.Open(saved.UAsset, saved.UExp, usmap, dir)` 能成功
- 反编译：新调用出现在节点里
- 全量回归：`tools/KismetCoverage`（Logic 1,328 / Cards 6,787 全绿是当前基线）

## 硬规则（违反就会静默出错）

1. **不猜调用形态**。形态由 jmap `flags`（`FUNC_Static`）+ 是否本资产导出决定；没有签名表就报错，不要 fallback 成 `EX_FinalFunction`。
2. **不按名字查 Import**。节点带 `FunctionRef`（含索引）；只有索引为 0 的手写节点才按名字解析，且必须唯一。
3. **`EX_Context.RValuePointer` 不能省**。新建 Context 调用用空 `FFieldPath`（0 段路径 + owner 0），用 `Old` 形态会 NRE。
4. **`KismetPropertyPointer` 必须 `New`（`FFieldPath`）形态**，UE5.6 走 `ShouldBeSerializeNew` 分支。
5. **`EX_Context.Offset`** = 内层表达式的序列化长度（用 `GetSize` 现算），不是 vtable 偏移。
6. **跳转是字节偏移**，不是数组下标；切片递归时必须传 `baseOffset`。
7. **临时变量名**：`CallFunc_<函数>_<返回引脚名>`，重复 `_1`、`_2`…（UE 实测约定）。
8. **工作目录必须可写**：`KismetWorkspace.Open` 要把字节落到磁盘再按路径构造资产。用当前目录下的 `.kismet-mcp/` 之类，别用 `%TEMP%`（某些环境对子进程拒绝写入）。

## 失败排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `没有 X 的签名` | catalog 没载入，或用了 `functions.kards.json` 查引擎函数 | 载 `functions.json` |
| `X 在 Import 表里有 N 个归属` | 同名函数跨类 | 用 `--class` / `ResolveFunction(name, owner)` |
| `结构化还原结果与原始字节不符` | 结构还原失败 → 自动退扁平 | 属正常退化，字节仍一致；别当成 bug |
| `表达式 XxxConst 暂不支持` | 遇到未覆盖指令（嵌套位置会抛） | 记下来，补 `ToArg`/`ToExpr` 分支 |
| `字节不一致 @N 位于第 k 条` | 某个数值字段（偏移/指针）不对 | 用探针的 `✗ [路径.字段]` 定位 |
| `Cannot add an empty FString to the name map` | 用空串建 `FName` | 用 `"None"` |

## 边界（别过度承诺）

- **字节可读回**已验证（1,737/1,737 资产插入后重开成功）；**UE 运行时行为未验证**
- `StructProperty` 的返回类型在 jmap 里 `class = null`：只知道是结构体 + 字节大小，不知道具体结构
- 蓝图资产定义的结构（`S_*`）**不在 usmap** 里，成员查不到
- `EX_CallMath` 形态未推断：纯函数也按 `FStatic → FinalFunction`

## 参考

- `docs/adding-a-call.md` — 新增调用的完整数据流向
- `docs/bytecode.md` — 100 个指令的操作数/作用/场景
- `docs/ubergraph.md` — ubergraph 展开与重烘
- `docs/mcp.md` — MCP 工具表
