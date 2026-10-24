using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solstice.Kismet;

namespace Solstice.Kismet.Mcp;

/// <summary>
/// Kismet 的 MCP 服务端（stdio + JSON-RPC 2.0）。
///
/// <para>
/// 目的：把 <see cref="KismetWorkspace"/> 的能力暴露成 MCP 工具，让 agent 能
/// 「读函数 / 解析调用 / 插入调用 / 重烘 ubergraph / 查结构体」。
/// </para>
///
/// <para>
/// 设计约束：
/// </para>
/// <list type="bullet">
/// <item>服务端**持有状态**：打开过的资产按路径缓存，编辑在内存里累积，直到显式 <c>kismet_save</c>。</item>
/// <item>所有会改字节的操作都必须显式保存——避免 agent 悄悄写坏资产。</item>
/// <item>工具返回文本，便于模型直接读；结构化信息用 JSON 字符串放进文本里。</item>
/// </list>
/// </summary>
public static class Program
{
    private const string ProtocolVersion = "2024-11-05";

    // 不转义非 ASCII：中文工具描述与返回内容直接可读
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args)
    {
        // stdio 上的 JSON-RPC 按 UTF-8 走。不显式设置的话，.NET 会沿用**继承来的控制台代码页**
        // （中文 Windows 是 936），服务端就把中文按 GBK 写出去，客户端按 UTF-8 读回来全是
        // U+FFFD ——工具描述与返回内容里的中文会整体变成乱码。
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = utf8;
        Console.InputEncoding = utf8;

        if (args.Contains("--selftest"))
        {
            return SelfTest(args);
        }

        // stdio：逐行读 JSON-RPC，逐行写 JSON-RPC
        var stdin = Console.In;
        var stdout = Console.Out;

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? response;
            try
            {
                response = Handle(line);
            }
            catch (Exception ex)
            {
                response = Error(null, -32603, $"{ex.GetType().Name}: {ex.Message}");
            }

            if (response is not null)
            {
                stdout.WriteLine(response.ToJsonString(Json));
                stdout.Flush();
            }
        }

        // stdin 关闭即客户端断开：显式退出，避免残留进程占住 dll（会让下一次 build 失败，
        // 也会在 MCP 客户端反复重连时堆积）。
        stdout.Flush();
        Environment.Exit(0);
        return 0;
    }

    // ---------------- JSON-RPC ----------------

    private static JsonNode? Handle(string line)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, "JSON 解析失败: " + ex.Message);
        }

        if (request is not JsonObject obj)
        {
            return Error(null, -32600, "请求必须是 JSON 对象");
        }

        var id = obj["id"]?.DeepClone();
        var method = obj["method"]?.GetValue<string>();
        var hasId = obj.ContainsKey("id");

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "solstice-kismet",
                        ["version"] = "0.1.0"
                    }
                });

            case "notifications/initialized":
            case "notifications/cancelled":
                return null;   // 通知不需要回包

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = ToolDefinitions() });

            case "tools/call":
                return CallTool(id, obj["params"] as JsonObject);

            default:
                return hasId ? Error(id, -32601, $"未知方法: {method}") : null;
        }
    }

    private static JsonNode Result(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonNode Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static JsonNode CallTool(JsonNode? id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? "";
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var text = Tools.Invoke(name, arguments);
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false
            });
        }
        catch (Exception ex)
        {
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"{ex.GetType().Name}: {ex.Message}"
                }),
                ["isError"] = true
            });
        }
    }

    private static JsonArray ToolDefinitions()
    {
        static JsonObject Prop(string type, string desc) => new()
        {
            ["type"] = type,
            ["description"] = desc
        };

        static JsonObject Schema(params (string Name, string Type, string Desc, bool Required)[] props)
        {
            var properties = new JsonObject();
            var required = new JsonArray();

            foreach (var (propName, type, desc, isRequired) in props)
            {
                properties[propName] = Prop(type, desc);

                if (isRequired)
                {
                    required.Add(propName);
                }
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required
            };
        }

        var tools = new JsonArray();

        void Add(string name, string description, JsonObject schema)
            => tools.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema
            });

        Add("kismet_list_functions", "列出资产里带字节码的函数（名字 + 表达式条数）",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true)));

        Add("kismet_decompile", "反编译一个函数，返回节点树（树形 / ubergraph / 基本块图之一，已字节校验）",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true),
                   ("function", "string", "函数名", true)));

        Add("kismet_resolve_function", "把函数名解析成引用（名字 + 归属类 + 包索引）；同名歧义会报错并列候选",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true),
                   ("name", "string", "函数短名", true),
                   ("owner", "string", "归属类全路径或短名（可选）", false),
                   ("catalog", "string", "jmap functions.json 路径（可选）", false)));

        Add("kismet_plan_call", "给出一次调用的形态与引脚（含哪些参数只是占位）",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true),
                   ("catalog", "string", "jmap functions.json 路径", true),
                   ("name", "string", "函数短名", true),
                   ("owner", "string", "归属类全路径或短名（可选）", false)));

        Add("kismet_insert_call", "插入一次调用：可在末尾、也可在指定节点之前；args 可给具体参数值（不给就按签名补占位）；withResult=true 时结果存进自动命名的临时变量",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["asset"] = Prop("string", "uasset 路径"),
                    ["usmap"] = Prop("string", "usmap 路径"),
                    ["catalog"] = Prop("string", "jmap functions.json 路径"),
                    ["function"] = Prop("string", "被修改的函数名"),
                    ["callee"] = Prop("string", "被调用的函数短名"),
                    ["owner"] = Prop("string", "被调函数归属类（可选）"),
                    ["before"] = Prop("string", "插到这个节点路径之前（可选，默认函数末尾；路径见 kismet_decompile 的 #0.2 标注）"),
                    ["withResult"] = Prop("boolean", "true = 结果存进 CallFunc_<函数>_<引脚> 临时变量"),
                    ["resultStruct"] = Prop("string", "withResult 时临时变量的结构名（jmap 里结构体返回没有类型名；可用 kismet_struct_members 查 usmap）"),
                    ["args"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "参数值。每项 {\"param\":\"参数名\",\"kind\":\"int|long|float|double|bool|byte|string|name|variable|none|self|call\",\"value\":...}；param 省略时按位置填；kind=call 时用 function/owner 指定被调函数",
                        ["items"] = new JsonObject { ["type"] = "object" }
                    }
                },
                ["required"] = new JsonArray("asset", "usmap", "catalog", "function", "callee")
            });

        Add("kismet_delete_node", "按节点路径删掉一个节点（路径见 kismet_decompile 的 #0.2 标注）",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true),
                   ("function", "string", "函数名", true),
                   ("path", "string", "节点路径，如 0 或 0.2.1", true)));

        Add("kismet_replace_call", "把某个节点上已有的调用换成另一次调用（保留它原来的形态与上下文）",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["asset"] = Prop("string", "uasset 路径"),
                    ["usmap"] = Prop("string", "usmap 路径"),
                    ["catalog"] = Prop("string", "jmap functions.json 路径"),
                    ["function"] = Prop("string", "函数名"),
                    ["path"] = Prop("string", "被替换的调用节点路径"),
                    ["callee"] = Prop("string", "新的被调函数短名"),
                    ["owner"] = Prop("string", "新被调函数归属类（可选）"),
                    ["args"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "新参数值，格式同 kismet_insert_call",
                        ["items"] = new JsonObject { ["type"] = "object" }
                    }
                },
                ["required"] = new JsonArray("asset", "usmap", "catalog", "function", "path", "callee")
            });

        Add("kismet_verify", "逐函数校验：能否结构化还原（= 回编译后逐字节一致），列出退回扁平的函数",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true)));

        Add("kismet_reload", "丢弃某个资产的内存状态（未写盘的修改全部丢失，下次调用重新从磁盘打开）",
            Schema(("asset", "string", "uasset 路径", true)));

        Add("kismet_bake_ubergraph", "重新烘焙 ubergraph 并返回入口偏移表（要写回 UClass 的那张表）",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true),
                   ("function", "string", "ExecuteUbergraph_* 函数名", true)));

        Add("kismet_struct_members", "查 usmap 里某个结构的成员与类型",
            Schema(("usmap", "string", "usmap 路径", true),
                   ("struct", "string", "结构名（短名或全路径）", true)));

        Add("kismet_save", "把当前内存中的修改写回磁盘（uasset + uexp 两半），返回文件路径",
            Schema(("asset", "string", "uasset 路径", true),
                   ("usmap", "string", "usmap 路径", true),
                   ("outDir", "string", "输出目录", true),
                   ("fileName", "string", "输出文件名（默认 out.uasset）", false)));

        return tools;
    }

    // ---------------- 自检（CI 用，不需要资产） ----------------

    private static int SelfTest(string[] args)
    {
        var failures = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  " + detail)}");
            if (!ok)
            {
                failures++;
            }
        }

        var init = Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        Check("initialize 返回 protocolVersion",
            init?["result"]?["protocolVersion"]?.GetValue<string>() == ProtocolVersion);

        var list = Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = list?["result"]?["tools"] as JsonArray;
        Check("tools/list 有工具", tools is { Count: >= 8 }, $"{tools?.Count} 个");

        var names = tools?.Select(t => t?["name"]?.GetValue<string>()).ToArray() ?? [];
        Check("包含 kismet_insert_call", names.Contains("kismet_insert_call"), string.Join(",", names));

        foreach (var tool in tools ?? [])
        {
            var schema = tool?["inputSchema"];
            Check($"schema 合法: {tool?["name"]?.GetValue<string>()}",
                schema?["type"]?.GetValue<string>() == "object" && schema?["properties"] is JsonObject);
        }

        var unknown = Handle("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"nope","arguments":{}}}""");
        Check("未知工具返回 isError", unknown?["result"]?["isError"]?.GetValue<bool>() == true);

        // 给了 usmap 就顺手验证一个真实工具
        var usmapIndex = Array.IndexOf(args, "--usmap");
        if (usmapIndex >= 0 && usmapIndex + 1 < args.Length)
        {
            var usmap = args[usmapIndex + 1];
            var call = Handle(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new { name = "kismet_struct_members", arguments = new { usmap, @struct = "Vector" } }
            }));
            var text = call?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            Check("kismet_struct_members(Vector) 能返回成员", text.Contains("DoubleProperty"), text.Split('\n').FirstOrDefault());
        }

        Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }
}
