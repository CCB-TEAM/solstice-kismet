using System.Text.RegularExpressions;
using Solstice.Kismet;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

// 同进程批量覆盖率：一次加载，遍历资产里所有带字节码的函数，
// 逐个「反编译 → 回编译 → 字节比对」。
//
// 用法:
//   KismetCoverage <资产目录> <usmap> [--max N] [--verbose]
//
// 为什么不用逐函数起进程：Cards 有 2000+ 资产、数万函数，
// 每个函数一次 dotnet 启动要几小时；这里一次进程扫完。

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: KismetCoverage <资产目录> <usmap> [--max N] [--verbose]");
    return 2;
}

var assetDir = args[0];
var usmapPath = args[1];
var maxAssets = args.Length > 2 && args[2] == "--max" ? int.Parse(args[3]) : int.MaxValue;
var verbose = args.Contains("--verbose");
var showDegraded = args.Contains("--degraded");
var useWorkspace = args.Contains("--workspace");
var insertIndex = Array.IndexOf(args, "--insert");
var insertCallee = insertIndex >= 0 && insertIndex + 1 < args.Length ? args[insertIndex + 1] : null;
var catalogIndex = Array.IndexOf(args, "--catalog");
var catalogPath = catalogIndex >= 0 && catalogIndex + 1 < args.Length ? args[catalogIndex + 1] : null;
var planIndex = Array.IndexOf(args, "--plan");
var planFunction = planIndex >= 0 && planIndex + 1 < args.Length ? args[planIndex + 1] : null;

if (!Directory.Exists(assetDir))
{
    Console.Error.WriteLine($"目录不存在: {assetDir}");
    return 2;
}

var usmap = new Usmap(usmapPath);
var files = Directory.GetFiles(assetDir, "*.uasset", SearchOption.AllDirectories)
    .OrderBy(f => f, StringComparer.Ordinal)
    .Take(maxAssets)
    .ToArray();

Console.WriteLine($"资产 {files.Length} 个，usmap={Path.GetFileName(usmapPath)}" + (useWorkspace ? "（走 KismetWorkspace 门面）" : ""));

if (insertCallee is not null)
{
    return RunInsert(files, usmapPath, insertCallee, catalogPath);
}

if (planFunction is not null)
{
    return RunPlan(files, usmapPath, planFunction, catalogPath);
}

if (useWorkspace)
{
    return RunWorkspace(files, usmapPath);
}


var total = 0;
var passed = 0;
var degraded = 0;
var treeCount = 0;
var blockGraphCount = 0;
var flatCount = 0;
var assetFailures = 0;
var failures = new List<(string Asset, string Function, string Reason)>();
var degradedList = new List<(string Asset, string Function, string Warn)>();
var reasons = new Dictionary<string, int>(StringComparer.Ordinal);

