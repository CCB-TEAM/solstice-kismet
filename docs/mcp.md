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
| `kismet_decompile` | `asset`, `usmap`, `function` | 反编译成节点树（树形 / ubergraph / 基本块图，已字节校验） |
| `kismet_resolve_function` | `asset`, `usmap`, `name`, `owner?`, `catalog?` | 解析成引用（名字 + 归属 + 包索引）；同名歧义会报错列候选 |
| `kismet_plan_call` | `asset`, `usmap`, `catalog`, `name`, `owner?` | 调用的形态与引脚，标出哪些参数只是占位 |
| `kismet_insert_call` | `asset`, `usmap`, `catalog`, `function`, `callee`, `owner?`, `withResult?` | 末尾插入一次调用；`withResult=true` 时结果存进自动命名的临时变量 |
| `kismet_bake_ubergraph` | `asset`, `usmap`, `function` | 重烘 ubergraph，返回入口偏移表 |
| `kismet_struct_members` | `usmap`, `struct` | 查结构体成员与类型（来自 usmap） |
| `kismet_save` | `asset`, `usmap`, `outDir`, `fileName?` | 把内存里的修改写回磁盘（uasset + uexp 两半） |

## 状态与安全

- **服务端持有状态**：打开过的资产按路径缓存，编辑在内存里累积
- **写盘必须显式**：所有修改都要调 `kismet_save` 才落盘——避免 agent 悄悄写坏资产
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
