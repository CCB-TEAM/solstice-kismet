using Solstice.Kismet;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

// 用法:
//   KismetProbe <asset.uasset> <usmap>             列出函数
//   KismetProbe <asset.uasset> <usmap> <函数名>    打印结构 + 反编译 + 回编译比对

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: KismetProbe <asset.uasset> <usmap> [函数名]");
    return 2;
}

// UAssetAPI 用 File.Open(ReadWrite) 打开，先把两半复制到可写目录
var work = Path.Combine(Path.GetTempPath(), "kismet-probe-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var assetCopy = Path.Combine(work, "a.uasset");
File.Copy(args[0], assetCopy, true);
var uexpSrc = Path.ChangeExtension(args[0], ".uexp");
if (File.Exists(uexpSrc))
{
    File.Copy(uexpSrc, Path.ChangeExtension(assetCopy, ".uexp"), true);
}

var asset = new UAsset(assetCopy, EngineVersion.VER_UE5_6, new Usmap(args[1]));

var functions = asset.Exports.OfType<FunctionExport>()
    .Where(f => f.ScriptBytecode is { Length: > 0 })
    .ToArray();

if (args.Length == 2)
{
    Console.WriteLine($"[probe] {functions.Length} 个带字节码的函数");
    foreach (var f in functions.OrderByDescending(x => x.ScriptBytecode!.Length).Take(30))
    {
        Console.WriteLine($"  {f.ScriptBytecode!.Length,5} 条  {f.ObjectName}");
    }
    return 0;
}

var target = functions.FirstOrDefault(f => f.ObjectName.ToString() == args[2]);
if (target is null)
{
    Console.Error.WriteLine($"找不到函数: {args[2]}");
    return 1;
}

var bytecode = target.ScriptBytecode!;
Console.WriteLine($"[probe] {target.ObjectName}  共 {bytecode.Length} 条表达式");

// ---- 1. 字节码结构 ----
var importNames = new Dictionary<int, string>();
for (var i = 0; i < asset.Imports.Count; i++)
{
    importNames[-(i + 1)] = asset.Imports[i].ObjectName?.ToString() ?? "";
}

Console.WriteLine();
Console.WriteLine("=== 结构 ===");
uint offset = 0;
for (var i = 0; i < bytecode.Length; i++)
{
    var e = bytecode[i];
    var size = e.GetSize(asset);
    var extra = e switch
    {
        EX_Jump j => $"-> {j.CodeOffset}",
        EX_JumpIfNot jn => $"-> {jn.CodeOffset}",
        EX_FinalFunction ff => importNames.TryGetValue(ff.StackNode?.Index ?? 0, out var n) ? n : $"<{ff.StackNode?.Index}>",
        EX_LocalVirtualFunction v => v.VirtualFunctionName?.ToString() ?? "",
        EX_IntConst ic => ic.Value.ToString(),
        EX_LetBool lb => $"varType={lb.VariableExpression?.GetType().Name} valType={lb.AssignmentExpression?.GetType().Name}",
        EX_Let l2 => $"varType={l2.Variable?.GetType().Name} valType={l2.Expression?.GetType().Name}",
        _ => ""
    };
    Console.WriteLine($"  {i,4}  off={offset,-6} {e.Inst,-24} {extra}");
    offset += size;
}
Console.WriteLine($"  总计 {offset} 字节");

// ---- 基本块划分：leader = 起始 / 跳转目标 / 跳转的下一条 ----
var offs = new uint[bytecode.Length];
uint acc2 = 0;
for (var i2 = 0; i2 < bytecode.Length; i2++) { offs[i2] = acc2; acc2 += bytecode[i2].GetSize(asset); }

int IdxOf(uint t) { for (var i2 = 0; i2 < offs.Length; i2++) if (offs[i2] >= t) return i2; return offs.Length; }

var leaders = new SortedSet<int> { 0 };
for (var i2 = 0; i2 < bytecode.Length; i2++)
{
    switch (bytecode[i2])
    {
        case EX_Jump j2:
            leaders.Add(IdxOf(j2.CodeOffset));
            leaders.Add(i2 + 1);
            break;
        case EX_JumpIfNot jn2:
            leaders.Add(IdxOf(jn2.CodeOffset));
            leaders.Add(i2 + 1);
            break;
    }
}

Console.WriteLine();
Console.WriteLine($"=== 基本块（{leaders.Count} 个 leader）===");
var leadList = leaders.Where(x => x <= bytecode.Length).ToArray();
for (var i2 = 0; i2 < leadList.Length; i2++)
{
    var from = leadList[i2];
    var to = i2 + 1 < leadList.Length ? leadList[i2 + 1] : bytecode.Length;
    if (from >= bytecode.Length) continue;
    var last = bytecode[to - 1];
    Console.WriteLine($"  [{from,3}..{to - 1,3}] off={offs[from],-5} 结束于 {last.Inst}");
}

// ---- 2. 反编译 ----
var options = new DecompileOptions(
    Measure: e => e.GetSize(asset),
    ResolveImport: idx => importNames.TryGetValue(idx?.Index ?? 0, out var n) ? n : null,
    Warn: m => Console.WriteLine("[warn] " + m),
    Flatten: Environment.GetEnvironmentVariable("KISMET_FLATTEN") == "1");

var nodes = KismetDecompiler.Decompile(bytecode, options);
Console.WriteLine();
Console.WriteLine($"=== 反编译（{nodes.Count} 个节点）===");
foreach (var node in nodes)
{
    Console.WriteLine("  " + Describe(node, 1));
}

// ---- 3. 回编译比对 ----
var variables = ExpressionWalker.BuildVariableIndex(bytecode);
var funcs = ImportIndex.BuildFunctionIndex(asset);

var regenerated = KismetCompiler.CompileToExpressions(
    nodes, variables, funcs,
    e => e.GetSize(asset),
    n => new FName(asset, n));

Console.WriteLine();
Console.WriteLine($"=== 回编译：{regenerated.Count} 条（原始 {bytecode.Length} 条）===");

var originalBytes = Serialize(bytecode, asset);
var regeneratedBytes = Serialize(regenerated, asset);

Console.WriteLine($"  原始字节 {originalBytes.Length}");
Console.WriteLine($"  回编译字节 {regeneratedBytes.Length}");
var same = originalBytes.AsSpan().SequenceEqual(regeneratedBytes);
Console.WriteLine($"  逐字节一致: {same}");

if (!same)
{
    KismetProbe.StructuralDiff.Report(bytecode, regenerated, asset);
}

Directory.Delete(work, true);
return same ? 0 : 1;

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

static string Describe(KNode node, int depth)
{
    var pad = new string(' ', depth * 2);
    return node switch
    {
        AssignNode a => $"{pad}Set {a.VariableName} = {Arg(a.Value)}",
        AssignBoolNode b => $"{pad}Set {b.VariableName} = {Arg(b.Value)}",
        CallNode c => $"{pad}Call {c.Function}({string.Join(", ", c.Args.Select(Arg))})",
        ContextCallNode cc => $"{pad}{cc.ObjectVariable}.{cc.Function}({string.Join(", ", cc.Args.Select(Arg))})",
        VirtualCallNode v => $"{pad}Virtual {v.Function}({string.Join(", ", v.Args.Select(Arg))})",
        IfNode f => $"{pad}If ({Arg(f.Condition)})\n" + string.Join("\n", f.Then.Select(n => Describe(n, depth + 1))),
        LoopNode l => $"{pad}Loop ({Arg(l.Condition)})\n" + string.Join("\n", l.Body.Select(n => Describe(n, depth + 1))),
        ReturnNode => $"{pad}Return",
        _ => $"{pad}{node.Title}"
    };
}

static string Arg(KArg a) => a switch
{
    IntArg i => i.Value.ToString(),
    BoolArg b => b.Value ? "true" : "false",
    PointerArg p => p.Name,
    LocalArg l => l.Name,
    CallArg c => $"{c.Function}(...)",
    _ => "?"
};
