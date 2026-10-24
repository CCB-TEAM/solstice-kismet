using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace Solstice.Kismet;

/// <summary>
/// 反编译所需的解析回调。都拿不到时只能还原与资产无关的部分。
/// </summary>
public sealed record DecompileOptions(
    Func<KismetExpression, uint>? Measure = null,
    Func<FPackageIndex, string?>? ResolveImport = null,
    Action<string>? Warn = null,
    bool Flatten = false,
    bool InsideUberSection = false,
    bool FoldBottomLoops = true,
    bool UseBlockGraph = false);

/// <summary>
/// Kismet 字节码 → 平铺节点模型。
///
/// <para>
/// <b>定位：结构还原，不是语义等价往返。</b>
/// 只覆盖本库能生成的那部分指令；遇到不认识的表达式会退化成
/// <see cref="RawNode"/> 保留原文，不会静默丢弃。
/// </para>
///
/// <para>
/// 跳转操作数是**字节偏移**，所以还原 if/loop 必须提供
/// <see cref="DecompileOptions.Measure"/>（一般是 <c>e =&gt; e.GetSize(asset)</c>）。
/// 没提供时跳转按普通指令处理，结构信息丢失。
/// </para>
/// </summary>
public static class KismetDecompiler
{
    /// <param name="baseOffset">
    /// 这段字节码在原函数里的起始偏移。递归反编译切片时必须传，
    /// 否则 EX_Jump/EX_JumpIfNot 里存的是绝对偏移，切完就对不上了。
    /// </param>
    public static IReadOnlyList<KNode> Decompile(
        IReadOnlyList<KismetExpression> expressions,
        DecompileOptions? options = null,
        uint baseOffset = 0)
    {
        options ??= new DecompileOptions();

        // Ubergraph 走独立的展开通道：它是「调度头 + 入口段 + 尾声」的基本块图，
        // 不是可以嵌套还原的控制流。段体反编译时 InsideUberSection=true，避免递归。
        if (!options.Flatten && !options.InsideUberSection && baseOffset == 0)
        {
            var uber = UberGraph.Analyze(expressions, options);
            if (uber is not null)
            {
                return DecompileUber(expressions, uber, options);
            }

            // 基本块 + 显式 goto：给「跳转越出片段、还原不成树」的函数留的可读出口
            if (options.UseBlockGraph)
            {
                var blocks = BlockGraph.Build(expressions, options);
                if (blocks.Count > 0)
                {
                    var starts = blocks.Select(b => b.StartOffset).ToArray();
                    var nodes2 = blocks.Select(b => new BasicBlockNode(
                        b.Index,
                        b.StartOffset,
                        b.Nodes,
                        b.Exit,
                        b.BranchTarget is { } target && Array.IndexOf(starts, target) is var idx && idx >= 0
                            ? idx
                            : null,
                        b.Condition)).ToList();

                    return new KNode[] { new BlockGraphNode(nodes2) };
                }
            }
        }

        var offsets = BuildOffsets(expressions, options.Measure, baseOffset);

        // 底部测条件的循环：先扫回边，再在扫描到循环头时整体折叠。
        // UE 把 for 循环编成「体 → 条件 → JumpIfNot(exit) → 自增 → Jump(体开头)」，
        // 回跳目标在条件**之前**，所以必须先认出来，否则回跳会被补错位置。
        var bottomLoops = options.Flatten || !options.FoldBottomLoops || offsets is null
            ? null
            : FindBottomTestedLoops(expressions, offsets);

        var nodes = new List<KNode>();
        var i = 0;

        while (i < expressions.Count)
        {
            if (bottomLoops is not null && bottomLoops.TryGetValue(i, out var loop))
            {
                var body = Decompile(Slice(expressions, i, loop.TestIndex), options, OffAt(offsets!, i));
                var condition = ToArg((expressions[loop.TestIndex] as EX_JumpIfNot)?.BooleanExpression, options)
                                ?? throw new InvalidOperationException("底部测条件循环的条件表达式暂不支持");
                var tail = Decompile(
                    Slice(expressions, loop.TestIndex + 1, loop.JumpIndex), options, OffAt(offsets!, loop.TestIndex + 1));

                nodes.Add(new BottomLoopNode(body, condition, tail));
                i = loop.JumpIndex + 1;
                continue;
            }

            var e = expressions[i];

            switch (e)
            {
                case EX_EndOfScript:
                    i++;
                    continue;

                case EX_Nothing:
                    // 占位（if/loop 的落点标记），跳过
                    i++;
                    continue;

                case EX_Let let:
                    nodes.Add(new AssignNode(
                        TargetName(let.Variable),
                        RequireArg(let.Expression, options),
                        PointerOf(let.Variable),
                        ToArg(let.Variable, options),
                        let.Value));
                    i++;
                    continue;

                case EX_LetBool letBool:
                    nodes.Add(ToLetBase(letBool.VariableExpression, letBool.AssignmentExpression, AssignKind.LetBool, options));
                    i++;
                    continue;

                case EX_LetObj letObj:
                    nodes.Add(ToLetBase(letObj.VariableExpression, letObj.AssignmentExpression, AssignKind.LetObj, options));
                    i++;
                    continue;

                case EX_LetWeakObjPtr letWeak:
                    nodes.Add(ToLetBase(letWeak.VariableExpression, letWeak.AssignmentExpression, AssignKind.LetWeakObjPtr, options));
                    i++;
                    continue;

                case EX_LetDelegate letDel:
                    nodes.Add(ToLetBase(letDel.VariableExpression, letDel.AssignmentExpression, AssignKind.LetDelegate, options));
                    i++;
                    continue;

                case EX_LetValueOnPersistentFrame persistent:
                    nodes.Add(new PersistentAssignNode(
                        persistent.DestinationProperty!,
                        RequireArg(persistent.AssignmentExpression, options),
                        PropName(persistent.DestinationProperty)));
                    i++;
                    continue;

                case EX_Return ret:
                    nodes.Add(new ReturnNode(ToArg(ret.ReturnExpression, options)));
                    i++;
                    continue;

                case EX_JumpIfNot jumpIfNot when offsets is not null && !options.Flatten:
                {
                    var block = BuildBlock(expressions, offsets, i, jumpIfNot, options);
                    if (block is null)
                    {
                        nodes.Add(Raw(e));
                        i++;
                        continue;
                    }

                    nodes.Add(block.Value.Node);
                    i = block.Value.Next;
                    continue;
                }

                case EX_Jump:
                    // 没有被 JumpIfNot 认领的孤立跳转，原样保留
                    nodes.Add(Raw(e));
                    i++;
                    continue;

                // ---- 执行流栈：异步/延迟节点的「恢复点」机制 ----
                // Push 只登记地址、不改变执行位置；Pop 才跳回去。
                case EX_PushExecutionFlow push:
                    nodes.Add(new PushFlowNode(push.PushingAddress));
                    i++;
                    continue;

                case EX_PopExecutionFlow:
                    nodes.Add(new PopFlowNode());
                    i++;
                    continue;

                case EX_PopExecutionFlowIfNot popIfNot:
                    nodes.Add(new PopFlowIfNotNode(RequireArg(popIfNot.BooleanExpression, options)));
                    i++;
                    continue;

                case EX_ComputedJump computedJump:
                    nodes.Add(new ComputedJumpNode(RequireArg(computedJump.CodeOffsetExpression, options)));
                    i++;
                    continue;

                case EX_SetArray setArray:
                    nodes.Add(new SetArrayNode(
                        setArray.AssigningProperty is null
                            ? new NoObjectArg()
                            : RequireArg(setArray.AssigningProperty, options),
                        setArray.ArrayInnerProp!,
                        ToArgs(setArray.Elements, options)));
                    i++;
                    continue;

                // 注意：EX_LocalFinalFunction / EX_CallMath / EX_CallMulticastDelegate
                // 都继承自 EX_FinalFunction，必须排在它之前，否则永远匹配不到。
                case EX_CallMulticastDelegate mcd:
                {
                    var mcdName = options.ResolveImport?.Invoke(mcd.StackNode) ?? $"<import {mcd.StackNode?.Index}>";
                    nodes.Add(new MulticastCallNode(
                        mcdName,
                        ToArgs(mcd.Parameters, options),
                        ToArg(mcd.Delegate, options),
                        mcd.StackNode));
                    i++;
                    continue;
                }

                case EX_LocalFinalFunction localFinal:
                {
                    var lfName = options.ResolveImport?.Invoke(localFinal.StackNode) ?? $"<import {localFinal.StackNode?.Index}>";
                    nodes.Add(new LocalFinalCallNode(lfName, ToArgs(localFinal.Parameters, options), localFinal.StackNode));
                    i++;
                    continue;
                }

                case EX_CallMath math:
                {
                    var mathName = options.ResolveImport?.Invoke(math.StackNode) ?? $"<import {math.StackNode?.Index}>";
                    nodes.Add(new MathCallNode(mathName, ToArgs(math.Parameters, options), math.StackNode));
                    i++;
                    continue;
                }

                case EX_FinalFunction final:
                {
                    var name = options.ResolveImport?.Invoke(final.StackNode) ?? $"<import {final.StackNode?.Index}>";
                    nodes.Add(new CallNode(name, ToArgs(final.Parameters, options), null, final.StackNode));
                    i++;
                    continue;
                }

                case EX_LocalVirtualFunction virt:
                    nodes.Add(new VirtualCallNode(virt.VirtualFunctionName?.ToString() ?? "<?>", ToArgs(virt.Parameters, options)));
                    i++;
                    continue;

                case EX_Context ctx:
                {
                    var ctxNode = ContextNode(ctx, options);
                    if (ctxNode is null)
                    {
                        nodes.Add(Raw(e));
                    }
                    else
                    {
                        nodes.Add(ctxNode);
                    }

                    i++;
                    continue;
                }

                default:
                    nodes.Add(Raw(e));
                    i++;
                    continue;
            }
        }

        return nodes;
    }

