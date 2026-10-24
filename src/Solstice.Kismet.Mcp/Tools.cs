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
        "kismet_bake_ubergraph" => BakeUbergraph(args),
        "kismet_struct_members" => StructMembers(args),
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
        var nodes = ws.Decompile(function);

        var sb = new StringBuilder();
        sb.AppendLine($"{function}: {nodes.Count} 个节点");
        foreach (var node in nodes)
        {
            Render(node, 0, sb);
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

        var callee = ws.ResolveFunction(calleeName, owner)
                     ?? throw new InvalidOperationException($"找不到函数 {calleeName}");

        var before = ws.ExpressionCount(function);

        if (withResult)
        {
            var inserted = ws.InsertCallWithResult(function, callee);
            return $"已插入（结果存进临时变量）\n" +
                   $"variable   : {inserted.Variable}\n" +
                   $"returnType : {inserted.ReturnType?.Type ?? "?"} size={inserted.ReturnType?.Size}\n" +
                   $"expressions: {before} -> {ws.ExpressionCount(function)}\n" +
                   $"bytes      : {inserted.Bytes}（尚未写盘，需调用 kismet_save）";
        }

        ws.InsertCall(function, callee);
        return $"已插入\n" +
               $"callee     : {callee.FullPath}\n" +
               $"expressions: {before} -> {ws.ExpressionCount(function)}（尚未写盘，需调用 kismet_save）";
    }

    private static string BakeUbergraph(JsonObject args)
    {
        var ws = Workspace(args);
        var function = Required(args, "function");

        var baked = ws.BakeUbergraph(function)
                    ?? throw new InvalidOperationException($"{function} 不是 ubergraph 形状");

        var sb = new StringBuilder();
        sb.AppendLine($"entryOffsets: {string.Join(", ", baked.EntryOffsets)}");
        sb.AppendLine($"epilogue    : {baked.EpilogueOffset}");
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
        var saved = ws.Save(outDir, fileName);

        return $"已写回\n" +
               $"uasset: {Path.Combine(outDir, fileName)} ({saved.UAsset.Length} 字节)\n" +
               $"uexp  : {Path.Combine(outDir, Path.ChangeExtension(fileName, ".uexp"))} ({saved.UExp.Length} 字节)";
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

            var workDir = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")[..8]);
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

    private static void Render(KNode node, int depth, StringBuilder sb)
    {
        var pad = new string(' ', depth * 2);

        switch (node)
        {
            case IfNode f:
                sb.AppendLine($"{pad}If");
                foreach (var child in f.Then)
                {
                    Render(child, depth + 1, sb);
                }

                if (f.Else is { Count: > 0 } els)
                {
                    sb.AppendLine($"{pad}Else");
                    foreach (var child in els)
                    {
                        Render(child, depth + 1, sb);
                    }
                }

                return;

            case LoopNode loop:
                sb.AppendLine($"{pad}Loop");
                foreach (var child in loop.Body)
                {
                    Render(child, depth + 1, sb);
                }

                return;

            case BottomLoopNode bottom:
                sb.AppendLine($"{pad}LoopDoWhile");
                foreach (var child in bottom.Body.Concat(bottom.Tail))
                {
                    Render(child, depth + 1, sb);
                }

                return;

            case BlockGraphNode graph:
                sb.AppendLine($"{pad}BlockGraph {graph.Blocks.Count} 块");
                foreach (var block in graph.Blocks)
                {
                    var target = block.TargetIndex is { } t ? $" -> #{t}" : "";
                    sb.AppendLine($"{pad}  #{block.Index} @{block.StartOffset} {block.Exit}{target}");
                    foreach (var child in block.Nodes)
                    {
                        Render(child, depth + 2, sb);
                    }
                }

                return;

            case UberGraphNode uber:
                sb.AppendLine($"{pad}UberGraph {uber.Sections.Count} 段");
                foreach (var section in uber.Sections)
                {
                    var label = string.IsNullOrEmpty(section.Label) ? "" : " " + section.Label;
                    var target = section.TargetOrdinal is { } t ? $" -> #{t}" : "";
                    sb.AppendLine($"{pad}  @{section.StartOffset}{label} {section.Terminator}{target}");
                    foreach (var child in section.Nodes)
                    {
                        Render(child, depth + 2, sb);
                    }
                }

                return;

            default:
                sb.AppendLine($"{pad}{node.Title}");
                return;
        }
    }
}