foreach (var file in files)
{
    var assetName = Path.GetFileNameWithoutExtension(file);
    var assetOk = true;

    try
    {
        var asset = new UAsset(file, EngineVersion.VER_UE5_6, usmap);

        var names = new Dictionary<int, string>();
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            names[-(i + 1)] = asset.Imports[i].ObjectName?.ToString() ?? "";
        }

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            names[i + 1] = asset.Exports[i].ObjectName?.ToString() ?? "";
        }

        var functions = asset.Exports.OfType<FunctionExport>()
            .Where(f => f.ScriptBytecode is { Length: > 0 })
            .ToArray();

        foreach (var fn in functions)
        {
            total++;
            var bytecode = fn.ScriptBytecode!;
            string? firstWarn = null;
            var options = new DecompileOptions(
                Measure: e => e.GetSize(asset),
                ResolveImport: idx => names.TryGetValue(idx?.Index ?? 0, out var n) ? n : null,
                Warn: m => firstWarn ??= m);

            var originalBytes = Serialize(bytecode, asset);

            try
            {
                // 与 DecompileVerified 一致：先试花式，再试保守，最后才退扁平
                byte[]? Recompile(IReadOnlyList<KNode> ns) => Serialize(
                    KismetCompiler.CompileToExpressions(ns, null, null, e => e.GetSize(asset), n => new FName(asset, n)),
                    asset);

                var attempts = new[]
                {
                    options,
                    options with { FoldBottomLoops = false },
                    options with { UseBlockGraph = true }
                };
                var structuredSame = false;
                IReadOnlyList<KNode>? finalNodes = null;
                var attemptIndex = -1;

                for (var a = 0; a < attempts.Length; a++)
                {
                    var candidate = KismetDecompiler.Decompile(bytecode, attempts[a]);
                    if (originalBytes.AsSpan().SequenceEqual(Recompile(candidate)))
                    {
                        structuredSame = true;
                        finalNodes = candidate;
                        attemptIndex = a;
                        break;
                    }
                }

                if (!structuredSame)
                {
                    degraded++;
                    flatCount++;
                    degradedList.Add((assetName, fn.ObjectName.ToString(), firstWarn ?? "(无告警)"));
                    finalNodes = KismetDecompiler.Decompile(bytecode, options with { Flatten = true });
                }
                else if (attemptIndex == attempts.Length - 1)
                {
                    blockGraphCount++;
                }
                else
                {
                    treeCount++;
                }

                var rebuilt = KismetCompiler.CompileToExpressions(
                    finalNodes!, null, null, e => e.GetSize(asset), n => new FName(asset, n));
                var finalBytes = Serialize(rebuilt, asset);
                if (originalBytes.AsSpan().SequenceEqual(finalBytes))
                {
                    passed++;
                }
                else
                {
                    assetOk = false;
                    var reason = FirstDiff(originalBytes, finalBytes, bytecode, asset);
                    failures.Add((assetName, fn.ObjectName.ToString(), reason));
                    Bump(reasons, reason);
                }
            }
            catch (Exception ex)
            {
                assetOk = false;
                var reason = ex.Message.Length > 90 ? ex.Message[..90] : ex.Message;
                failures.Add((assetName, fn.ObjectName.ToString(), reason));
                Bump(reasons, reason);
            }
        }
    }
    catch (Exception ex)
    {
        assetOk = false;
        assetFailures++;
        var reason = "资产打开失败: " + (ex.Message.Length > 80 ? ex.Message[..80] : ex.Message);
        failures.Add((assetName, "<asset>", reason));
        Bump(reasons, reason);
    }

    if (!assetOk || verbose)
    {
        Console.WriteLine($"  {(assetOk ? "ok  " : "FAIL")} {assetName}");
    }
}

Console.WriteLine();
Console.WriteLine("==== 汇总 ====");
Console.WriteLine($"资产          : {files.Length}（打开失败 {assetFailures}）");
Console.WriteLine($"函数          : {total}");
Console.WriteLine($"逐字节一致    : {passed}");
Console.WriteLine($"失败          : {total - passed}");
Console.WriteLine($"结构化退化    : {degraded}（退回扁平）");
Console.WriteLine($"  树形结构化  : {treeCount}");
Console.WriteLine($"  基本块图    : {blockGraphCount}");
Console.WriteLine($"  扁平        : {flatCount}");

if (showDegraded && degradedList.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("结构化退化清单（字节一致，只是退回扁平）:");
    foreach (var (asset, fn, warn) in degradedList)
    {
        Console.WriteLine($"  {asset} :: {fn}");
        Console.WriteLine($"      首个告警: {warn}");
    }
}

if (reasons.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("失败原因（按次数）:");
    foreach (var (reason, count) in reasons.OrderByDescending(kv => kv.Value).Take(20))
    {
        Console.WriteLine($"  {count,6}  {reason}");
    }

    Console.WriteLine();
    Console.WriteLine("失败样本（最多 20 条）:");
    foreach (var (asset, fn, reason) in failures.Take(20))
    {
        Console.WriteLine($"  {asset} :: {fn} :: {reason}");
    }
}

return total == passed ? 0 : 1;

