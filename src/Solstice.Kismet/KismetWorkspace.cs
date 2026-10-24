using System.Text.Json;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Solstice.Kismet;

/// <summary>
/// 浏览器/CLI 共用的 Kismet 工作区。
///
/// <para>
/// UAssetAPI 的 <c>UAsset</c> 与 <c>Usmap</c> 只能按路径构造，所以这里先把字节写到
/// 工作目录（Blazor WASM 下就是内存虚拟文件系统），再按路径打开。
/// 实测可行：<c>File.WriteAllBytes</c> + <c>new Usmap(path)</c>、
/// 以及 <c>uasset+uexp</c> 拼接流都能成功。
/// </para>
/// </summary>
public sealed class KismetWorkspace
{
    private readonly UAsset _asset;
    private readonly Usmap _usmap;

    private KismetWorkspace(UAsset asset, Usmap usmap)
    {
        _asset = asset;
        _usmap = usmap;
    }

    /// <summary>把上传的三份字节落到工作目录并打开资产。</summary>
    public static KismetWorkspace Open(byte[] uasset, byte[] uexp, byte[] usmap, string workDir)
    {
        Directory.CreateDirectory(workDir);

        var assetPath = Path.Combine(workDir, "a.uasset");
        File.WriteAllBytes(assetPath, uasset);
        File.WriteAllBytes(Path.ChangeExtension(assetPath, ".uexp"), uexp);

        var usmapPath = Path.Combine(workDir, "kards.usmap");
        File.WriteAllBytes(usmapPath, usmap);

        // 注意：Usmap 内部用 File.Open（默认 ReadWrite），文件必须可写
        var usmapObj = new Usmap(usmapPath);
        var assetObj = new UAsset(assetPath, EngineVersion.VER_UE5_6, usmapObj);

        return new KismetWorkspace(assetObj, usmapObj);
    }

    /// <summary>带字节码的函数名。</summary>
    public IReadOnlyList<string> Functions => _asset.Exports
        .OfType<FunctionExport>()
        .Where(x => x.ScriptBytecode is { Length: > 0 })
        .Select(x => x.ObjectName.ToString())
        .ToArray();

    /// <summary>某个函数里出现过的变量名（来自字节码里的属性指针）。</summary>
    public IReadOnlyList<string> Variables(string functionName)
    {
        var fn = FindFunction(functionName);
        return fn is null
            ? []
            : ExpressionWalker.BuildVariableIndex(fn.ScriptBytecode!).Keys.ToArray();
    }

    /// <summary>某个函数当前的表达式条数（用于「改前/改后」对比）。</summary>
    public int ExpressionCount(string functionName)
        => FindFunction(functionName)?.ScriptBytecode?.Length ?? 0;

    /// <summary>可调用的函数导入（名字 → 索引），供 UI 提示用。</summary>
    public IReadOnlyDictionary<string, FPackageIndex> FunctionImports => ImportIndex.BuildFunctionIndex(_asset);

    /// <summary>函数签名目录（可选，由 jmap 函数表载入）。</summary>
    public FunctionCatalog? Catalog { get; private set; }

    public void AttachCatalog(FunctionCatalog catalog) => Catalog = catalog;

    /// <summary>
    /// 把函数导入解析成 owner 完整路径，例如 <c>/Script/Engine.KismetSystemLibrary</c>。
    /// 链：Function → Class → Package。
    /// </summary>
    public string? FunctionOwnerPath(string functionName)
    {
        for (var i = 0; i < _asset.Imports.Count; i++)
        {
            var im = _asset.Imports[i];
            if (im.ClassName?.ToString() != "Function" || im.ObjectName?.ToString() != functionName)
            {
                continue;
            }

            var outer = im.OuterIndex?.Index ?? 0;
            if (outer >= 0)
            {
                return null;
            }

            var cls = _asset.Imports[-outer - 1];
            var clsOuter = cls.OuterIndex?.Index ?? 0;
            if (clsOuter >= 0)
            {
                return $"/Script/{cls.ObjectName}";
            }

            var pkg = _asset.Imports[-clsOuter - 1];
            return $"{pkg.ObjectName}.{cls.ObjectName}";
        }

        return null;
    }

    /// <summary>查某个函数导入的签名（需要先 AttachCatalog）。</summary>
    public FunctionSignature? SignatureOf(string functionName)
        => Catalog?.Find(FunctionOwnerPath(functionName), functionName);

