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

    /// <summary>函数名 → 首次编辑前的字节码快照。ubergraph 的入口点校验要靠它当基准。</summary>
    private readonly Dictionary<string, KismetExpression[]> _originalBytecode = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 某个函数里出现过的变量名（**短名**，即 FFieldPath 最后一段）。
    ///
    /// <para>
    /// 节点模型里的变量名一律是短名（见 <c>KismetDecompiler.TargetName</c>），
    /// 所以这里也返回短名；要看完整路径与 ResolvedOwner 用 <see cref="DescribeVariables"/>。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Variables(string functionName)
    {
        var fn = FindFunction(functionName);
        if (fn is null)
        {
            return [];
        }

        return ExpressionWalker.CollectVariables(fn.ScriptBytecode!)
            .Select(v => v.Variable?.New?.Path is { Length: > 0 } path ? path[^1].ToString() : null)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray()!;
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

    /// <summary>usmap 结构体 schema（可选），用于解析结构体成员的类型。</summary>
    public UsmapCatalog? Usmap { get; private set; }

    public void AttachUsmap(UsmapCatalog usmap) => Usmap = usmap;

    /// <summary>
    /// 解析函数里所有 <c>EX_StructMemberContext</c> 的结构体与成员类型。
    ///
    /// <para>
    /// 字节码只带「成员字段路径 + 归属索引」，类型要靠 usmap 的 schema 查。
    /// <b>注意</b>：usmap 只有原生/脚本结构；蓝图资产定义的结构（<c>S_*</c>）不在其中，
    /// 这类会显示为「不在 usmap」。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DescribeStructMembers(string functionName)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var lines = new List<string>();

        foreach (var expr in ExpressionWalker.Walk(fn.ScriptBytecode!))
        {
            if (expr is not EX_StructMemberContext sm)
            {
                continue;
            }

            var pointer = sm.StructMemberExpression;
            var path = pointer?.New?.Path;
            var memberName = path is { Length: > 0 } ? path[^1].ToString() : "?";
            var structPath = OwnerPathOf(pointer?.New?.ResolvedOwner);

            if (Usmap is null)
            {
                lines.Add($"{memberName,-24} struct={structPath}（未载入 usmap，无法判定类型）");
                continue;
            }

            string type;
            try
            {
                var resolved = Usmap.ResolveSchema(structPath);
                var member = Usmap.FindMember(resolved, memberName);
                type = member is null ? "（结构里没有这个成员）" : member.TypeName;
                structPath = resolved;
            }
            catch (Exception ex)
            {
                type = ex.Message.Contains("没有名为") ? "（不在 usmap：多为蓝图资产结构）" : ex.Message;
            }

            lines.Add($"{memberName,-24} {type,-20} struct={structPath}");
        }

        return lines.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

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
        // 归属允许写「类短名」：objects.json 能把它解析成全路径（唯一才接受，多个会列出候选）
        if (!string.IsNullOrWhiteSpace(ownerPath) && Objects is not null && !ownerPath.Contains('/'))
        {
            ownerPath = Objects.ResolveClass(ownerPath);
        }

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

        // 把索引写回引用，节点直接持有它——不再留「名字 + 可选 StackNode」两处真相
        var bound = function with { Index = index };

        if (signature.IsStatic)
        {
            return new CallNode(bound, args);
        }

        return new ContextCallNode(
            "this",
            bound,
            args,
            ObjectArg: self ?? new SelfArg(),
            InnerShape: CallShape.Final);
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

    /// <summary>
    /// 打印函数里出现过的变量表达式及其属性指针（诊断用）。
    ///
    /// <para>
    /// 编译器生成的临时变量（<c>CallFunc_XXX_ReturnValue</c>）就是这里的
    /// <c>LocalVariable</c>：路径段数、ResolvedOwner 都能看到。反编译保留它们、
    /// 回编译原样发回，所以不需要「临时变量分配」逻辑。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DescribeVariables(string functionName)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var lines = new List<string>();
        foreach (var variable in ExpressionWalker.CollectVariables(fn.ScriptBytecode!))
        {
            var pointer = variable.Variable;
            var path = pointer?.New?.Path;
            var names = path is null ? "" : string.Join(".", path.Select(p => p.ToString()));
            var owner = pointer?.New?.ResolvedOwner?.Index ?? pointer?.Old?.Index ?? 0;
            lines.Add($"{variable.GetType().Name,-22} path=[{names}] resolvedOwner={owner}");
        }

        return lines.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 取（或构造）一个**局部变量**的指针：<c>FFieldPath{ [名字], ResolvedOwner = 该函数导出 }</c>。
    ///
    /// <para>
    /// 这正是 UE 给 <c>CallFunc_XXX_ReturnValue</c> 这类临时变量用的形态
    /// （实测 <c>path=[CallFunc_GetChat_chat] resolvedOwner=23</c>，23 就是函数导出索引）。
    /// 名字已存在就直接复用原指针，不存在则按同一形态构造一个。
    /// </para>
    /// </summary>
    public PointerArg EnsureLocalVariable(string functionName, string variableName)
        => EnsureVariable(functionName, variableName, instance: false);

    /// <summary>
    /// 取（或构造）变量指针。<paramref name="instance"/> 为 true 时构造**成员变量**
    /// （<c>ResolvedOwner</c> 指向本类导出，实测为 1）；否则构造局部变量
    /// （<c>ResolvedOwner</c> 指向该函数导出）。
    ///
    /// <para>
    /// 名字已在字节码里出现过就复用原指针（连同它的形态：局部 / 成员 / out / CDO 默认值）。
    /// </para>
    /// </summary>
    public PointerArg EnsureVariable(string functionName, string variableName, bool instance)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var existing = ExpressionWalker.BuildVariableIndex(fn.ScriptBytecode!);
        if (existing.TryGetValue(variableName, out var pointer))
        {
            var kind = ExpressionWalker.CollectVariables(fn.ScriptBytecode!)
                .FirstOrDefault(v => v.Variable?.New?.Path is { Length: > 0 } p
                                     && string.Equals(p[^1].ToString(), variableName, StringComparison.Ordinal));

            return kind switch
            {
                EX_InstanceVariable => new PointerArg(pointer, variableName, Instance: true),
                EX_LocalOutVariable => new PointerArg(pointer, variableName, Out: true),
                EX_DefaultVariable => new PointerArg(pointer, variableName, Default: true),
                _ => new PointerArg(pointer, variableName)
            };
        }

        // 新建：owner 取「函数导出」或「本类导出」
        var ownerIndex = instance ? ClassExportIndex() : _asset.Exports.IndexOf(fn) + 1;
        if (ownerIndex <= 0)
        {
            throw new InvalidOperationException(
                $"无法确定 {variableName} 的归属（instance={instance}，函数={functionName}）");
        }

        return new PointerArg(
            new KismetPropertyPointer(new FFieldPath
            {
                Path = [new FName(_asset, variableName)],
                ResolvedOwner = new FPackageIndex(ownerIndex)
            }),
            variableName,
            Instance: instance);
    }

    /// <summary>本类导出的索引（成员变量的 ResolvedOwner）。取第一个 Class 导出。</summary>
    private int ClassExportIndex()
    {
        for (var i = 0; i < _asset.Exports.Count; i++)
        {
            if (_asset.Exports[i] is ClassExport)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// 把一次调用当作**值**来用（返回 <see cref="CallArg"/>），用于「赋值给变量 / 当参数」。
    /// </summary>
    public KArg CreateCallArg(FunctionRef function, IReadOnlyList<KArg>? args = null, KArg? self = null)
    {
        var signature = function.Signature
                        ?? Catalog?.Find(function.OwnerPath, function.Name)
                        ?? throw new InvalidOperationException($"没有 {function.FullPath} 的签名——不猜调用形态");

        args ??= DefaultArgs(function.Name, function.OwnerPath).Select(a => a.Value).ToArray();

        var index = function.Index.Index != 0
            ? function.Index
            : ImportIndex.EnsureFunction(_asset, function.Name, function.OwnerPath);

        var bound = function with { Index = index };

        if (function.IsExported)
        {
            return new CallArg(bound, args, CallShape.Virtual);
        }

        if (signature.IsStatic)
        {
            return new CallArg(bound, args, CallShape.Final);
        }

        return new CallArg(
            bound,
            args,
            CallShape.Context,
            ObjectVariable: "this",
            ObjectArg: self ?? new SelfArg(),
            InnerShape: CallShape.Final);
    }

    /// <summary>
    /// 插入调用，并把结果存进**自动命名**的临时变量。
    ///
    /// <para>
    /// 名字按 UE 约定派生：<c>CallFunc_&lt;函数名&gt;_ReturnValue</c>，重名加 <c>_2</c>、<c>_3</c>…
    /// 返回变量名，调用方用它继续引用结果。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不在编译器里自动生成</b>：编译器的输入是节点、输出是字节码，两者必须一一对应
    /// （否则「反编译 → 编译」的往返保证就废了）。临时变量必须在**节点层面**显式存在——
    /// 由这里在构造节点时创建，而不是编译期偷偷插入。
    /// </para>
    /// </summary>
    public InsertedCall InsertCallWithResult(
        string functionName,
        FunctionRef callee,
        params KArg[] args)
    {
        var returnType = ResultTypeOf(callee)
                         ?? throw new InvalidOperationException(
                             $"{callee.FullPath} 没有返回值，不能用 withResult——直接用 insert_call 即可");

        var existing = new HashSet<string>(Variables(functionName), StringComparer.Ordinal);

        // UE 的命名：CallFunc_<函数>_<返回引脚名>；重复时从 _1 开始（实测 ..._ReturnValue、_1、_2、_3）
        var pinName = returnType.Name;
        var baseName = $"CallFunc_{callee.Name}_{(string.IsNullOrEmpty(pinName) ? "ReturnValue" : pinName)}";
        var name = baseName;
        var suffix = 1;

        while (existing.Contains(name))
        {
            name = $"{baseName}_{suffix++}";
        }

        // 先声明再赋值：只造指针、不往属性表里加条目，运行时解析不到这个变量
        var declaration = DeclareLocalVariable(functionName, name, returnType.Type);

        var result = AssignCallResult(functionName, callee, name, args);
        return new InsertedCall(name, returnType, result.Opcodes, result.Hex, result.Bytes, declaration);
    }

    /// <summary>插入调用后得到的临时变量：名字 + **它的类型**（= 被调函数的返回参数）+ 编译结果。</summary>
    public sealed record InsertedCall(
        string Variable,
        FunctionParam? ReturnType,
        IReadOnlyList<string> Opcodes,
        string Hex,
        int Bytes,
        string? Declaration = null);

    /// <summary>
    /// 调用的返回类型。临时变量的类型就是它，不需要读资产的局部变量表。
    ///
    /// <para>
    /// 只给到 Property kind 与字节大小；<c>StructProperty</c> 的具体结构名在 jmap 里通常是 null。
    /// </para>
    /// </summary>
    public FunctionParam? ResultTypeOf(FunctionRef function)
        => (function.Signature ?? Catalog?.Find(function.OwnerPath, function.Name))?.ReturnParam;

    /// <summary>
    /// 调用一次并把返回值存进局部变量（等价于 UE 的
    /// <c>Set CallFunc_XXX_ReturnValue = XXX(...)</c>）。
    /// </summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) AssignCallResult(
        string functionName,
        FunctionRef callee,
        string resultVariable,
        params KArg[] args)
    {
        var target = EnsureLocalVariable(functionName, resultVariable);
        var value = CreateCallArg(callee, args.Length > 0 ? args : null);

        return Edit(functionName, nodes =>
        {
            var list = nodes.ToList();
            var at = list.Count > 0 && list[^1] is ReturnNode ? list.Count - 1 : list.Count;
            list.Insert(at, new AssignNode(resultVariable, value, TargetPointer: target));
            return list;
        });
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
        => DecompileDetailed(functionName).Nodes;

    /// <summary>反编译结果 + 它到底是哪种表示（是否退化成扁平）。</summary>
    public sealed record DecompileResult(IReadOnlyList<KNode> Nodes, bool Structured);

    /// <summary>
    /// 同 <see cref="Decompile"/>，但额外告诉调用方「结构化还原是否成功」。
    ///
    /// <para>
    /// 退化本身不是错误（字节仍然一致），但**必须让调用方知道**：
    /// 拿到扁平指令流的 agent 会把 `&lt;JumpIfNot&gt;` 当成普通节点，改出来的东西不是它想要的。
    /// </para>
    /// </summary>
    public DecompileResult DecompileDetailed(string functionName)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var bytecode = fn.ScriptBytecode!;
        var fellBack = false;
        var options = new DecompileOptions(
            Measure: e => e.GetSize(_asset),
            ResolveImport: idx => Names.TryGetValue(idx?.Index ?? 0, out var n) ? n : null);

        var original = SerializeBytecode(bytecode);
        var nodes = KismetDecompiler.DecompileVerified(
            bytecode, original, options,
            ns => SerializeBytecode(KismetCompiler.CompileToExpressions(ns, ContextOf(fn))),
            onFallback: () => fellBack = true);

        return new DecompileResult(nodes, !fellBack);
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

    // ---------------- ubergraph 入口点 ----------------
    //
    // 入口偏移**不在** ubergraph 自己的字节码里，也不在 UClass 上（本机实测 UE5.6 里
    // FunctionExport 尾部的 EventGraphFunction/EventGraphCallOffset 全是 0）——
    // 它被烘进了**调用点**：每个事件存根都是
    //     LocalFinal ExecuteUbergraph_X(<EX_IntConst 入口偏移>)
    // 所以「插入调用会不会写坏资产」的判据只有一条：
    //     这些常量指向的那条指令，改动后还在不在原位。
    // 段内字节一变、这些偏移就整体错位，UE 的 ComputedJump 会跳到指令中间。

    /// <summary>一个 ubergraph 入口点：哪个函数调用它、传进去的偏移是多少。</summary>
    public sealed record UbergraphEntrySite(string CallerFunction, int Offset);

    /// <summary>入口点在编辑后是否仍然落在同一条指令上。</summary>
    public sealed record UbergraphEntryCheck(
        string CallerFunction,
        int Offset,
        bool Ok,
        string Before,
        string After);

    /// <summary>扫描全资产，找出所有「调用 <paramref name="ubergraphName"/> 并传常量偏移」的调用点。</summary>
    public IReadOnlyList<UbergraphEntrySite> UbergraphCallSites(string ubergraphName)
    {
        var sites = new List<UbergraphEntrySite>();

        foreach (var fn in _asset.Exports.OfType<FunctionExport>())
        {
            if (fn.ScriptBytecode is not { Length: > 0 } bytecode)
            {
                continue;
            }

            foreach (var expression in bytecode)
            {
                var (stack, parameters) = expression switch
                {
                    EX_LocalFinalFunction lf => (lf.StackNode, lf.Parameters),
                    EX_FinalFunction ff => (ff.StackNode, ff.Parameters),
                    _ => (null, null)
                };

                if (stack is null || parameters is not { Length: > 0 })
                {
                    continue;
                }

                if (!Names.TryGetValue(stack.Index, out var callee) || callee != ubergraphName)
                {
                    continue;
                }

                if (parameters[0] is EX_IntConst constant)
                {
                    sites.Add(new UbergraphEntrySite(fn.ObjectName.ToString(), constant.Value));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// 校验 ubergraph 的所有入口点：编辑后每个偏移是否还落在**同一条指令**上。
    ///
    /// <para>
    /// 比对基准是打开资产时的原始字节码（首次编辑前抓的快照），因为调用点里的常量
    /// 就是按原始布局写的。
    /// </para>
    /// </summary>
    public IReadOnlyList<UbergraphEntryCheck> CheckUbergraphEntryPoints(string ubergraphName)
    {
        var fn = FindFunction(ubergraphName)
                 ?? throw new InvalidOperationException($"找不到函数: {ubergraphName}");

        var before = _originalBytecode.TryGetValue(ubergraphName, out var snapshot)
            ? snapshot
            : fn.ScriptBytecode ?? [];
        var after = fn.ScriptBytecode ?? [];

        var oldMap = InstructionMap(before);
        var newMap = InstructionMap(after);

        return UbergraphCallSites(ubergraphName)
            .Select(site =>
            {
                var oldOp = oldMap.TryGetValue(site.Offset, out var o) ? o : "<无>";
                var newOp = newMap.TryGetValue(site.Offset, out var n) ? n : "<无>";
                return new UbergraphEntryCheck(
                    site.CallerFunction, site.Offset, oldOp == newOp && oldOp != "<无>", oldOp, newOp);
            })
            .ToArray();
    }

    /// <summary>所有「改过且是 ubergraph」的函数的入口点校验；有失效的直接抛异常（写盘前最后一道闸）。</summary>
    public IReadOnlyList<UbergraphEntryCheck> VerifyEditedUbergraphs()
    {
        var all = new List<UbergraphEntryCheck>();

        foreach (var name in _originalBytecode.Keys.Where(
                     n => n.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal)))
        {
            var checks = CheckUbergraphEntryPoints(name);
            var broken = checks.Where(c => !c.Ok).ToArray();
            if (broken.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{name} 的入口点已失效，拒绝写盘（会跳进指令中间）：\n" +
                    string.Join("\n", broken.Select(b =>
                        $"  {b.CallerFunction} 传的偏移 {b.Offset}：改动前 {b.Before} → 改动后 {b.After}")));
            }

            all.AddRange(checks);
        }

        return all;
    }

    /// <summary>指令起始偏移 → 指令名。</summary>
    private Dictionary<int, string> InstructionMap(IReadOnlyList<KismetExpression> expressions)
    {
        var map = new Dictionary<int, string>();
        var offset = 0;
        foreach (var expression in expressions)
        {
            map[offset] = expression.Inst;
            offset += (int)expression.GetSize(_asset);
        }

        return map;
    }

    // ---------------- 参数赋值 ----------------

    /// <summary>参数值的种类（transport 无关；MCP 侧把 JSON 转成它）。</summary>
    public enum ArgKind
    {
        Int,
        Long,
        Float,
        Double,
        Bool,
        Byte,
        String,
        Name,
        Variable,
        None,
        Self,
        Call
    }

    /// <summary>一个具体的参数值。</summary>
    public sealed record ArgValue(
        ArgKind Kind,
        string? Text = null,
        long? Integer = null,
        double? Number = null,
        bool? Flag = null,
        string? Callee = null,
        string? CalleeOwner = null);

    /// <summary>「给哪个参数赋什么值」：<see cref="Param"/> 为空时按位置取第一个还没填的参数。</summary>
    public sealed record ArgSpec(string? Param, ArgValue Value);

    /// <summary>
    /// 按签名生成**完整**参数表：<paramref name="overrides"/> 里给到的用给的值，其余用占位默认值。
    ///
    /// <para>
    /// 类型不对会直接报错，不会「凑合着编码」——参数类型错了，字节看着对、运行时行为全错。
    /// 目前支持标量（int / int64 / float / double / bool / byte / str / name）、
    /// 变量引用、嵌套调用、空对象；text / struct / array / map / set 仍只支持占位。
    /// </para>
    /// </summary>
    public IReadOnlyList<KArg> BuildArgs(
        string functionName,
        FunctionRef callee,
        IReadOnlyList<ArgSpec> overrides)
    {
        var signature = callee.Signature ?? Catalog?.Find(callee.OwnerPath, callee.Name);

        // 没有签名（比如本资产自己的蓝图函数，jmap 里只有 /Script）：
        // 只接受**显式给值**，绝不猜默认参数——猜出来的调用形态是错的。
        if (signature is null)
        {
            if (overrides.Count == 0)
            {
                throw new InvalidOperationException(
                    $"没有 {callee.FullPath} 的签名（jmap 只覆盖 /Script）——" +
                    "调用本资产的蓝图函数必须用 args 显式给出全部参数");
            }

            return overrides.Select(o => MakeArg(functionName, null, o.Value)).ToArray();
        }

        var inputs = signature.Inputs;
        var slots = new KArg?[inputs.Count];

        foreach (var spec in overrides)
        {
            var index = -1;
            if (!string.IsNullOrEmpty(spec.Param))
            {
                index = -1;
                for (var i = 0; i < inputs.Count; i++)
                {
                    if (string.Equals(inputs[i].Name, spec.Param, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"{callee.Name} 没有名为 {spec.Param} 的输入参数；可选：{string.Join(", ", inputs.Select(p => p.Name))}");
                }
            }
            else
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    if (slots[i] is null)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"{callee.Name} 只有 {inputs.Count} 个输入参数，收到 {overrides.Count} 个值");
                }
            }

            if (slots[index] is not null)
            {
                throw new InvalidOperationException($"{callee.Name} 的参数 {inputs[index].Name} 被赋了两次值");
            }

            slots[index] = MakeArg(functionName, inputs[index], spec.Value);
        }

        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] ??= MakeDefault(inputs[i]).Value;
        }

        return slots.Select(s => s!).ToArray();
    }

    private KArg MakeArg(string functionName, FunctionParam? param, ArgValue value)
    {
        if (value.Kind == ArgKind.Variable)
        {
            return ResolveVariableArg(functionName, Require(value.Text, "variable", param));
        }

        if (value.Kind == ArgKind.Call)
        {
            var callee = ResolveFunction(Require(value.Callee, "call", param), value.CalleeOwner)
                         ?? throw new InvalidOperationException($"找不到函数 {value.Callee}");
            return CreateCallArg(callee, BuildArgs(functionName, callee, []));
        }

        // 没有签名时只按 kind 构造，不做类型校验（调用方显式负责）
        if (param is null)
        {
            return value.Kind switch
            {
                ArgKind.Int => new IntArg((int)(value.Integer ?? 0)),
                ArgKind.Long => new LongArg(value.Integer ?? 0),
                ArgKind.Float => new FloatArg((float)(value.Number ?? 0)),
                ArgKind.Double => new DoubleArg(value.Number ?? 0),
                ArgKind.Bool => new BoolArg(value.Flag ?? false),
                ArgKind.Byte => new ByteArg((byte)(value.Integer ?? 0)),
                ArgKind.String => new StringArg(value.Text ?? ""),
                ArgKind.Name => new NameArg(new FName(_asset, string.IsNullOrEmpty(value.Text) ? "None" : value.Text)),
                ArgKind.None => new NoObjectArg(),
                ArgKind.Self => new SelfArg(),
                _ => throw new InvalidOperationException($"不支持的参数种类 {value.Kind}")
            };
        }

        static InvalidOperationException Mismatch(FunctionParam p, ArgValue v) => new(
            $"参数 {p.Name} 的类型是 {p.Type}，给的是 {v.Kind}——类型不匹配，不猜");

        return param.Type switch
        {
            "IntProperty" => value.Kind switch
            {
                ArgKind.Int => new IntArg((int)(value.Integer ?? 0)),
                ArgKind.Byte => new IntArg((byte)(value.Integer ?? 0)),
                ArgKind.Bool => new IntArg(value.Flag == true ? 1 : 0),
                _ => throw Mismatch(param, value)
            },
            "Int64Property" => value.Kind is ArgKind.Long or ArgKind.Int
                ? new LongArg(value.Integer ?? 0)
                : throw Mismatch(param, value),
            "FloatProperty" => value.Kind switch
            {
                ArgKind.Float => new FloatArg((float)(value.Number ?? 0)),
                ArgKind.Double => new FloatArg((float)(value.Number ?? 0)),
                ArgKind.Int => new FloatArg(value.Integer ?? 0),
                _ => throw Mismatch(param, value)
            },
            "DoubleProperty" => value.Kind switch
            {
                ArgKind.Double => new DoubleArg(value.Number ?? 0),
                ArgKind.Float => new DoubleArg(value.Number ?? 0),
                ArgKind.Int => new DoubleArg(value.Integer ?? 0),
                _ => throw Mismatch(param, value)
            },
            "BoolProperty" => value.Kind == ArgKind.Bool
                ? new BoolArg(value.Flag ?? false)
                : throw Mismatch(param, value),
            "ByteProperty" or "EnumProperty" => value.Kind is ArgKind.Byte or ArgKind.Int
                ? new ByteArg((byte)(value.Integer ?? 0))
                : throw Mismatch(param, value),
            "StrProperty" => value.Kind == ArgKind.String
                ? new StringArg(value.Text ?? "")
                : throw Mismatch(param, value),
            // FName 的空值是 None，空字符串进不了名字表
            "NameProperty" => value.Kind == ArgKind.Name
                ? new NameArg(new FName(_asset, string.IsNullOrEmpty(value.Text) ? "None" : value.Text))
                : throw Mismatch(param, value),
            "ObjectProperty" or "ClassProperty" or "SoftObjectProperty" or "DelegateProperty" =>
                value.Kind switch
                {
                    ArgKind.None => new NoObjectArg(),
                    ArgKind.Self => new SelfArg(),
                    _ => throw Mismatch(param, value)
                },
            _ => throw new InvalidOperationException(
                $"参数 {param.Name} 的类型 {param.Type} 暂不支持赋值（text / struct / array / map / set 需要真实载荷）——" +
                "先用占位，或改用参数类型简单的函数")
        };
    }

    private static string Require(string? text, string what, FunctionParam? param) =>
        string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException(
                $"{(param is null ? "参数" : "参数 " + param.Name)} 的 {what} 值不能为空")
            : text;

    /// <summary>变量引用：已在字节码里出现过就复用原指针；否则看是不是类上的成员变量。</summary>
    private KArg ResolveVariableArg(string functionName, string name)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var existing = ExpressionWalker.BuildVariableIndex(fn.ScriptBytecode!);
        if (existing.ContainsKey(name))
        {
            return EnsureVariable(functionName, name, instance: false);
        }

        var classExport = _asset.Exports.OfType<ClassExport>().FirstOrDefault();
        var isMember = classExport?.LoadedProperties?.Any(p => p.Name.ToString() == name) == true;
        if (!isMember)
        {
            throw new InvalidOperationException(
                $"{functionName} 里没有变量 {name}（局部变量只认已出现在字节码里的；成员变量要在类上）");
        }

        return EnsureVariable(functionName, name, instance: true);
    }

    // ---------------- 局部变量声明 ----------------

    /// <summary>
    /// 把 <paramref name="variableName"/> 真正声明成函数的局部变量（往属性表里加一条 FProperty）。
    ///
    /// <para>
    /// 只造 <c>FFieldPath</c> 指针是不够的：运行时是按指针去函数的属性表里解析的，
    /// 表里没有这一条就解析不到。模板从资产里**同类型的现有属性**克隆——
    /// 不猜 <c>ElementSize</c> / <c>PropertyFlags</c> 这些字段。
    /// </para>
    ///
    /// <para>返回 null 表示成功；否则返回不能声明的原因（调用方决定是报错还是只警告）。</para>
    /// </summary>
    public string? DeclareLocalVariable(string functionName, string variableName, string? propertyType)
    {
        var fn = FindFunction(functionName)
                 ?? throw new InvalidOperationException($"找不到函数: {functionName}");

        var properties = fn.LoadedProperties ?? [];
        if (properties.Any(p => p.Name.ToString() == variableName))
        {
            return null;
        }

        if (string.IsNullOrEmpty(propertyType))
        {
            return $"不知道 {variableName} 的类型（被调函数没有返回值签名）";
        }

        // 结构体 / 容器 / 对象：模板的 Struct / PropertyClass / Inner 可能是别的类型，克隆出来就是错的
        if (propertyType is "StructProperty" or "ArrayProperty" or "MapProperty" or "SetProperty")
        {
            return $"{propertyType} 的临时变量需要正确的结构/容器类型，jmap 里拿不到，不自动声明";
        }

        if (propertyType is "ObjectProperty" or "ClassProperty" or "SoftObjectProperty")
        {
            return $"{propertyType} 的临时变量需要正确的 PropertyClass，不自动声明";
        }

        var template = FindPropertyTemplate(fn, propertyType);
        if (template is null)
        {
            return $"资产里没有 {propertyType} 类型的属性可作模板";
        }

        var clone = CloneProperty(template);
        clone.Name = new FName(_asset, variableName);
        fn.LoadedProperties = [.. properties, clone];
        return null;
    }

    /// <summary>找一个同类型的属性当模板：优先本函数，其次全资产。</summary>
    private UAssetAPI.FieldTypes.FProperty? FindPropertyTemplate(FunctionExport fn, string propertyType)
    {
        static bool Matches(UAssetAPI.FieldTypes.FProperty p, string type)
            => string.Equals(p.SerializedType?.Value?.Value, type, StringComparison.Ordinal);

        var local = fn.LoadedProperties?.FirstOrDefault(p => Matches(p, propertyType));
        if (local is not null)
        {
            return local;
        }

        foreach (var export in _asset.Exports.OfType<StructExport>())
        {
            var found = export.LoadedProperties?.FirstOrDefault(p => Matches(p, propertyType));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>浅拷贝一个属性（只换名字；Write 只读字段，不共享可变状态）。</summary>
    private static UAssetAPI.FieldTypes.FProperty CloneProperty(UAssetAPI.FieldTypes.FProperty source)
    {
        var method = typeof(object).GetMethod(
            "MemberwiseClone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("拿不到 MemberwiseClone");
        return (UAssetAPI.FieldTypes.FProperty)method.Invoke(source, null)!;
    }

    // ---------------- 节点级编辑 ----------------

    /// <summary>删掉路径指向的节点。</summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) DeleteNode(string functionName, string path)
    {
        var target = NodePaths.Find(Decompile(functionName), path);
        if (target is ReturnNode)
        {
            throw new InvalidOperationException("不能删 Return——函数的字节码必须以 Return 结束");
        }

        return Edit(functionName, nodes => NodePaths.Delete(nodes, path));
    }

    /// <summary>在路径指向的节点**之前**插入一次调用（不写 <paramref name="before"/> 就是末尾追加）。</summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) InsertCallBefore(
        string functionName,
        FunctionRef callee,
        string before,
        params KArg[] args)
    {
        var call = CreateCall(callee, args.Length > 0 ? args : null);
        return Edit(functionName, nodes => NodePaths.InsertBefore(nodes, before, call));
    }

    /// <summary>把路径指向的调用节点换成另一次调用（保留它原来的形态与上下文）。</summary>
    public (IReadOnlyList<string> Opcodes, string Hex, int Bytes) ReplaceCall(
        string functionName,
        string path,
        FunctionRef callee,
        params KArg[] args)
    {
        var target = NodePaths.Find(Decompile(functionName), path);
        var replacement = RebuildCall(target, callee, args.Length > 0 ? args : null)
                          ?? throw new InvalidOperationException(
                              $"路径 {path} 指向的是 {target.GetType().Name}（{target.Title}），不是调用节点");

        return Edit(functionName, nodes => NodePaths.Replace(nodes, path, replacement));
    }

    /// <summary>按原节点的形态造一个新调用；不是调用节点返回 null。</summary>
    private KNode? RebuildCall(KNode node, FunctionRef callee, IReadOnlyList<KArg>? args) => node switch
    {
        CallNode c => new CallNode(callee, args ?? c.Args),
        MathCallNode m => new MathCallNode(callee, args ?? m.Args),
        LocalFinalCallNode l => new LocalFinalCallNode(callee, args ?? l.Args),
        VirtualCallNode v => new VirtualCallNode(callee.Name, args ?? v.Args),
        MulticastCallNode mc => new MulticastCallNode(callee, args ?? mc.Args, mc.Delegate),
        ContextCallNode cc => new ContextCallNode(
            cc.ObjectVariable, callee, args ?? cc.Args, cc.ObjectArg, cc.RValuePointer, cc.InnerShape, cc.ContextKind),
        _ => null
    };

    // ---------------- 整资产校验 ----------------

    /// <summary>单个函数的校验结论。</summary>
    public sealed record VerifyEntry(string Function, bool Structured, string? Error);

    /// <summary>
    /// 逐函数校验：能不能**结构化**还原（= 回编译后逐字节一致）。
    ///
    /// <para>
    /// 退回扁平也是字节一致的，但那只说明「原样搬运」没错——结构化才是「我们真的看懂了这段字节」。
    /// 所以这里把「扁平」也当成需要关注的结果报出来。
    /// </para>
    /// </summary>
    public IReadOnlyList<VerifyEntry> VerifyAll()
    {
        var entries = new List<VerifyEntry>();

        foreach (var name in Functions)
        {
            try
            {
                var result = DecompileDetailed(name);
                entries.Add(new VerifyEntry(name, result.Structured, null));
            }
            catch (Exception ex)
            {
                entries.Add(new VerifyEntry(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return entries;
    }

    private CompileContext ContextOf(FunctionExport fn) => new(
        ExpressionWalker.BuildVariableIndex(fn.ScriptBytecode!),
        ImportIndex.BuildFunctionIndex(_asset),
        e => e.GetSize(_asset),
        n => new FName(_asset, n),
        ImportIndex.BuildFunctionIndexByOwner(_asset));

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
        var functionsByOwner = ImportIndex.BuildFunctionIndexByOwner(_asset);

        var exprs = KismetCompiler.CompileToExpressions(
            nodes,
            new CompileContext(
                Variables: variables,
                Functions: functions,
                Measure: e => e.GetSize(_asset),
                ResolveName: n => new FName(_asset, n),
                FunctionsByOwner: functionsByOwner));

        if (assign)
        {
            // 首次编辑前抓快照：ubergraph 的入口点校验要拿原始布局当基准
            _originalBytecode.TryAdd(functionName, fn.ScriptBytecode ?? []);

            var previous = fn.ScriptBytecode;
            fn.ScriptBytecode = exprs.ToArray();

            // ubergraph 的特殊闸门：入口偏移烘在**调用点**里，改完必须仍然指向同一条指令
            if (functionName.StartsWith("ExecuteUbergraph_", StringComparison.Ordinal))
            {
                var broken = CheckUbergraphEntryPoints(functionName).Where(c => !c.Ok).ToArray();
                if (broken.Length > 0)
                {
                    fn.ScriptBytecode = previous;   // 回滚这次编辑，工作区保持可用
                    throw new InvalidOperationException(
                        $"{functionName} 的改动会让入口点错位，已回滚：\n" +
                        string.Join("\n", broken.Select(b =>
                            $"  {b.CallerFunction} 传的偏移 {b.Offset}：改动前 {b.Before} → 改动后 {b.After}\n" +
                            "  （入口偏移烘在调用点的 EX_IntConst 里，只能改段的尾部，不能动段内布局）")));
                }
            }
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
        // 写盘前最后一道闸：改过的 ubergraph 入口点必须仍然有效
        VerifyEditedUbergraphs();

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
