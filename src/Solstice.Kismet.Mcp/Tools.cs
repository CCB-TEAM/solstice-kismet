using System.Text;
using System.Text.Json.Nodes;
using Solstice.Kismet;

namespace Solstice.Kismet.Mcp;

/// <summary>
/// MCP 工具的实现。资产按路径缓存，编辑在内存里累积，直到 <c>kismet_save</c>。
/// </summary>
internal static class Tools
{
    private static readonly Dictionary<string, KismetWorkspace> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string WorkRoot =
        Path.Combine(Directory.GetCurrentDirectory(), ".kismet-mcp");

    public static string Invoke(string name, JsonObject args) => name switch
    {
        "kismet_list_functions" => ListFunctions(args),
        "kismet_decompile" => Decompile(args),
        "kismet_resolve_function" => ResolveFunction(args),
        "kismet_plan_call" => PlanCall(args),
        "kismet_insert_call" => InsertCall(args),
        "kismet_delete_node" => DeleteNode(args),
        "kismet_replace_call" => ReplaceCall(args),
        "kismet_bake_ubergraph" => BakeUbergraph(args),
        "kismet_struct_members" => StructMembers(args),
        "kismet_verify" => Verify(args),
        "kismet_reload" => Reload(args),
        "kismet_save" => Save(args),
        _ => throw new InvalidOperationException($"未知工具: {name}")
    };

    // ---------------- 工具实现 ----------------

    private static string ListFunctions(JsonObject args)
    {
        var ws = Workspace(args);
        var lines = ws.Functions
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{ws.ExpressionCount(f),6}  {f}");

        return $"{ws.Functions.Count} 个带字节码的函数\n" + string.Join("\n", lines);
    }

    private static string Decompile(JsonObject args)
    {
        var ws = Workspace(args);
        var function = Required(args, "function");
        var result = ws.DecompileDetailed(function);

        var sb = new StringBuilder();
        sb.AppendLine($"{function}: {result.Nodes.Count} 个节点" +
                      (result.Structured ? "（结构化还原，已字节校验）" : "（已退回扁平：结构还原失败，字节仍一致）"));
        for (var i = 0; i < result.Nodes.Count; i++)
        {
            Render(result.Nodes[i], i.ToString(), 0, sb);
        }

        return sb.ToString().TrimEnd();
    }

    private static string ResolveFunction(JsonObject args)
    {
        var ws = Workspace(args);
        var name = Required(args, "name");
        var owner = Optional(args, "owner");

        var reference = ws.ResolveFunction(name, owner)
                        ?? throw new InvalidOperationException($"找不到函数 {name}");

        return $"fullPath   : {reference.FullPath}\n" +
               $"ownerPath  : {reference.OwnerPath}\n" +
               $"index      : {reference.Index.Index}\n" +
               $"isExported : {reference.IsExported}\n" +
               $"hasReturn  : {reference.Signature?.HasReturn.ToString() ?? "?"}";
    }