    /// <summary>
    /// 展开 ubergraph：调度头 + 各入口段（段尾单独记）+ 尾声及其之后的指令。
    /// 段体已经由 <see cref="UberGraph.Analyze"/> 反编译好，这里只负责组装与段间引用。
    /// </summary>
    private static IReadOnlyList<KNode> DecompileUber(
        IReadOnlyList<KismetExpression> expressions,
        UberGraphAnalysis uber,
        DecompileOptions options)
    {
        var starts = uber.Sections.Select(s => s.StartOffset).ToArray();
        var sections = new List<UberSectionNode>(uber.Sections.Count);

        foreach (var section in uber.Sections)
        {
            int? targetOrdinal = null;
            if (section.Terminator == UberTerminator.JumpToOtherSection && section.BranchTarget is { } target)
            {
                var index = Array.IndexOf(starts, target);
                if (index >= 0)
                {
                    targetOrdinal = index;
                }
                else
                {
                    options.Warn?.Invoke($"ubergraph 段 #{section.Ordinal} 跳到未识别的段（offset={target}）");
                }
            }

            sections.Add(new UberSectionNode(
                section.StartOffset,
                section.Nodes,
                section.Terminator,
                targetOrdinal,
                section.TerminatorCondition,
                section.Label));
        }

        var nodes = new List<KNode>
        {
            new UberGraphNode(uber.DispatchExpression ?? new NoObjectArg(), uber.PushesEpilogue, sections)
        };

        // 尾声及其之后的指令（Return / EndOfScript）照常还原，不能丢
        if (uber.EpilogueIndex < expressions.Count)
        {
            var tail = new List<KismetExpression>(expressions.Count - uber.EpilogueIndex);
            for (var i = uber.EpilogueIndex; i < expressions.Count; i++)
            {
                tail.Add(expressions[i]);
            }

            nodes.AddRange(Decompile(tail, options with { InsideUberSection = true }, uber.EpilogueOffset));
        }

        return nodes;
    }

