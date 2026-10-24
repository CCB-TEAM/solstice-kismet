# MCP 服务端

把 `Solstice.Kismet` 的能力暴露成 [MCP](https://modelcontextprotocol.io) 工具，让 agent 能直接
「读函数 / 解析调用 / 插入调用 / 重烘 ubergraph / 查结构体」。

## 启动

```powershell
dotnet src/Solstice.Kismet.Mcp/bin/Release/net10.0/Solstice.Kismet.Mcp.dll
```

stdio + JSON-RPC 2.0（`protocolVersion = 2024-11-05`）。MCP 客户端配置示例：

```json
{
  "mcpServers": {
    "solstice-kismet": {
      "command": "dotnet",
      "args": ["D:\\files\\projects\\out-repos\\solstice-kismet\\src\\Solstice.Kismet.Mcp\\bin\\Release\\net10.0\\Solstice.Kismet.Mcp.dll"]
    }
  }
}
```

自检（CI 里跑，不需要资产）：

```powershell
dotnet src/Solstice.Kismet.Mcp/bin/Release/net10.0/Solstice.Kismet.Mcp.dll --selftest [--usmap <path>]
```

## 工具

| 工具 | 参数 | 作用 |
|---|---|---|
| `kismet_list_functions` | `asset`, `usmap` | 列出带字节码的函数（名字 + 表达式条数） |
| `kismet_decompile` | `asset`, `usmap`, `function` | 反编译成节点树，**每个节点带路径 `#0.2`**，调用节点带参数；退化时会明说「已退回扁平」 |
| `kismet_resolve_function` | `asset`, `usmap`, `name`, `owner?`, `catalog?` | 解析成引用（名字 + 归属 + 包索引）；同名歧义会报错列候选 |
| `kismet_plan_call` | `asset`, `usmap`, `catalog`, `name`, `owner?` | 调用的形态与引脚，标出哪些参数只是占位 |
| `kismet_insert_call` | `asset`, `usmap`, `catalog`, `function`, `callee`, `owner?`, `before?`, `withResult?`, `resultStruct?`, `args?` | 插入一次调用：默认末尾、`before=<路径>` 插到指定节点前；`args` 给具体参数值（不给就按签名补占位）；`withResult` 把结果存进临时变量并**声明**它（结构体返回值用 `resultStruct=<结构名>` 指定类型） |
| `kismet_replace_call` | `asset`, `usmap`, `catalog`, `function`, `path`, `callee`, `owner?`, `args?` | 把某个节点上已有的调用换成另一次调用（保留原形态与上下文） |
| `kismet_delete_node` | `asset`, `usmap`, `function`, `path` | 删掉路径指向的节点（`Return` 不允许删） |
| `kismet_bake_ubergraph` | `asset`, `usmap`, `function` | 重烘 ubergraph，报「段起始偏移」+「调用点入口表」两份（两者本就可以不等） |
| `kismet_struct_members` | `usmap`, `struct` | 查结构体成员与类型（来自 usmap） |
| `kismet_verify` | `asset`, `usmap` | 逐函数校验：能否结构化还原（= 回编译后逐字节一致），列出退回扁平的函数 |
| `kismet_reload` | `asset` | 丢弃该资产的内存状态（未写盘的修改全部丢失） |
| `kismet_save` | `asset`, `usmap`, `outDir`, `fileName?` | 把内存里的修改写回磁盘（uasset + uexp 两半）；写盘前校验 ubergraph 入口点，错位就拒绝 |

### 参数值（`args`）

```json
"args": [
  {"param": "InString",       "kind": "string", "value": "hello"},
  {"param": "bPrintToScreen", "kind": "bool",   "value": true},
  {"param": "Duration",       "kind": "float",  "value": 2.5},
  {"kind": "variable", "value": "bCanChange"},
  {"kind": "name",     "value": "Home"},
  {"kind": "call",     "function": "GetGameTimeInSeconds"}
]
```

- `param` 省略时按位置填第一个还没赋值的参数；给了不存在的参数名会列出可选参数。
- **类型不匹配直接报错**（不会凑合编码）：`int / long / float / double / bool / byte / string / name / variable / none / self / call`。
- text / struct / array / map / set 仍只支持占位（需要真实载荷）。
- 被调函数**没有 jmap 签名**时（本资产自己的蓝图函数），必须用 `args` 显式给出全部参数——不猜。

### 节点路径（`path` / `before`）

`kismet_decompile` 的每一行都带路径：`#0`、`#5.1`、`#0.13.2`。
复合节点按展开顺序编号（`If` 是 Then 接着 Else，`LoopDoWhile` 是 Body 接着 Tail），
改完再反编译路径会变——按最新一次输出取。


## 状态与安全

- **服务端持有状态**：打开过的资产按路径缓存，编辑在内存里累积
- **写盘必须显式**：所有修改都要调 `kismet_save` 才落盘——避免 agent 悄悄写坏资产
- **ubergraph 入口点**：入口偏移烘在调用点（`LocalFinal ExecuteUbergraph_X(<IntConst>)`）里，
  改完必须仍指向同一条指令。`KismetWorkspace` 在编辑后校验、不符就回滚这次编辑，
  `Save` 前再校验一次、不符就拒绝写盘。往段尾追加是安全的；改段内布局会被拦下。
- 工作目录：当前目录下的 `.kismet-mcp/`（UAssetAPI 只能按路径构造资产，需要可写目录）

## 数据源

| 参数 | 文件 | 用途 |
|---|---|---|
| `usmap` | `D:\files\projects\kards\Content\Paks\kards-5.6.1-...usmap` | 结构体 schema（成员类型） |
| `catalog` | `D:\files\projects\jmap-out\functions.json` | 函数签名（形态、参数、返回类型） |

`catalog` 同目录下的 `objects.json` 会被自动载入，用于把「类短名」解析成全路径。

## 边界

- 新增调用的**字节可读回**已验证（1,737/1,737 资产），**UE 运行时行为未验证**
- `StructProperty` 的具体结构名在 jmap 里是 `null`，只能给到字节大小
- 资产定义的结构（`S_*`）不在 usmap 里，查不到成员
- **临时变量的声明**：按类型**直接构造** `FProperty`（基础类型用 UAssetAPI 对应子类 +
  实测 `ElementSize`；对象类用签名里的 class 补类导入填 `PropertyClass`；枚举补枚举导入填
  `Enum` + `UnderlyingType`），公共字段是实测值（`RF_Public` + `CPF_None` + `ArrayDim=TArray`）。
  结构体 / 容器 / 委托的类型 jmap 里没有（`class = null`），走两条兜底：
  ① 资产里**已经调用过同一个函数**时，复用那条现成的 `CallFunc_<函数>_*` 属性（`Struct`/`Inner` 准确）；
  ② 调用方显式给 `resultStruct=<结构名或全路径>`，名字用 usmap 校验（`kismet_struct_members` 可查），
  导入按 `/Script/…` → `ScriptStruct`、`/Game/…` → `UserDefinedStruct` 补。
  两条都不成立才不声明，并把原因写清楚
- 节点路径按「当前反编译结果」编号，**不跨编辑稳定**；批量改动请一次读完再改