    private static string PlanCall(JsonObject args)
    {
        var ws = Workspace(args);
        var name = Required(args, "name");
        var owner = Optional(args, "owner");

        var plan = ws.PlanCall(ws.ResolveFunction(name, owner, ensureImport: false));

        var sb = new StringBuilder();
        sb.AppendLine($"shape={plan.Shape} owner={plan.OwnerPath}");
        foreach (var pin in plan.Pins)
        {
            var mark = pin.IsOutput ? "->" : pin.IsPlaceholder ? "??" : "  ";
            sb.AppendLine($"{mark} {pin.Name,-24} {pin.Type,-18} {pin.Dir}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string InsertCall(JsonObject args)
    {
        var ws = Workspace(args);
        var function = Required(args, "function");
        var calleeName = Required(args, "callee");
        var owner = Optional(args, "owner");
        var withResult = args["withResult"]?.GetValue<bool>() ?? false;
        var before = Optional(args, "before");

        var callee = ws.ResolveFunction(calleeName, owner)
                     ?? throw new InvalidOperationException($"找不到函数 {calleeName}");

        // 参数：给到的用给的值，没给的按签名补占位
        var specs = ParseArgSpecs(args);
        var callArgs = ws.BuildArgs(function, callee, specs);
        var placeholders = specs.Count == 0;

        var countBefore = ws.ExpressionCount(function);

        if (withResult)
        {
            var resultStruct = Optional(args, "resultStruct");
            var inserted = ws.InsertCallWithResult(function, callee, resultStruct, callArgs.ToArray());
            var sbResult = new StringBuilder();
            sbResult.AppendLine("已插入（结果存进临时变量）");
            sbResult.AppendLine($"variable   : {inserted.Variable}");
            sbResult.AppendLine($"returnType : {inserted.ReturnType?.Type ?? "?"} size={inserted.ReturnType?.Size}");
            sbResult.AppendLine($"declaration: {inserted.Declaration ?? "已写进函数的属性表"}");
            sbResult.AppendLine($"expressions: {countBefore} -> {ws.ExpressionCount(function)}（尚未写盘，需调用 kismet_save）");
            AppendUbergraphCheck(ws, function, sbResult);
            return sbResult.ToString().TrimEnd();
        }

        if (before is not null)
        {
            ws.InsertCallBefore(function, callee, before, callArgs.ToArray());
        }
        else
        {
            ws.InsertCall(function, callee, callArgs.ToArray());
        }

        var sb = new StringBuilder();
        sb.AppendLine("已插入");
        sb.AppendLine($"callee     : {callee.FullPath}");
        sb.AppendLine($"位置       : {(before is null ? "函数末尾（Return 之前）" : $"节点 #{before} 之前")}");
        sb.AppendLine($"参数       : {(placeholders ? "全部按签名自动生成（占位）" : $"{specs.Count} 个显式赋值 + {callArgs.Count - specs.Count} 个占位")}");
        sb.AppendLine($"expressions: {countBefore} -> {ws.ExpressionCount(function)}（尚未写盘，需调用 kismet_save）");
        AppendUbergraphCheck(ws, function, sb);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 解析 args：<c>[{"param":"InString","kind":"string","value":"hi"}, {"kind":"int","value":3}]</c>
    /// <c>param</c> 省略时按位置取第一个还没填的参数。
    /// </summary>
    private static IReadOnlyList<KismetWorkspace.ArgSpec> ParseArgSpecs(JsonObject args)
    {
        if (args["args"] is not JsonArray array)
        {
            return [];
        }

        var specs = new List<KismetWorkspace.ArgSpec>();

        foreach (var item in array)
        {
            if (item is not JsonObject spec)
            {
                throw new InvalidOperationException("args 里每一项都必须是对象，例如 {\"param\":\"InString\",\"kind\":\"string\",\"value\":\"hi\"}");
            }

            var kindText = spec["kind"]?.GetValue<string>()
                           ?? throw new InvalidOperationException("args 里每一项都要有 kind（int/long/float/double/bool/byte/string/name/variable/none/self/call）");

            if (!Enum.TryParse<KismetWorkspace.ArgKind>(kindText, ignoreCase: true, out var kind))
            {
                throw new InvalidOperationException(
                    $"不认识的 kind: {kindText}（可用：int/long/float/double/bool/byte/string/name/variable/none/self/call）");
            }

            var valueNode = spec["value"];
            var text = kind is KismetWorkspace.ArgKind.String or KismetWorkspace.ArgKind.Name
                       or KismetWorkspace.ArgKind.Variable ? valueNode?.GetValue<string>() : null;
            long? integer = kind is KismetWorkspace.ArgKind.Int or KismetWorkspace.ArgKind.Long
                or KismetWorkspace.ArgKind.Byte ? valueNode?.GetValue<long>() : null;
            double? number = kind is KismetWorkspace.ArgKind.Float or KismetWorkspace.ArgKind.Double
                ? valueNode?.GetValue<double>() : null;
            bool? flag = kind == KismetWorkspace.ArgKind.Bool ? valueNode?.GetValue<bool>() : null;

            specs.Add(new KismetWorkspace.ArgSpec(
                spec["param"]?.GetValue<string>(),
                new KismetWorkspace.ArgValue(
                    kind,
                    Text: text,
                    Integer: integer,
                    Number: number,
                    Flag: flag,
                    Callee: spec["function"]?.GetValue<string>(),
                    CalleeOwner: spec["owner"]?.GetValue<string>())));
        }

        return specs;
    }

    private static string DeleteNode(JsonObject args)
    {
        var ws = Workspace(args);
        var function = Required(args, "function");
        var path = Required(args, "path");

        var target = NodePaths.Find(ws.Decompile(function), path);
        var countBefore = ws.ExpressionCount(function);
        ws.DeleteNode(function, path);

        var sb = new StringBuilder();
        sb.AppendLine("已删除");
        sb.AppendLine($"节点       : #{path} {target.GetType().Name}  {target.Title}");
        sb.AppendLine($"expressions: {countBefore} -> {ws.ExpressionCount(function)}（尚未写盘，需调用 kismet_save）");
        AppendUbergraphCheck(ws, function, sb);
        return sb.ToString().TrimEnd();
    }

    private static string ReplaceCall(JsonObject args)
    {
        var ws = Workspace(args);
        var function = Required(args, "function");
        var path = Required(args, "path");
        var calleeName = Required(args, "callee");
        var owner = Optional(args, "owner");

        var callee = ws.ResolveFunction(calleeName, owner)
                     ?? throw new InvalidOperationException($"找不到函数 {calleeName}");

        var target = NodePaths.Find(ws.Decompile(function), path);
        var specs = ParseArgSpecs(args);
        var callArgs = ws.BuildArgs(function, callee, specs);

        ws.ReplaceCall(function, path, callee, callArgs.ToArray());

        var sb = new StringBuilder();
        sb.AppendLine("已替换");
        sb.AppendLine($"节点       : #{path} {target.GetType().Name}  {target.Title}");
        sb.AppendLine($"新调用     : {callee.FullPath}");
        sb.AppendLine($"参数       : {(specs.Count == 0 ? "按签名自动生成（占位）" : $"{specs.Count} 个显式赋值 + {callArgs.Count - specs.Count} 个占位")}");
        sb.AppendLine("（尚未写盘，需调用 kismet_save）");
        AppendUbergraphCheck(ws, function, sb);
        return sb.ToString().TrimEnd();
    }

    private static string Verify(JsonObject args)
    {
        var ws = Workspace(args);
        var entries = ws.VerifyAll();

        var structured = entries.Count(e => e.Error is null && e.Structured);
        var flat = entries.Where(e => e.Error is null && !e.Structured).ToArray();
        var failed = entries.Where(e => e.Error is not null).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine($"{entries.Count} 个函数：结构化 {structured}，退回扁平 {flat.Length}，失败 {failed.Length}");
        foreach (var entry in flat)
        {
            sb.AppendLine($"  扁平  {entry.Function}");
        }

        foreach (var entry in failed)
        {
            sb.AppendLine($"  失败  {entry.Function}  {entry.Error}");
        }

        sb.AppendLine(failed.Length == 0 && flat.Length == 0
            ? "全部函数都能结构化还原（且逐字节一致）"
            : "有函数没能结构化——字节仍然一致，但不要基于它的节点树做编辑");
        return sb.ToString().TrimEnd();
    }

    private static string Reload(JsonObject args)
    {
        var assetPath = Required(args, "asset");
        var removed = Cache.Remove(assetPath);
        return removed
            ? $"已丢弃 {assetPath} 的内存状态（下次调用会重新从磁盘打开，未写盘的修改全部丢失）"
            : $"{assetPath} 没有缓存状态（本来就没打开过，或已经被丢弃）";
    }

    /// <summary>改的是 ubergraph 时，把入口点校验结论一起报出来（偏移烘在调用点里，错了会跳进指令中间）。</summary>
    private static void AppendUbergraphCheck(KismetWorkspace ws, string function, StringBuilder sb)
    {
        if (!function.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
        {
            return;
        }

        var checks = ws.CheckUbergraphEntryPoints(function);
        sb.AppendLine(checks.Count == 0
            ? "ubergraph 入口点: 这个资产里没有调用点（可能全部事件在别处）"
            : $"ubergraph 入口点: {checks.Count} 处，{(checks.All(c => c.Ok) ? "全部仍在原位" : "有错位！")}");
        foreach (var check in checks)
        {
            sb.AppendLine($"  {check.CallerFunction,-42} @ {check.Offset}  {check.Before} -> {check.After}");
        }
    }

    private static string BakeUbergraph(JsonObject args)
    {
        var ws = Workspace(args);
        var function = Required(args, "function");

        var baked = ws.BakeUbergraph(function)
                    ?? throw new InvalidOperationException($"{function} 不是 ubergraph 形状");

        var sb = new StringBuilder();
        sb.AppendLine($"段起始偏移（按段顺序烘出来的）: {string.Join(", ", baked.EntryOffsets)}");
        sb.AppendLine($"epilogue: {baked.EpilogueOffset}");
        sb.AppendLine($"expressions: {baked.Expressions.Count}");

        // 真正的入口表不在资产里，而在**调用点**：LocalFinal ExecuteUbergraph_X(<IntConst>)
        var sites = ws.UbergraphCallSites(function);
        sb.AppendLine($"调用点入口表: {sites.Count} 处（UE5 把入口偏移烘进了调用点的 EX_IntConst）");
        foreach (var site in sites)
        {
            sb.AppendLine($"  {site.CallerFunction,-42} @ {site.Offset}");
        }

        var siteOffsets = sites.Select(s => s.Offset).Distinct().OrderBy(x => x).ToArray();
        var sectionOffsets = baked.EntryOffsets.Select(x => (int)x).OrderBy(x => x).ToArray();
        sb.AppendLine(siteOffsets.SequenceEqual(sectionOffsets)
            ? "入口表一致性: 一致（调用点常量 = 段起始偏移）"
            : "入口表一致性: 不一致——段是连续块，一个段可以有多个入口（事件从段中间进入），这是正常的；" +
              "写回时以调用点为准");

        return sb.ToString().TrimEnd();
    }

    private static string StructMembers(JsonObject args)
    {
        var usmapPath = Required(args, "usmap");
        var structName = Required(args, "struct");

        var catalog = UsmapCatalog.Load(usmapPath);
        var full = catalog.ResolveSchema(structName);

        var sb = new StringBuilder();
        sb.AppendLine($"struct {full}");

        var supers = catalog.SuperChainOf(full);
        if (supers.Count > 0)
        {
            sb.AppendLine("super: " + string.Join(" <- ", supers));
        }

        foreach (var member in catalog.MembersOf(full))
        {
            sb.AppendLine($"{member.Name,-28} {member.TypeName,-18} size={member.ArraySize}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Save(JsonObject args)
    {
        var ws = Workspace(args);
        var asset = Required(args, "asset");
        var outDir = Required(args, "outDir");
        var fileName = Optional(args, "fileName") ?? "out.uasset";

        Directory.CreateDirectory(outDir);

        // 先跑入口点校验（Save 内部还会再拦一次），好把结论写进回报里
        var checks = ws.VerifyEditedUbergraphs();
        var saved = ws.Save(outDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("已写回");
        sb.AppendLine($"uasset: {Path.Combine(outDir, fileName)} ({saved.UAsset.Length} 字节)");
        sb.AppendLine($"uexp  : {Path.Combine(outDir, Path.ChangeExtension(fileName, ".uexp"))} ({saved.UExp.Length} 字节)");

        if (checks.Count > 0)
        {
            sb.AppendLine($"ubergraph 入口点: {checks.Count} 处，全部仍在原位");
            foreach (var check in checks)
            {
                sb.AppendLine($"  {check.CallerFunction,-42} @ {check.Offset}  {check.Before}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ---------------- 基础设施 ----------------

    private static KismetWorkspace Workspace(JsonObject args)
    {
        var assetPath = Required(args, "asset");
        var usmapPath = Required(args, "usmap");

        if (!Cache.TryGetValue(assetPath, out var ws))
        {
            var uexp = Path.ChangeExtension(assetPath, ".uexp");
            if (!File.Exists(uexp))
            {
                throw new InvalidOperationException($"缺少 .uexp: {uexp}");
            }

            // 按资产路径固定工作目录：避免每次调用都新建一个（会无限堆积）
            var workDir = Path.Combine(WorkRoot, StableKey(assetPath));
            ws = KismetWorkspace.Open(
                File.ReadAllBytes(assetPath),
                File.ReadAllBytes(uexp),
                File.ReadAllBytes(usmapPath),
                workDir);

            Cache[assetPath] = ws;
        }

        // 可选数据源：每次调用按参数挂上（幂等）
        var catalogPath = Optional(args, "catalog");
        if (catalogPath is not null && ws.Catalog is null)
        {
            ws.AttachCatalog(FunctionCatalog.Load(File.ReadAllText(catalogPath)));

            var objectsPath = Path.Combine(Path.GetDirectoryName(catalogPath)!, "objects.json");
            if (File.Exists(objectsPath) && ws.Objects is null)
            {
                ws.AttachObjects(ObjectCatalog.Load(File.ReadAllText(objectsPath)));
            }
        }

        if (ws.Usmap is null)
        {
            ws.AttachUsmap(UsmapCatalog.Load(usmapPath));
        }

        return ws;
    }

    /// <summary>资产路径 → 稳定的短目录名（避免工作目录无限增长）。</summary>
    private static string StableKey(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string Required(JsonObject args, string name)
        => Optional(args, name) ?? throw new InvalidOperationException($"缺少参数 {name}");

    private static string? Optional(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null)
        {
            return null;
        }

        var value = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void Render(KNode node, string path, int depth, StringBuilder sb)
    {
        var pad = new string(' ', depth * 2);

        switch (node)
        {
            case IfNode f:
                sb.AppendLine($"{pad}#{path} If");
                for (var i = 0; i < f.Then.Count; i++)
                {
                    Render(f.Then[i], $"{path}.{i}", depth + 1, sb);
                }

                if (f.Else is { Count: > 0 } els)
                {
                    sb.AppendLine($"{pad}Else");
                    for (var i = 0; i < els.Count; i++)
                    {
                        Render(els[i], $"{path}.{f.Then.Count + i}", depth + 1, sb);
                    }
                }

                return;

            case LoopNode loop:
                sb.AppendLine($"{pad}#{path} Loop");
                for (var i = 0; i < loop.Body.Count; i++)
                {
                    Render(loop.Body[i], $"{path}.{i}", depth + 1, sb);
                }

                return;

            case BottomLoopNode bottom:
                sb.AppendLine($"{pad}#{path} LoopDoWhile");
                for (var i = 0; i < bottom.Body.Count; i++)
                {
                    Render(bottom.Body[i], $"{path}.{i}", depth + 1, sb);
                }

                for (var i = 0; i < bottom.Tail.Count; i++)
                {
                    Render(bottom.Tail[i], $"{path}.{bottom.Body.Count + i}", depth + 1, sb);
                }

                return;

            case BlockGraphNode graph:
                sb.AppendLine($"{pad}#{path} BlockGraph {graph.Blocks.Count} 块");
                for (var i = 0; i < graph.Blocks.Count; i++)
                {
                    var block = graph.Blocks[i];
                    var target = block.TargetIndex is { } t ? $" -> #{t}" : "";
                    sb.AppendLine($"{pad}  #{path}.{i} @{block.StartOffset} {block.Exit}{target}");
                    for (var j = 0; j < block.Nodes.Count; j++)
                    {
                        Render(block.Nodes[j], $"{path}.{i}.{j}", depth + 2, sb);
                    }
                }

                return;

            case UberGraphNode uber:
                sb.AppendLine($"{pad}#{path} UberGraph {uber.Sections.Count} 段");
                for (var i = 0; i < uber.Sections.Count; i++)
                {
                    var section = uber.Sections[i];
                    var label = string.IsNullOrEmpty(section.Label) ? "" : " " + section.Label;
                    var target = section.TargetOrdinal is { } t ? $" -> #{t}" : "";
                    sb.AppendLine($"{pad}  #{path}.{i} @{section.StartOffset}{label} {section.Terminator}{target}");
                    for (var j = 0; j < section.Nodes.Count; j++)
                    {
                        Render(section.Nodes[j], $"{path}.{i}.{j}", depth + 2, sb);
                    }
                }

                return;

            default:
                sb.AppendLine($"{pad}#{path} {node.Title}{RenderArgs(node)}");
                return;
        }
    }

    /// <summary>调用节点把参数一起显示出来（agent 要能看见自己写进去的值）。</summary>
    private static string RenderArgs(KNode node) => node switch
    {
        CallNode c => ArgRender.List(c.Args),
        MathCallNode m => ArgRender.List(m.Args),
        LocalFinalCallNode l => ArgRender.List(l.Args),
        VirtualCallNode v => ArgRender.List(v.Args),
        MulticastCallNode mc => ArgRender.List(mc.Args),
        ContextCallNode cc => ArgRender.List(cc.Args),
        AssignNode a => " = " + ArgRender.Of(a.Value),
        AssignBoolNode ab => " = " + ArgRender.Of(ab.Value),
        PersistentAssignNode pa => " = " + ArgRender.Of(pa.Value),
        ReturnNode { Value: { } value } => " " + ArgRender.Of(value),
        _ => ""
    };
}