    /// <summary>
    /// 反编译并自校验：结构化还原的结果回编译后若与原始字节不符，
    /// 自动退回扁平模式（纯指令、不还原 if/loop）。
    ///
    /// <para>
    /// 字节一致性是硬要求，可读结构是附加价值——所以结构还原失败时宁可退化成平铺，
    /// 也不能产出与原始字节不同的结果。
    /// </para>
    /// </summary>
    /// <param name="originalBytes">原始字节码序列化后的字节，用作比对基准。</param>
    /// <param name="recompile">把节点列表重新编译成字节，由调用方提供（需要资产上下文）。</param>
    /// <param name="onFallback">结构化尝试全部失败、即将退回扁平模式时回调（调用方据此告知用户「这次是退化的」）。</param>
    public static IReadOnlyList<KNode> DecompileVerified(
        IReadOnlyList<KismetExpression> expressions,
        byte[] originalBytes,
        DecompileOptions? options,
        Func<IReadOnlyList<KNode>, byte[]> recompile,
        uint baseOffset = 0,
        Action? onFallback = null)
    {
        options ??= new DecompileOptions();

        if (!options.Flatten)
        {
            // 结构还原是「尝试」而不是「断言」：先试完整花式（含底部循环折叠），
            // 不行再退一步试保守形态，最后才退扁平。
            // 折叠只对一部分函数有益（Logic 57→23），对另一部分有害（Cards 126→143），
            // 所以必须用字节比对来裁决，而不是一刀切。
            foreach (var attempt in Attempts(options))
            {
                var structured = Decompile(expressions, attempt, baseOffset);
                byte[] rebuilt;
                try
                {
                    rebuilt = recompile(structured);
                }
                catch (Exception ex)
                {
                    options.Warn?.Invoke($"结构化还原回编译失败（{Describe(attempt)}）：{ex.Message}");
                    continue;
                }

                if (rebuilt.AsSpan().SequenceEqual(originalBytes))
                {
                    return structured;
                }

                options.Warn?.Invoke(
                    $"结构化还原结果与原始字节不符（{Describe(attempt)}，{rebuilt.Length} vs {originalBytes.Length}）");
            }

            options.Warn?.Invoke("所有结构化尝试均不符，退回扁平模式");
            onFallback?.Invoke();
        }

        return Decompile(expressions, options with { Flatten = true }, baseOffset);
    }