    /// <summary>
    /// 把一个函数反编译成节点。
    ///
    /// <para>
    /// 结果一定是三种可读表示之一：树形结构化 / ubergraph 展开 / 基本块图；
    /// 且**已用字节比对校验过**——校验不通过会逐级退让，最终形态仍与原字节一致。
    /// </para>
    /// </summary>
    public IReadOnlyList<KNode> Decompile(string functionName)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var bytecode = fn.ScriptBytecode!;
        var options = new DecompileOptions(
            Measure: e => e.GetSize(_asset),
            ResolveImport: idx => Names.TryGetValue(idx?.Index ?? 0, out var n) ? n : null);

        var original = SerializeBytecode(bytecode);
        return KismetDecompiler.DecompileVerified(
            bytecode, original, options,
            ns => SerializeBytecode(KismetCompiler.CompileToExpressions(ns, ContextOf(fn))));
    }

    /// <summary>
    /// 重新烘焙 ubergraph 并返回新的入口偏移表。
    /// 非 ubergraph 函数返回 null。
    /// </summary>
    public KismetCompiler.UbergraphBakeResult? BakeUbergraph(string functionName)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        return KismetCompiler.BakeUbergraph(Decompile(functionName), ContextOf(fn));
    }

    private CompileContext ContextOf(FunctionExport fn) => new(
        ExpressionWalker.BuildVariableIndex(fn.ScriptBytecode!),
        ImportIndex.BuildFunctionIndex(_asset),
        e => e.GetSize(_asset),
        n => new FName(_asset, n));

    private byte[] SerializeBytecode(IReadOnlyList<KismetExpression> expressions)
    {
        using var ms = new MemoryStream();
        var writer = new AssetBinaryWriter(ms, _asset);
        foreach (var expression in expressions)
        {
            ExpressionSerializer.WriteExpression(expression, writer);
        }

        return ms.ToArray();
    }

    private Dictionary<int, string>? _names;

    /// <summary>包索引 → 名字。负数为 Import，正数为本包 Export（ubergraph 段名靠它）。</summary>
    private Dictionary<int, string> Names
    {
        get
        {
            if (_names is not null)
            {
                return _names;
            }

            _names = new Dictionary<int, string>();
            for (var i = 0; i < _asset.Imports.Count; i++)
            {
                _names[-(i + 1)] = _asset.Imports[i].ObjectName?.ToString() ?? "";
            }

            for (var i = 0; i < _asset.Exports.Count; i++)
            {
                _names[i + 1] = _asset.Exports[i].ObjectName?.ToString() ?? "";
            }

            return _names;
        }
    }

    /// <summary>
    /// 按节点 JSON 生成字节码并写回函数。
    /// 返回生成结果的指令名序列与序列化后的十六进制。
    /// </summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) Generate(string json, string functionName)
    {
        List<KNode> nodes;
        try
        {
            nodes = ParseNodes(json);
        }
        catch (JsonException ex)
        {
            // 把收到的原文带进异常，否则只看到行列号无法定位
            throw new InvalidOperationException(
                $"节点 JSON 解析失败：{ex.Message}\n收到的内容：{Preview(json)}", ex);
        }

        return Generate(nodes, functionName);
    }

    private static string Preview(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "(空字符串)";
        }

        var shown = s.Length <= 400 ? s : s[..400] + "…";
        return shown.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    /// <summary>
    /// 只编译、不写回函数。用于编辑时的实时预览。
    /// 注意：仍会按需补 Import 条目（幂等）。
    /// </summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) Preview(
        IReadOnlyList<KNode> nodes, string functionName)
        => Compile(nodes, functionName, assign: false);

    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) Generate(
        IReadOnlyList<KNode> nodes, string functionName)
        => Compile(nodes, functionName, assign: true);

    private (IReadOnlyList<string> Opcodes, string Hex, int Bytes) Compile(
        IReadOnlyList<KNode> nodes, string functionName, bool assign)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");
        var variables = ExpressionWalker.BuildVariableIndex(fn.ScriptBytecode!);

        // 调用目标若还没出现在 Import 表里，先补一条（克隆现有函数导入的结构）
        foreach (var (name, owner) in CollectCallTargets(nodes))
        {
            ImportIndex.EnsureFunction(_asset, name, owner);
        }

        var functions = ImportIndex.BuildFunctionIndex(_asset);

        var exprs = KismetCompiler.CompileToExpressions(
            nodes,
            new CompileContext(
                Variables: variables,
                Functions: functions,
                Measure: e => e.GetSize(_asset),
                ResolveName: n => new FName(_asset, n)));

        if (assign)
        {
            fn.ScriptBytecode = exprs.ToArray();
        }

        var ms = new MemoryStream();
        var writer = new AssetBinaryWriter(ms, _asset);
        foreach (var e in exprs)
        {
            ExpressionSerializer.WriteExpression(e, writer);
        }

        var bytes = ms.ToArray();
        return (exprs.Select(x => x.Inst).ToArray(), Convert.ToHexString(bytes), bytes.Length);
    }

    /// <summary>写回结果：UE5 资产是 uasset + uexp 两个文件，缺一不可。</summary>
    public sealed record SavedAsset(byte[] UAsset, byte[] UExp);

    /// <summary>
    /// 把改过的资产写回磁盘并返回**两半**字节。
    ///
    /// <para>
    /// 注意：只取 <c>.uasset</c> 而不取 <c>.uexp</c> 会得到一对不匹配的文件，
    /// 重新加载时导出会大面积丢失（实测从 21 个掉到 1 个）。
    /// </para>
    /// </summary>
    public SavedAsset Save(string workDir, string fileName = "out.uasset")
    {
        var outPath = Path.Combine(workDir, fileName);
        _asset.Write(outPath);

        var uexpPath = Path.ChangeExtension(outPath, ".uexp");
        return new SavedAsset(
            File.ReadAllBytes(outPath),
            File.Exists(uexpPath) ? File.ReadAllBytes(uexpPath) : []);
    }

    private FunctionExport? FindFunction(string name) => _asset.Exports
        .OfType<FunctionExport>()
        .FirstOrDefault(x => x.ObjectName.ToString() == name);

    /// <summary>递归收集节点里所有「按 Import 索引解析」的调用目标。</summary>
    private static IEnumerable<(string Name, string? Owner)> CollectCallTargets(IEnumerable<KNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case CallNode c:
                    yield return (c.Function, c.Owner);
                    break;

                case ContextCallNode cc:
                    yield return (cc.Function, cc.Owner);
                    break;

                case IfNode f:
                    foreach (var t in CollectCallTargets(f.Then))
                    {
                        yield return t;
                    }

                    break;

                case LoopNode lp:
                    foreach (var b in CollectCallTargets(lp.Body))
                    {
                        yield return b;
                    }

                    break;
            }
        }
    }

    /// <summary>解析节点 JSON（与 KismetGen 的 schema 一致）。</summary>
    public static List<KNode> ParseNodes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseList(doc.RootElement.GetProperty("nodes"));
    }

    private static List<KNode> ParseList(JsonElement array)
    {
        var nodes = new List<KNode>();

        foreach (var n in array.EnumerateArray())
        {
            var kind = n.GetProperty("kind").GetString();

            switch (kind)
            {
                case "assign":
                    nodes.Add(new AssignNode(n.GetProperty("var").GetString()!, new IntArg(n.GetProperty("value").GetInt32())));
                    break;

                case "assign-bool":
                    nodes.Add(new AssignBoolNode(n.GetProperty("var").GetString()!, new BoolArg(n.GetProperty("value").GetBoolean())));
                    break;

                case "call":
                    nodes.Add(new CallNode(n.GetProperty("function").GetString()!, ParseArgs(n), ReadOwner(n)));
                    break;

                case "context-call":
                    nodes.Add(new ContextCallNode(
                        n.GetProperty("object").GetString()!,
                        n.GetProperty("function").GetString()!,
                        ParseArgs(n),
                        ReadOwner(n)));
                    break;

                case "virtual-call":
                    nodes.Add(new VirtualCallNode(n.GetProperty("function").GetString()!, ParseArgs(n)));
                    break;

                case "if":
                    nodes.Add(new IfNode(
                        ParseCond(n.GetProperty("cond")),
                        n.TryGetProperty("then", out var t) ? ParseList(t) : []));
                    break;

                case "loop":
                    nodes.Add(new LoopNode(
                        ParseCond(n.GetProperty("cond")),
                        n.TryGetProperty("body", out var b) ? ParseList(b) : []));
                    break;

                case "return":
                    nodes.Add(new ReturnNode());
                    break;
            }
        }

        return nodes;
    }

    private static List<KArg> ParseArgs(JsonElement n)
    {
        var args = new List<KArg>();
        if (!n.TryGetProperty("args", out var arr))
        {
            return args;
        }

        foreach (var x in arr.EnumerateArray())
        {
            if (x.TryGetProperty("int", out var iv))
            {
                args.Add(new IntArg(iv.GetInt32()));
            }
            else if (x.TryGetProperty("var", out var vv))
            {
                args.Add(new LocalArg(vv.GetString()!));
            }
        }

        return args;
    }

    private static string? ReadOwner(JsonElement n)
        => n.TryGetProperty("owner", out var o) ? o.GetString() : null;

    private static KArg ParseCond(JsonElement cond)
        => cond.TryGetProperty("int", out var iv)
            ? new IntArg(iv.GetInt32())
            : new LocalArg(cond.GetProperty("var").GetString()!);
}