// 走 KismetWorkspace 门面：验证主项目真正会用的那条路径
// （Import/Export 双向名字表、CompileContext、字节校验）。
static int RunWorkspace(string[] files, string usmapPath)
{
    var workDir = Path.Combine(Path.GetTempPath(), "kismet-coverage-" + Guid.NewGuid().ToString("N")[..8]);
    var usmapBytes = File.ReadAllBytes(usmapPath);

    var total = 0;
    var ok = 0;
    var failures = new List<string>();

    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        try
        {
            var uexp = Path.ChangeExtension(file, ".uexp");
            if (!File.Exists(uexp))
            {
                continue;
            }

            var ws = KismetWorkspace.Open(
                File.ReadAllBytes(file), File.ReadAllBytes(uexp), usmapBytes, workDir);

            foreach (var fn in ws.Functions)
            {
                total++;
                try
                {
                    // Decompile 内部已做字节校验；能返回就说明该表示是保真的
                    var nodes = ws.Decompile(fn);
                    if (nodes.Count == 0)
                    {
                        failures.Add($"{name} :: {fn} :: 反编译结果为空");
                    }
                    else
                    {
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{name} :: {fn} :: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{name} :: <asset> :: {ex.GetType().Name}: {ex.Message}");
        }
    }

    if (Directory.Exists(workDir))
    {
        Directory.Delete(workDir, true);
    }

    Console.WriteLine();
    Console.WriteLine($"门面路径: 函数 {total}，成功 {ok}，失败 {failures.Count}");
    foreach (var f in failures.Take(20))
    {
        Console.WriteLine("  " + f);
    }

    return failures.Count == 0 ? 0 : 1;
}

// 插入调用端到端验证：反编译 → CreateCall（按 jmap 签名选形态）→ 编译写回 →
// 存盘 → 重新打开 → 反编译确认新调用在、且能再读回来。
// 走的是编辑器「插入节点」的完整链路。
// 打印 PlanCall 的结果：形态 + 每个引脚 + 哪些是占位。
// 这是 PlanCall 的唯一实测入口（自检没有资产，跑不到它）。
static int RunPlan(string[] files, string usmapPath, string function, string? catalogPath)
{
    if (catalogPath is null || !File.Exists(catalogPath))
    {
        Console.Error.WriteLine("--plan 需要 --catalog <functions.json>");
        return 2;
    }

    var workDir = Path.Combine(Path.GetTempPath(), "kismet-plan-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(workDir);

    var file = files.FirstOrDefault(f => File.Exists(Path.ChangeExtension(f, ".uexp")));
    if (file is null)
    {
        Console.Error.WriteLine("没有可用资产");
        return 2;
    }

    var ws = KismetWorkspace.Open(
        File.ReadAllBytes(file),
        File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")),
        File.ReadAllBytes(usmapPath),
        workDir);

    ws.AttachCatalog(FunctionCatalog.Load(File.ReadAllText(catalogPath)));

    var plan = ws.PlanCall(function);
    Console.WriteLine($"PlanCall({function})");
    Console.WriteLine($"  owner={plan.OwnerPath}");
    Console.WriteLine($"  shape={plan.Shape}  flags={plan.Flags}");
    foreach (var pin in plan.Pins)
    {
        var mark = pin.IsOutput ? "->" : pin.IsPlaceholder ? "??" : "  ";
        Console.WriteLine($"  {mark} {pin.Name,-24} {pin.Type,-18} {pin.Dir,-7} {pin.Value.GetType().Name}");
    }

    Directory.Delete(workDir, true);
    return plan.Pins.Count > 0 ? 0 : 1;
}

static int RunInsert(string[] files, string usmapPath, string callee, string? catalogPath)
{
    if (catalogPath is null || !File.Exists(catalogPath))
    {
        Console.Error.WriteLine("--insert 需要 --catalog <functions.json>");
        return 2;
    }

    var workDir = Path.Combine(Path.GetTempPath(), "kismet-insert-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(workDir);

    var usmapBytes = File.ReadAllBytes(usmapPath);
    var catalogBytes = File.ReadAllText(catalogPath);
    var catalog = FunctionCatalog.Load(catalogBytes);

    var total = 0;
    var ok = 0;
    var failures = new List<string>();

    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var uexp = Path.ChangeExtension(file, ".uexp");
        if (!File.Exists(uexp))
        {
            continue;
        }

        try
        {
            var ws = KismetWorkspace.Open(File.ReadAllBytes(file), File.ReadAllBytes(uexp), usmapBytes, workDir);
            ws.AttachCatalog(catalog);

            // 挑最小的函数，改动面最小
            var target = ws.Functions.OrderBy(f => ws.ExpressionCount(f)).FirstOrDefault();
            if (target is null)
            {
                continue;
            }

            total++;

            // 便捷 API：一行完成「反编译 + 插入调用 + 编译写回」
            ws.InsertCall(target, callee);
            var expectedArgs = catalog.Find(null, callee)?.Inputs.Count ?? -1;
            var saved = ws.Save(workDir, "inserted.uasset");

            // 重新打开存盘结果，确认新调用能反编译回来（参数个数也要对上）
            var reopened = KismetWorkspace.Open(saved.UAsset, saved.UExp, usmapBytes, workDir + "-re");
            var again = reopened.Decompile(target);

            var found = FindCall(again, callee);
            if (found is null)
            {
                failures.Add($"{name} :: {target} :: 写回后找不到 {callee}");
            }
            else if (expectedArgs >= 0 && found.Value != expectedArgs)
            {
                failures.Add($"{name} :: {target} :: 参数个数 {found.Value} != 签名 {expectedArgs}");
            }
            else
            {
                ok++;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{name} :: {ex.GetType().Name}: {ex.Message}");
        }
    }

    foreach (var dir in new[] { workDir, workDir + "-re" })
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    Console.WriteLine();
    Console.WriteLine($"插入调用 {callee}: 资产 {total}，成功 {ok}，失败 {failures.Count}");
    foreach (var f in failures.Take(20))
    {
        Console.WriteLine("  " + f);
    }

    return failures.Count == 0 ? 0 : 1;
}

static int? FindCall(IEnumerable<KNode> nodes, string callee)
{
    foreach (var node in nodes)
    {
        switch (node)
        {
            case CallNode c when c.Function == callee:
                return c.Args.Count;

            case VirtualCallNode v when v.Function == callee:
                return v.Args.Count;

            case ContextCallNode cc when cc.Function == callee:
                return cc.Args.Count;

            case MathCallNode m when m.Function == callee:
                return m.Args.Count;

            case LocalFinalCallNode lf when lf.Function == callee:
                return lf.Args.Count;

            case IfNode f:
                foreach (var r in new[] { FindCall(f.Then, callee), f.Else is { } e ? FindCall(e, callee) : null })
                {
                    if (r is not null) { return r; }
                }

                break;

            case LoopNode loop when FindCall(loop.Body, callee) is { } body:
                return body;

            case BlockGraphNode graph:
                foreach (var b in graph.Blocks)
                {
                    if (FindCall(b.Nodes, callee) is { } r) { return r; }
                }

                break;

            case UberGraphNode uber:
                foreach (var s in uber.Sections)
                {
                    if (FindCall(s.Nodes, callee) is { } r) { return r; }
                }

                break;
        }
    }

    return null;
}

static void Bump(Dictionary<string, int> map, string key)
{
    // 把「表达式 FooConst 暂不支持」这类消息归一化，便于聚类
    var normalized = Regex.Replace(key, @"\b(EX_\w+|[A-Z]\w*Const|[A-Z]\w+Cast)\b", "<op>");
    map[normalized] = map.TryGetValue(normalized, out var v) ? v + 1 : 1;
}

static byte[] Serialize(IReadOnlyList<KismetExpression> expressions, UAsset asset)
{
    using var ms = new MemoryStream();
    var writer = new AssetBinaryWriter(ms, asset);
    foreach (var e in expressions)
    {
        ExpressionSerializer.WriteExpression(e, writer);
    }

    return ms.ToArray();
}

static string FirstDiff(byte[] a, byte[] b, KismetExpression[] bytecode, UAsset asset)
{
    var limit = Math.Min(a.Length, b.Length);
    for (var i = 0; i < limit; i++)
    {
        if (a[i] != b[i])
        {
            uint walk = 0;
            for (var k = 0; k < bytecode.Length; k++)
            {
                var size = bytecode[k].GetSize(asset);
                if (i >= walk && i < walk + size)
                {
                    return $"字节不一致 @{i}，位于第 {k} 条 {bytecode[k].Inst}";
                }

                walk += size;
            }

            return $"字节不一致 @{i}";
        }
    }

    return a.Length == b.Length ? "一致" : $"长度不同 {a.Length} vs {b.Length}";
}