    /// <summary>结构化还原的尝试顺序：先花式，再保守，最后基本块图。</summary>
    private static IEnumerable<DecompileOptions> Attempts(DecompileOptions options)
    {
        yield return options;

        if (options.FoldBottomLoops)
        {
            yield return options with { FoldBottomLoops = false };
        }

        yield return options with { FoldBottomLoops = true, UseBlockGraph = true };
    }

    private static string Describe(DecompileOptions options)
        => options.UseBlockGraph ? "基本块图"
            : options.FoldBottomLoops ? "折叠底部循环"
            : "保守";

    /// <summary>
    /// 未识别/未归类的表达式。回编译时**原样发回去**——
    /// 丢掉会让后面所有指令错位，比保留更糟。
    /// </summary>
    public sealed record RawNode(KismetExpression Expression) : KNode($"<{Expression.Inst}>");

    private static RawNode Raw(KismetExpression e) => new(e);

    // ---- 控制流 ----

    private readonly record struct Block(KNode Node, int Next);

    /// <summary>
    /// 找出「底部测条件」的循环：回边 <c>Jump</c> 的目标在它自己之前，
    /// 且区间里最后一条 <c>JumpIfNot</c> 的落点正好是这条 <c>Jump</c> 的下一条。
    ///
    /// <para>
    /// 返回 循环头下标 → (测试下标, 回跳下标)。两个条件缺一不认——
    /// 宁可让外层退回扁平，也不要瞎折。注意测试下标必须**严格大于**循环头：
    /// 条件就在循环头本身的那种是「顶部测条件」，交给 <c>BuildBlock</c> 处理。
    /// </para>
    /// </summary>
    private static Dictionary<int, (int TestIndex, int JumpIndex)> FindBottomTestedLoops(
        IReadOnlyList<KismetExpression> expressions,
        uint[] offsets)
    {
        var loops = new Dictionary<int, (int TestIndex, int JumpIndex)>();

        for (var j = 0; j < expressions.Count; j++)
        {
            if (expressions[j] is not EX_Jump jump)
            {
                continue;
            }

            var header = IndexOfOffset(offsets, jump.CodeOffset);
            if (header < 0 || header >= j)
            {
                continue;
            }

            for (var k = j - 1; k > header; k--)
            {
                if (expressions[k] is not EX_JumpIfNot test)
                {
                    continue;
                }

                if (IndexOfOffset(offsets, test.CodeOffset) == j + 1)
                {
                    // 同一循环头只登记一次（外层优先），内层回边交给递归切片处理
                    loops.TryAdd(header, (k, j));
                    break;
                }
            }
        }

        return loops;
    }

