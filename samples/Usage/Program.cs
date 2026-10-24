using Solstice.Kismet;
using UAssetAPI.ExportTypes;

// 用法示例：对着真实资产跑一遍主要场景
//   dotnet run --project samples/Usage -- <asset.uasset> <usmap> [functions.json]
//
// 每个场景都打印「做了什么 + 结果」，可以直接对照 API 看。

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: Usage <asset.uasset> <usmap> [functions.json]");
    return 2;
}

var assetPath = args[0];
var usmapPath = args[1];
var catalogPath = args.Length > 2 ? args[2] : null;
// 工作目录必须可写：KismetWorkspace.Open 会把三份字节落到这里再按路径打开
// （UAssetAPI 只能按路径构造）。这里用当前目录下的子目录，避免 %TEMP% 在某些环境被拒。
var workDir = Path.Combine(Directory.GetCurrentDirectory(), ".sample-work");

var ws = KismetWorkspace.Open(
    File.ReadAllBytes(assetPath),
    File.ReadAllBytes(Path.ChangeExtension(assetPath, ".uexp")),
    File.ReadAllBytes(usmapPath),
    workDir);

Console.WriteLine($"资产: {Path.GetFileName(assetPath)}");
Console.WriteLine($"带字节码的函数: {ws.Functions.Count} 个");
Console.WriteLine();

// ---- 场景 1：看有哪些函数，挑一个「有实际内容但最小」的 ----
var smallest = ws.Functions.Where(f => ws.ExpressionCount(f) >= 4)
                    .OrderBy(ws.ExpressionCount)
                    .DefaultIfEmpty(ws.Functions.OrderBy(ws.ExpressionCount).First())
                    .First();
Console.WriteLine($"[1] 最小的有内容函数: {smallest}（{ws.ExpressionCount(smallest)} 条表达式）");

// ---- 场景 2：反编译，看节点 ----
var nodes = ws.Decompile(smallest);
Console.WriteLine($"[2] 反编译得到 {nodes.Count} 个节点:");
foreach (var node in nodes.Take(6))
{
    Console.WriteLine($"      {node.Title}");
}

if (nodes.Count > 6)
{
    Console.WriteLine("      …");
}

Console.WriteLine();

// ---- 场景 3：ubergraph 的结构与入口表 ----
var uberName = ws.Functions.FirstOrDefault(f => f.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal));
if (uberName is not null)
{
    var baked = ws.BakeUbergraph(uberName);
    Console.WriteLine($"[3] {uberName} 重烘: {baked?.EntryOffsets.Count} 个入口, 尾声 off={baked?.EpilogueOffset}");
}
else
{
    Console.WriteLine("[3] 这个资产没有 ubergraph");
}

Console.WriteLine();

// ---- 场景 4：看一次调用的引脚（哪些参数只是占位）----
if (catalogPath is not null && File.Exists(catalogPath))
{
    var catalog = FunctionCatalog.Load(File.ReadAllText(catalogPath));
    ws.AttachCatalog(catalog);

    var plan = ws.PlanCall("PrintString");
    Console.WriteLine($"[4] PlanCall(PrintString): shape={plan.Shape}, owner={plan.OwnerPath}");

    // 推荐用法：先解析成「引用」，之后不再按名字查表
    var callee = ws.ResolveFunction("PrintString");
    Console.WriteLine($"    引用: {callee}  idx={callee?.Index.Index} exported={callee?.IsExported}");
    foreach (var pin in plan.Pins)
    {
        var mark = pin.IsOutput ? "->" : pin.IsPlaceholder ? "??" : "  ";
        Console.WriteLine($"      {mark} {pin.Name,-20} {pin.Type,-16} {pin.Dir}");
    }

    Console.WriteLine();

    // ---- 场景 5：插入一次调用并写回，再读回来确认 ----
    var target = smallest;
    var before = ws.ExpressionCount(target);
    ws.InsertCall(target, ws.ResolveFunction("Greater_IntInt")!);   // 传引用，不传名字
    var after = ws.ExpressionCount(target);

    var saved = ws.Save(workDir, "edited.uasset");
    var reopened = KismetWorkspace.Open(saved.UAsset, saved.UExp, File.ReadAllBytes(usmapPath), workDir + "-re");
    var again = reopened.Decompile(target);

    Console.WriteLine($"[5] 往 {target} 插入 Greater_IntInt: 表达式 {before} -> {after}");
    Console.WriteLine($"      写回 uasset {saved.UAsset.Length} 字节 / uexp {saved.UExp.Length} 字节");
    Console.WriteLine($"      重新打开后该函数 {again.Count} 个节点（读回成功）");
}
else
{
    Console.WriteLine("[4][5] 没给 functions.json，跳过「新增调用」相关场景");
}

if (Directory.Exists(workDir))
{
    Directory.Delete(workDir, true);
}

if (Directory.Exists(workDir + "-re"))
{
    Directory.Delete(workDir + "-re", true);
}

return 0;
