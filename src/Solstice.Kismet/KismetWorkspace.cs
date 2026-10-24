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

    /// <summary>对象表（可选，由 jmap 的 objects.json 载入），用于核对归属类。</summary>
    public ObjectCatalog? Objects { get; private set; }

    public void AttachObjects(ObjectCatalog objects) => Objects = objects;

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
    /// 解析一个函数引用（推荐用它代替「只传名字」）。
    ///
    /// <para>
    /// 顺序：① 给了 <paramref name="ownerPath"/> 就按 <c>(名字, 归属)</c> 精确查；
    /// ② 否则找本资产导出的同名函数；③ 再找 Import 表——**同名多个且归属不同时直接报错**
    /// （把候选列出来，让人选，而不是悄悄取第一个）；④ 都没有才按 jmap 签名补导入。
    /// </para>
    /// </summary>
    /// <param name="ensureImport">
    /// 为 true 时，若函数还不在 Import 表里就补一条（需要 jmap 签名提供归属）。
    /// 设为 false 则只解析、不改资产。
    /// </param>
    public FunctionRef? ResolveFunction(string functionName, string? ownerPath = null, bool ensureImport = true)
    {
        var byOwner = FunctionImportOwners();

        // ① 精确匹配
        if (!string.IsNullOrWhiteSpace(ownerPath))
        {
            return byOwner.TryGetValue(new FunctionKey(functionName, ownerPath), out var exact)
                ? new FunctionRef(functionName, ownerPath, exact, false, Catalog?.Find(ownerPath, functionName))
                : Ensure(functionName, ownerPath, ensureImport);
        }

        // ② 本资产导出
        for (var i = 0; i < _asset.Exports.Count; i++)
        {
            if (_asset.Exports[i] is FunctionExport fn && fn.ObjectName?.ToString() == functionName)
            {
                return new FunctionRef(functionName, "", new FPackageIndex(i + 1), true, Catalog?.Find(null, functionName));
            }
        }

        // ③ Import 表：唯一才接受
        var candidates = byOwner.Where(kv => kv.Key.Name == functionName).ToArray();
        if (candidates.Length == 1)
        {
            var only = candidates[0];
            return new FunctionRef(functionName, only.Key.Owner, only.Value, false, Catalog?.Find(only.Key.Owner, functionName));
        }

        if (candidates.Length > 1)
        {
            var listed = string.Join("\n  ", candidates.Select(c => c.Key.Owner));
            throw new InvalidOperationException(
                $"{functionName} 在 Import 表里有 {candidates.Length} 个归属，无法只凭名字确定：\n  {listed}\n" +
                "请用 ResolveFunction(name, ownerPath) 指定归属。");
        }

        // ④ 靠 jmap 的归属补导入
        var signature = Catalog?.Find(null, functionName)
                        ?? throw new InvalidOperationException(
                            $"没有 {functionName} 的签名（先 AttachCatalog 载入 jmap functions.json）——不猜归属");

        return Ensure(functionName, signature.OwnerPath, ensureImport);

        FunctionRef? Ensure(string name, string owner, bool doIt)
        {
            var signature = Catalog?.Find(owner, name);
            if (!doIt)
            {
                return new FunctionRef(name, owner, new FPackageIndex(0), false, signature);
            }

            if (Objects is not null
                && Objects.Find(owner) is { } info
                && !string.Equals(info.Type, "Class", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{owner} 在对象表里是 {info.Type}，不是类——拒绝按它补导入");
            }

            var index = ImportIndex.EnsureFunction(_asset, name, owner);
            return new FunctionRef(name, owner, index, false, signature);
        }
    }

    /// <summary>
    /// 按 jmap 签名选调用形态——本资产导出 → <see cref="VirtualCallNode"/>、
    /// <c>FUNC_Static</c> → <see cref="CallNode"/>、其余 → <see cref="ContextCallNode"/>。
    ///
    /// <para>
    /// 没有签名表时**直接报错**，不猜形态——形态猜错会让字节看起来对、运行时行为错。
    /// </para>
    /// </summary>
    /// <param name="args">参数；给 <c>null</c> 时按签名自动生成（见 <see cref="DefaultArgs"/>）。</param>
    /// <param name="self">非静态函数的调用对象；不给则用 <c>this</c>。</param>
    public KNode CreateCall(FunctionRef function, IReadOnlyList<KArg>? args = null, KArg? self = null)
    {
        if (function.IsExported)
        {
            return new VirtualCallNode(function.Name, args ?? []);
        }

        var signature = function.Signature
                        ?? Catalog?.Find(function.OwnerPath, function.Name)
                        ?? throw new InvalidOperationException(
                            $"没有 {function.FullPath} 的签名——不猜调用形态（先 AttachCatalog）");

        args ??= DefaultArgs(function.Name, function.OwnerPath).Select(a => a.Value).ToArray();

        // 引用里的索引为 0 说明只解析没落表，这里补上
        var index = function.Index.Index != 0
            ? function.Index
            : ImportIndex.EnsureFunction(_asset, function.Name, function.OwnerPath);

        if (signature.IsStatic)
        {
            return new CallNode(function.Name, args, function.OwnerPath, index);
        }

        return new ContextCallNode(
            "this",
            function.Name,
            args,
            Owner: function.OwnerPath,
            ObjectArg: self ?? new SelfArg(),
            InnerShape: CallShape.Final,
            StackNode: index);
    }

    /// <summary>按名字新建调用（内部先 <see cref="ResolveFunction"/>，有歧义会报错）。</summary>
    public KNode CreateCall(string functionName, IReadOnlyList<KArg>? args = null, KArg? self = null)
    {
        var function = ResolveFunction(functionName)
                       ?? throw new InvalidOperationException($"找不到函数 {functionName}");

        return CreateCall(function, args, self);
    }

    /// <summary>
    /// 读-改-写一条龙：反编译 → 交给 <paramref name="transform"/> 改 → 编译写回。
    ///
    /// <para>
    /// SDK 调用方最常用的一步就是「打开函数、改几个节点、写回去」，
    /// 这个方法把三次调用压成一次，并且反编译那一步已经过字节校验。
    /// </para>
    /// </summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) Edit(
        string functionName,
        Func<IReadOnlyList<KNode>, IReadOnlyList<KNode>> transform)
    {
        var nodes = Decompile(functionName);
        return Generate(transform(nodes), functionName);
    }

    /// <summary>
    /// 在函数末尾（Return 之前）插入一次调用。
    ///
    /// <para>
    /// 不给 <paramref name="args"/> 时按 jmap 签名自动生成占位参数；
    /// 形态、Import 补齐、字节编码都由 <see cref="CreateCall"/> 负责。
    /// </para>
    /// </summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) InsertCall(
        string functionName,
        string calleeName,
        params KArg[] args)
        => InsertCall(functionName, ResolveFunction(calleeName)
            ?? throw new InvalidOperationException($"找不到函数 {calleeName}"), args);

    /// <summary>按引用插入调用（推荐：无歧义）。</summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) InsertCall(
        string functionName,
        FunctionRef callee,
        params KArg[] args)
    {
        var call = CreateCall(callee, args.Length > 0 ? args : null);

        return Edit(functionName, nodes =>
        {
            var list = nodes.ToList();
            var at = list.Count > 0 && list[^1] is ReturnNode ? list.Count - 1 : list.Count;
            list.Insert(at, call);
            return list;
        });
    }

    /// <summary>一次调用的一个引脚（编辑器 UI 渲染用）。</summary>
    public sealed record CallPin(
        string Name,
        string Type,
        string Dir,
        string ClassName,
        string EnumName,
        KArg Value,
        bool IsPlaceholder,
        bool IsOutput);

    /// <summary>一次待插入调用的完整描述：形态、归属、以及全部引脚。</summary>
    public sealed record CallPlan(
        string Function,
        string OwnerPath,
        string Flags,
        CallShape Shape,
        IReadOnlyList<CallPin> Pins);

    /// <summary>
    /// 生成一次调用的「计划」：形态怎么选、每个引脚是什么、哪些参数只是占位。
    ///
    /// <para>
    /// 编辑器 UI 靠它渲染插入节点：输入引脚标成可填，占位引脚标成待用户替换，
    /// 输出引脚（return / out）只读。实际插入仍走 <see cref="CreateCall"/>。
    /// </para>
    /// </summary>
    public CallPlan PlanCall(string functionName, IReadOnlyList<KArg>? args = null)
        => PlanCall(ResolveFunction(functionName, ensureImport: false), args);

    /// <summary>按引用生成调用计划（推荐：无歧义）。</summary>
    public CallPlan PlanCall(FunctionRef? function, IReadOnlyList<KArg>? args = null)
    {
        if (function is null)
        {
            throw new InvalidOperationException("函数引用为空");
        }

        var signature = function.Signature
                        ?? Catalog?.Find(function.OwnerPath, function.Name)
                        ?? throw new InvalidOperationException(
                            $"没有 {function.FullPath} 的签名（先 AttachCatalog 载入 jmap functions.json）");

        var defaults = DefaultArgs(function.Name, function.OwnerPath);
        var shape = function.IsExported
            ? CallShape.Virtual
            : signature.IsStatic ? CallShape.Final : CallShape.Context;

        var pins = new List<CallPin>();
        var inputIndex = 0;

        foreach (var param in signature.Params)
        {
            var isInput = string.Equals(param.Dir, "in", StringComparison.OrdinalIgnoreCase);
            if (isInput)
            {
                var arg = args is { Count: > 0 } && inputIndex < args.Count
                    ? args[inputIndex]
                    : defaults[inputIndex].Value;
                var placeholder = defaults[inputIndex].IsPlaceholder && (args is null || inputIndex >= args.Count);

                pins.Add(new CallPin(param.Name, param.Type, param.Dir, param.Class, param.Enum, arg, placeholder, false));
                inputIndex++;
            }
            else
            {
                // return / out：没有值可填，只描述类型
                pins.Add(new CallPin(param.Name, param.Type, param.Dir, param.Class, param.Enum, new NoObjectArg(), false, true));
            }
        }

        return new CallPlan(function.FullPath, signature.OwnerPath, signature.Flags, shape, pins);
    }

    /// <summary>函数导入的身份：名字 + 归属类全路径。同名函数挂在不同类下必须区分开。</summary>
    public readonly record struct FunctionKey(string Name, string Owner);

    /// <summary>
    /// Import 表里的函数导入，按 <c>(名字, 归属类)</c> 索引。
    ///
    /// <para>
    /// 归属类由导入链推出：Function 的 outer 是 Class，Class 的 outer 是 Package，
    /// 拼成 <c>/Script/Engine.KismetMathLibrary</c> 这种全路径。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<FunctionKey, FPackageIndex> FunctionImportOwners()
    {
        var map = new Dictionary<FunctionKey, FPackageIndex>();

        for (var i = 0; i < _asset.Imports.Count; i++)
        {
            var entry = _asset.Imports[i];
            if (entry.ClassName?.ToString() != "Function")
            {
                continue;
            }

            var name = entry.ObjectName?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            map.TryAdd(new FunctionKey(name, OwnerPathOf(entry.OuterIndex)), new FPackageIndex(-(i + 1)));
        }

        return map;
    }

    /// <summary>由导入链拼出归属类的全路径（如 <c>/Script/Engine.KismetMathLibrary</c>）。</summary>
    public string OwnerPathOf(FPackageIndex? index)
    {
        var raw = index?.Index ?? 0;
        if (raw == 0)
        {
            return "";
        }

        if (raw > 0)
        {
            return raw <= _asset.Exports.Count
                ? _asset.Exports[raw - 1].ObjectName?.ToString() ?? ""
                : "";
        }

        var cursor = -raw - 1;
        if (cursor < 0 || cursor >= _asset.Imports.Count)
        {
            return "";
        }

        var classImport = _asset.Imports[cursor];
        var className = classImport.ObjectName?.ToString() ?? "";
        var outer = classImport.OuterIndex?.Index ?? 0;

        if (outer < 0 && -outer - 1 < _asset.Imports.Count)
        {
            var package = _asset.Imports[-outer - 1].ObjectName?.ToString() ?? "";
            return $"{package}.{className}";
        }

        return className;
    }

    /// <summary>当前 Import 表条目数（新增导入前后对比用）。</summary>
    public int ImportCount => _asset.Imports.Count;

    /// <summary>
    /// 打印某个函数导入及其 outer 链（调试/审计用）。
    ///
    /// <para>
    /// 用来核对「新增的导入到底是克隆来的，还是按约定合成出来的」——
    /// 合成条目的 <c>ClassPackage</c> 固定是 <c>/Script/CoreUObject</c>。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DescribeImports(string functionName)
    {
        var lines = new List<string>();

        for (var i = 0; i < _asset.Imports.Count; i++)
        {
            var entry = _asset.Imports[i];
            if (entry.ObjectName?.ToString() != functionName)
            {
                continue;
            }

            var chain = new List<string>();
            var cursor = i;
            var guard = 0;

            while (cursor >= 0 && cursor < _asset.Imports.Count && guard++ < 8)
            {
                var node = _asset.Imports[cursor];
                chain.Add(
                    $"{-(cursor + 1)}:{node.ClassName} '{node.ObjectName}' " +
                    $"[clsPkg={node.ClassPackage} pkgName={node.PackageName} outer={node.OuterIndex?.Index}]");

                var outer = node.OuterIndex?.Index ?? 0;
                cursor = outer < 0 ? -outer - 1 : -1;
            }

            lines.Add($"导入 idx={-(i + 1)} 链: " + string.Join(" -> ", chain));
        }

        return lines;
    }

    /// <summary>自动生成的参数：值本身 + 它是不是「占位」（无法忠实合成的类型）。</summary>
    public sealed record DefaultArg(string Name, string Type, KArg Value, bool IsPlaceholder);

    /// <summary>
    /// 按 jmap 签名给一次调用生成占位参数（编辑器插入节点时用）。
    ///
    /// <para>
    /// 17 种 Property 类型：**能忠实合成**的走真实常量（bool/int/byte/float/double/string/
    /// name/object/class/delegate）；**需要真实载荷**的（text/struct/array/map/set）用
    /// <see cref="NoObjectArg"/> 占位并标记 <c>IsPlaceholder</c>，调用方必须替换。
    /// </para>
    /// </summary>
    public IReadOnlyList<DefaultArg> DefaultArgs(string functionName, string? ownerPath = null)
    {
        var signature = Catalog?.Find(ownerPath, functionName)
                        ?? throw new InvalidOperationException(
                            $"没有 {functionName} 的签名（先 AttachCatalog 载入 jmap functions.json）");

        return signature.Inputs.Select(MakeDefault).ToArray();
    }

    private DefaultArg MakeDefault(FunctionParam param) => param.Type switch
    {
        "BoolProperty" => new DefaultArg(param.Name, param.Type, new BoolArg(false), false),
        "IntProperty" => new DefaultArg(param.Name, param.Type, new IntArg(0), false),
        "ByteProperty" or "EnumProperty" => new DefaultArg(param.Name, param.Type, new ByteArg(0), false),
        "FloatProperty" => new DefaultArg(param.Name, param.Type, new FloatArg(0f), false),
        "DoubleProperty" => new DefaultArg(param.Name, param.Type, new DoubleArg(0d), false),
        "StrProperty" => new DefaultArg(param.Name, param.Type, new StringArg(""), false),
        // FName 的默认值是 None；空字符串不能进名字表（AddNameReference 会抛）
        "NameProperty" => new DefaultArg(param.Name, param.Type, new NameArg(new FName(_asset, "None")), false),
        "ObjectProperty" or "ClassProperty" or "DelegateProperty" or "SoftObjectProperty"
            => new DefaultArg(param.Name, param.Type, new NoObjectArg(), false),

        // text / struct / array / map / set：需要真实载荷，只能占位
        _ => new DefaultArg(param.Name, param.Type, new NoObjectArg(), true)
    };

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
    private IEnumerable<(string Name, string? Owner)> CollectCallTargets(IEnumerable<KNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case CallNode c:
                    yield return (c.Function, c.Owner ?? Catalog?.Find(null, c.Function)?.OwnerPath);
                    break;

                case ContextCallNode cc:
                    yield return (cc.Function, cc.Owner ?? Catalog?.Find(null, cc.Function)?.OwnerPath);
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