    /// <summary>
    /// 从一条 EX_JumpIfNot 还原 if / loop：
    /// 条件 → JumpIfNot(退出) → 体内 → Jump(回跳或跳到末尾) → 退出落点。
    /// </summary>
    private static Block? BuildBlock(
        IReadOnlyList<KismetExpression> expressions,
        uint[] offsets,
        int start,
        EX_JumpIfNot jumpIfNot,
        DecompileOptions options)
    {
        var exit = IndexOfOffset(offsets, jumpIfNot.CodeOffset);
        if (exit < 0 || exit <= start)
        {
            options.Warn?.Invoke($"[{start}] JumpIfNot 落点无效：exit={exit} start={start} target={jumpIfNot.CodeOffset}");
            return null;
        }

        // 落点越出当前片段：目标在更外层的代码里（共享尾部/状态机）。
        // 这种情况无法用嵌套结构表达，硬猜只会让回编译的跳转偏移错位，
        // 所以放弃结构化、把这条 JumpIfNot 原样保留——外层仍然可以继续还原。
        if (exit >= expressions.Count)
        {
            options.Warn?.Invoke($"[{start}] JumpIfNot 目标越出片段（target={jumpIfNot.CodeOffset}），保留原文");
            return null;
        }

        // 体内第一条 EX_Jump 才标志着 then 分支结束（正序找，不是倒序）：
        //   Jump 落到 exit      → 没有 else
        //   Jump 落到别处      → then 结束，[jumpIndex+1, exit) 是 else 分支
        // 找与这条 JumpIfNot 配对的 EX_Jump：
        //   跳过整个 else 区间（目标 >= exit）的 → then 分支的结束跳转
        //   跳回起点之前的 → 循环回跳
        // 不能随便取第一条——体内可能还有别的跳转。
        var jumpIndex = -1;
        for (var k = start + 1; k < exit; k++)
        {
            if (expressions[k] is not EX_Jump jj)
            {
                continue;
            }

            var target = IndexOfOffset(offsets, jj.CodeOffset);

            if (target >= exit)
            {
                jumpIndex = k;
                break;
            }

            if (jumpIndex < 0 && target >= 0 && target <= start)
            {
                jumpIndex = k;
            }
        }

        // 没有 EX_Jump → 「没有 else 的 if」
        if (jumpIndex < 0)
        {
            var plainCond = ToArg(jumpIfNot.BooleanExpression, options)
                            ?? throw new InvalidOperationException(
                                $"分支条件表达式 {jumpIfNot.BooleanExpression?.Inst} 暂不支持");

            var plainBody = Decompile(Slice(expressions, start + 1, exit), options, OffAt(offsets, start + 1));
            var plainNext = exit;
            while (plainNext < expressions.Count && expressions[plainNext] is EX_Nothing)
            {
                plainNext++;
            }

            // 落点上原本有没有 EX_Nothing 决定回编译要不要补这一个占位字节
            return new Block(new IfNode(plainCond, plainBody, null, plainNext > exit), plainNext);
        }

        var jump = (EX_Jump)expressions[jumpIndex];
        var backTarget = IndexOfOffset(offsets, jump.CodeOffset);
        var cond = ToArg(jumpIfNot.BooleanExpression, options)
                   ?? throw new InvalidOperationException(
                       $"分支条件表达式 {jumpIfNot.BooleanExpression?.Inst} 暂不支持");

        // 回跳到起点之前 → 循环
        if (backTarget >= 0 && backTarget <= start)
        {
            var loopBody = Decompile(Slice(expressions, start + 1, jumpIndex), options, OffAt(offsets, start + 1));
            var loopNext = exit;
            while (loopNext < expressions.Count && expressions[loopNext] is EX_Nothing)
            {
                loopNext++;
            }

            return new Block(new LoopNode(cond, loopBody, loopNext > exit), loopNext);
        }

        var thenBody = Decompile(Slice(expressions, start + 1, jumpIndex), options, OffAt(offsets, start + 1));

        // 结构的真正结束点是 Jump 的目标——它可能越过 JumpIfNot 的落点。
        // 用 exit 当结束点会把中间那段代码整段丢掉。
        var structureEnd = backTarget > jumpIndex && backTarget <= expressions.Count
            ? backTarget
            : exit;

        var elseBody = structureEnd > jumpIndex + 1
            ? Decompile(Slice(expressions, jumpIndex + 1, structureEnd), options, OffAt(offsets, jumpIndex + 1))
            : null;

        var next = structureEnd;
        while (next < expressions.Count && expressions[next] is EX_Nothing)
        {
            next++;
        }

        var node = new IfNode(cond, thenBody, elseBody, next > structureEnd);

        return new Block(node, next);
    }

    private static List<KismetExpression> Slice(IReadOnlyList<KismetExpression> src, int from, int toExclusive)
    {
        var list = new List<KismetExpression>();
        for (var i = from; i < toExclusive && i < src.Count; i++)
        {
            list.Add(src[i]);
        }

        return list;
    }

    // ---- 偏移表 ----

    private static uint[]? BuildOffsets(IReadOnlyList<KismetExpression> expressions, Func<KismetExpression, uint>? measure, uint baseOffset = 0)
    {
        if (measure is null)
        {
            return null;
        }

        var offsets = new uint[expressions.Count];
        uint acc = baseOffset;
        for (var i = 0; i < expressions.Count; i++)
        {
            offsets[i] = acc;
            acc += measure(expressions[i]);
        }

        return offsets;
    }

    /// <summary>
    /// 把字节偏移映射回表达式下标。
    ///
    /// <para>
    /// 跳转目标理论上总是落在某条表达式的起始处，但资产里也可能出现落在两条之间
    /// （或指向末尾）的情况——那就取第一条起始偏移 &gt;= target 的表达式，
    /// 而不是直接判失败，否则整个分支结构会丢失。
    /// </para>
    /// </summary>
    /// <summary>越界安全的偏移取值（切片可能落在末尾）。</summary>
    private static uint OffAt(uint[] offsets, int index)
        => index >= 0 && index < offsets.Length ? offsets[index] : (offsets.Length > 0 ? offsets[^1] : 0);

