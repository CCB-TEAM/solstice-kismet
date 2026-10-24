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

Console.WriteLine($"资产 {files.Length} 个，usmap={Path.GetFileName(usmapPath)}");

var total = 0;
var passed = 0;
var degraded = 0;
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

                var attempts = new[] { options, options with { FoldBottomLoops = false } };
                var structuredSame = false;
                IReadOnlyList<KNode>? finalNodes = null;

                foreach (var attempt in attempts)
                {
                    var candidate = KismetDecompiler.Decompile(bytecode, attempt);
                    if (originalBytes.AsSpan().SequenceEqual(Recompile(candidate)))
                    {
                        structuredSame = true;
                        finalNodes = candidate;
                        break;
                    }
                }

                if (!structuredSame)
                {
                    degraded++;
                    degradedList.Add((assetName, fn.ObjectName.ToString(), firstWarn ?? "(无告警)"));
                    finalNodes = KismetDecompiler.Decompile(bytecode, options with { Flatten = true });
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
Console.WriteLine($"结构化退化    : {degraded}（字节仍一致，只是退回扁平）");

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