    private static int IndexOfOffset(uint[] offsets, uint target)
    {
        for (var i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] >= target)
            {
                return i;
            }
        }

        // 越过末尾：跳到结束（跳过最后一条）
        return offsets.Length;
    }

    // ---- 表达式 → 参数 ----

    /// <summary>
    /// 把任意表达式转成可嵌套的实参（<see cref="ToArg"/> 的公开入口）。
    /// 供 ubergraph 分析这类只需要「读一个子表达式」的场景使用。
    /// </summary>
    public static KArg? ArgOf(KismetExpression? expression, DecompileOptions options)
        => ToArg(expression, options);

    private static KArg? ToArg(KismetExpression? e, DecompileOptions options) => e switch
    {
        null => null,
        EX_IntConst i => new IntArg(i.Value),
        EX_IntZero => new IntArg(0),
        EX_IntOne => new IntArg(1),
        EX_True => new BoolArg(true),
        EX_False => new BoolArg(false),
        EX_Self => new SelfArg(),
        EX_ObjectConst oc => new ObjectArg(oc.Value),
        EX_NoObject => new NoObjectArg(),
        EX_DoubleConst d => new DoubleArg(d.Value),
        EX_FloatConst f2 => new FloatArg(f2.Value),
        EX_ByteConst b => new ByteArg(b.Value),
        EX_SkipOffsetConst so => new SkipOffsetArg(so.Value),
        EX_VectorConst v => new VectorArg(v.Value),
        EX_RotationConst r => new RotationArg(r.Value),
        EX_DynamicCast dc => new DynamicCastArg(
            ToArg(dc.Target, options) ?? new NoObjectArg(),
            dc.ClassPtr),
        EX_ObjToInterfaceCast ic => new InterfaceCastArg(
            ToArg(ic.Target, options) ?? new NoObjectArg(),
            ic.ClassPtr),
        EX_MetaCast metaCast => new MetaCastArg(
            ToArg(metaCast.Target, options) ?? new NoObjectArg(),
            metaCast.ClassPtr),
        EX_ArrayGetByRef arrayGet => new ArrayGetByRefArg(
            RequireArg(arrayGet.ArrayVariable, options),
            RequireArg(arrayGet.ArrayIndex, options)),
        EX_UnicodeStringConst unicode => new UnicodeStringArg(unicode.Value),
        EX_SoftObjectConst soft => new SoftObjectArg(RequireArg(soft.Value, options)),
        EX_InstanceDelegate instanceDelegate => new InstanceDelegateArg(instanceDelegate.FunctionName),
        EX_TransformConst transform => new TransformArg(transform.Value),
        EX_MapConst mapConst => new MapConstArg(
            mapConst.KeyProperty!,
            mapConst.ValueProperty!,
            ToArgs(mapConst.Elements, options)),
        // 包装层必须保留：脱掉会让回编译少 1 字节（EX_InterfaceContext 的 token）
        EX_InterfaceContext icx => new InterfaceContextArg(RequireArg(icx.InterfaceValue, options)),
        EX_Int64Const la => new LongArg(la.Value),
        EX_PrimitiveCast pc => new PrimitiveCastArg(
            ToArg(pc.Target, options) ?? new IntArg(0),
            pc.ConversionType),
        // 含相对偏移，重建容易出错，原样保留
        EX_SwitchValue sv => new RawArg(sv),
        EX_ArrayConst ac => new ArrayConstArg(ac.InnerProperty!, ToArgs(ac.Elements, options)),
        EX_TextConst t => new TextArg(t.Value),
        EX_StructConst st => new StructConstArg(st.Struct, st.StructSize, ToArgs(st.Value, options)),
        EX_StructMemberContext sm => new StructMemberArg(
            ToArg(sm.StructExpression, options) ?? new NoObjectArg(),
            sm.StructMemberExpression!,
            PropName(sm.StructMemberExpression)),
        EX_StringConst sc => new StringArg(sc.Value),
        EX_NameConst nc => new NameArg(nc.Value),
        EX_LocalVariable lv => new PointerArg(lv.Variable!, TargetName(lv)),
        EX_LocalOutVariable lo => new PointerArg(lo.Variable!, TargetName(lo), Out: true),
        EX_InstanceVariable iv => new PointerArg(iv.Variable!, TargetName(iv), Instance: true),
        EX_DefaultVariable dv => new PointerArg(dv.Variable!, TargetName(dv), Default: true),
        // 注意：EX_LocalFinalFunction / EX_CallMath 都继承自 EX_FinalFunction，
        // 必须排在 EX_FinalFunction 之前，否则永远匹配不到。
        EX_CallMulticastDelegate mcd => new CallArg(
            options.ResolveImport?.Invoke(mcd.StackNode) ?? $"<import {mcd.StackNode?.Index}>",
            ToArgs(mcd.Parameters, options),
            CallShape.MulticastDelegate,
            StackNode: mcd.StackNode,
            Delegate: ToArg(mcd.Delegate, options)),
        EX_LocalFinalFunction lf => new CallArg(
            options.ResolveImport?.Invoke(lf.StackNode) ?? $"<import {lf.StackNode?.Index}>",
            ToArgs(lf.Parameters, options),
            CallShape.LocalFinal,
            StackNode: lf.StackNode),
        EX_CallMath cm => new CallArg(
            options.ResolveImport?.Invoke(cm.StackNode) ?? $"<import {cm.StackNode?.Index}>",
            ToArgs(cm.Parameters, options),
            CallShape.Math,
            StackNode: cm.StackNode),
        EX_FinalFunction f => new CallArg(
            options.ResolveImport?.Invoke(f.StackNode) ?? $"<import {f.StackNode?.Index}>",
            ToArgs(f.Parameters, options),
            CallShape.Final,
            StackNode: f.StackNode),
        EX_LocalVirtualFunction v => new CallArg(
            v.VirtualFunctionName?.ToString() ?? "<?>",
            ToArgs(v.Parameters, options),
            CallShape.Virtual),
        EX_VirtualFunction vf => new CallArg(
            vf.VirtualFunctionName?.ToString() ?? "<?>",
            ToArgs(vf.Parameters, options),
            CallShape.VirtualGlobal),
        EX_Context ctx => ContextArg(ctx, options),
        _ => null
    };

    /// <summary>
    /// 把 EX_Context 还原成可嵌套的实参：外层恒为 Context，
    /// 内层指令的形态放在 InnerShape（它不一定是 FinalFunction）。
    /// </summary>
    private static KArg? ContextArg(EX_Context ctx, DecompileOptions options)
    {
        var objName = TargetName(ctx.ObjectExpression);
        var objArg = ToArg(ctx.ObjectExpression, options);
        var kind = KindOf(ctx);

        switch (ctx.ContextExpression)
        {
            case EX_LocalVirtualFunction v:
                return new CallArg(
                    v.VirtualFunctionName?.ToString() ?? "<?>", ToArgs(v.Parameters, options),
                    CallShape.Context, objName, null, objArg, ctx.RValuePointer, CallShape.Virtual,
                    ContextKind: kind);

            case EX_VirtualFunction vf:
                return new CallArg(
                    vf.VirtualFunctionName?.ToString() ?? "<?>", ToArgs(vf.Parameters, options),
                    CallShape.Context, objName, null, objArg, ctx.RValuePointer, CallShape.VirtualGlobal,
                    ContextKind: kind);

            case EX_FinalFunction f:
            {
                var inner = f is EX_CallMath ? CallShape.Math
                          : f is EX_LocalFinalFunction ? CallShape.LocalFinal
                          : CallShape.Final;

                return new CallArg(
                    options.ResolveImport?.Invoke(f.StackNode) ?? $"<import {f.StackNode?.Index}>",
                    ToArgs(f.Parameters, options),
                    CallShape.Context, objName, f.StackNode, objArg, ctx.RValuePointer, inner,
                    ContextKind: kind);
            }

            case EX_InstanceVariable iv:
                // 跨对象读成员：ContextExpression 是变量而不是调用
                return new ContextMemberArg(objArg ?? new NoObjectArg(), TargetName(iv), iv.Variable!, Kind: kind);

            case EX_DefaultVariable dv:
                // 读的是类默认对象（CDO）上的默认值，形态和实例变量一样但指令不同
                return new ContextMemberArg(
                    objArg ?? new NoObjectArg(), TargetName(dv), dv.Variable!, IsDefault: true, Kind: kind);

            default:
                options.Warn?.Invoke($"ContextExpression 类型 {ctx.ContextExpression?.Inst} 未处理");
                return null;
        }
    }

    private static List<KArg> ToArgs(KismetExpression[]? parameters, DecompileOptions options)
    {
        var args = new List<KArg>();
        if (parameters is null)
        {
            return args;
        }

        foreach (var p in parameters)
        {
            var a = ToArg(p, options) ?? throw new InvalidOperationException($"参数表达式 {p.Inst} 暂不支持");
            if (a is not null)
            {
                args.Add(a);
            }
        }

        return args;
    }

    private static KNode ToLetBase(
        KismetExpression? variable,
        KismetExpression? assignment,
        AssignKind kind,
        DecompileOptions options)
    {
        var target = TargetName(variable);
        // 赋值源可能是字面量，也可能是一次读变量/调用，必须原样保留
        var value = ToArg(assignment, options) ?? new BoolArg(false);
        return new AssignBoolNode(target, value, PointerOf(variable), kind, ToArg(variable, options));
    }

    /// <summary>属性指针的名字（FFieldPath 最后一段）。</summary>
    private static string PropName(KismetPropertyPointer? pointer)
    {
        var path = pointer?.New?.Path;
        return path is { Length: > 0 } ? path[^1].ToString() : "?";
    }

    /// <summary>三种 EX_Context 形态只差令牌，还原时要记住是哪一种。</summary>
    private static ContextKind KindOf(EX_Context ctx) => ctx switch
    {
        EX_ClassContext => ContextKind.ClassContext,
        EX_Context_FailSilent => ContextKind.FailSilent,
        _ => ContextKind.Normal
    };

    /// <summary>
    /// 把 EX_Context 还原成 ContextCallNode。
    /// ContextExpression 的形态决定 InnerShape——它不一定都是 EX_FinalFunction。
    /// </summary>
    private static ContextCallNode? ContextNode(EX_Context ctx, DecompileOptions options)
    {
        var objName = TargetName(ctx.ObjectExpression);
        var objArg = ToArg(ctx.ObjectExpression, options);
        var kind = KindOf(ctx);

        switch (ctx.ContextExpression)
        {
            case EX_LocalVirtualFunction v:
                return new ContextCallNode(
                    objName, v.VirtualFunctionName?.ToString() ?? "<?>", ToArgs(v.Parameters, options),
                    ObjectArg: objArg, RValuePointer: ctx.RValuePointer, InnerShape: CallShape.Virtual,
                    ContextKind: kind);

            case EX_FinalFunction f:
            {
                var shape = f is EX_CallMath ? CallShape.Math
                          : f is EX_LocalFinalFunction ? CallShape.LocalFinal
                          : CallShape.Final;

                var name = options.ResolveImport?.Invoke(f.StackNode) ?? $"<import {f.StackNode?.Index}>";
                return new ContextCallNode(
                    objName, name, ToArgs(f.Parameters, options),
                    ObjectArg: objArg, RValuePointer: ctx.RValuePointer, InnerShape: shape,
                    StackNode: f.StackNode, ContextKind: kind);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 必须有值的参数转换。不认识的表达式直接抛异常——
    /// 静默兜底成 IntConst 会让整条表达式退化，而且看不出是哪一步丢的。
    /// </summary>
    private static KArg RequireArg(KismetExpression? e, DecompileOptions options)
        => ToArg(e, options)
           ?? throw new InvalidOperationException($"表达式 {e?.Inst ?? "null"} 暂不支持");

    /// <summary>把变量表达式还原成指针参数，供回编译原样使用。</summary>
    private static PointerArg? PointerOf(KismetExpression? e) => e switch
    {
        EX_LocalVariable lv => new PointerArg(lv.Variable!, TargetName(lv)),
        EX_LocalOutVariable lo => new PointerArg(lo.Variable!, TargetName(lo), Out: true),
        EX_InstanceVariable iv => new PointerArg(iv.Variable!, TargetName(iv), Instance: true),
        EX_DefaultVariable dv => new PointerArg(dv.Variable!, TargetName(dv), Default: true),
        _ => null
    };

    /// <summary>从变量表达式里取出变量名（FFieldPath 的最后一段）。</summary>
    private static string TargetName(KismetExpression? expression)
    {
        // 接口上下文只是包装层，名字取里面那个变量，显示才不会变成「InterfaceContext」
        if (expression is EX_InterfaceContext icx)
        {
            return TargetName(icx.InterfaceValue);
        }

        var pointer = expression switch
        {
            EX_LocalVariable lv => lv.Variable,
            EX_LocalOutVariable lo => lo.Variable,
            EX_InstanceVariable iv => iv.Variable,
            EX_DefaultVariable dv => dv.Variable,
            _ => null
        };

        var path = pointer?.New?.Path;
        if (path is { Length: > 0 })
        {
            return path[^1].ToString();
        }

        return expression?.Inst ?? "<?>";
    }
}
