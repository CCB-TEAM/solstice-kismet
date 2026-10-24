using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace Solstice.Kismet;

/// <summary>
/// 反编译所需的解析回调。都拿不到时只能还原与资产无关的部分。
/// </summary>
public sealed record DecompileOptions(
    Func<KismetExpression, uint>? Measure = null,
    Func<FPackageIndex, string?>? ResolveImport = null);

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
    public static IReadOnlyList<KNode> Decompile(
        IReadOnlyList<KismetExpression> expressions,
        DecompileOptions? options = null)
    {
        options ??= new DecompileOptions();
        var offsets = BuildOffsets(expressions, options.Measure);

        var nodes = new List<KNode>();
        var i = 0;

        while (i < expressions.Count)
        {
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
                    nodes.Add(new AssignNode(TargetName(let.Variable), ToArg(let.Expression, options) ?? new IntArg(0), PointerOf(let.Variable)));
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
                        ToArg(persistent.AssignmentExpression, options) ?? new IntArg(0),
                        PropName(persistent.DestinationProperty)));
                    i++;
                    continue;

                case EX_Return:
                    nodes.Add(new ReturnNode());
                    i++;
                    continue;

                case EX_JumpIfNot jumpIfNot when offsets is not null:
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

                // 注意：EX_LocalFinalFunction / EX_CallMath 都继承自 EX_FinalFunction，
                // 必须排在它之前，否则永远匹配不到。
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

                case EX_Context ctx when ctx.ContextExpression is EX_FinalFunction inner:
                {
                    var name = options.ResolveImport?.Invoke(inner.StackNode) ?? $"<import {inner.StackNode?.Index}>";
                    nodes.Add(new ContextCallNode(
                        TargetName(ctx.ObjectExpression),
                        name,
                        ToArgs(inner.Parameters, options),
                        ObjectArg: ToArg(ctx.ObjectExpression, options),
                        RValuePointer: ctx.RValuePointer));
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

    private sealed record RawNode(KismetExpression Expression) : KNode($"<{Expression.Inst}>");

    private static RawNode Raw(KismetExpression e) => new(e);

    // ---- 控制流 ----

    private readonly record struct Block(KNode Node, int Next);

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
            return null;
        }

        // 体内最后一条应该是 EX_Jump
        var jumpIndex = -1;
        for (var k = exit - 1; k > start; k--)
        {
            if (expressions[k] is EX_Jump)
            {
                jumpIndex = k;
                break;
            }
        }

        if (jumpIndex < 0)
        {
            return null;
        }

        var jump = (EX_Jump)expressions[jumpIndex];
        var backTarget = IndexOfOffset(offsets, jump.CodeOffset);

        var body = Decompile(Slice(expressions, start + 1, jumpIndex), options);
        var cond = ToArg(jumpIfNot.BooleanExpression, options) ?? new IntArg(0);

        // 回跳到起点之前 → 循环；否则 → 条件分支
        var isLoop = backTarget >= 0 && backTarget <= start;

        KNode node = isLoop
            ? new LoopNode(cond, body)
            : new IfNode(cond, body);

        // 跳过退出落点处的占位
        var next = exit;
        while (next < expressions.Count && expressions[next] is EX_Nothing)
        {
            next++;
        }

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

    private static uint[]? BuildOffsets(IReadOnlyList<KismetExpression> expressions, Func<KismetExpression, uint>? measure)
    {
        if (measure is null)
        {
            return null;
        }

        var offsets = new uint[expressions.Count];
        uint acc = 0;
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

    private static KArg? ToArg(KismetExpression? e, DecompileOptions options) => e switch
    {
        null => null,
        EX_IntConst i => new IntArg(i.Value),
        EX_IntZero => new IntArg(0),
        EX_IntOne => new IntArg(1),
        EX_True => new BoolArg(true),
        EX_False => new BoolArg(false),
        EX_NameConst nc => new NameArg(nc.Value),
        EX_LocalVariable lv => new PointerArg(lv.Variable!, TargetName(lv)),
        EX_LocalOutVariable lo => new PointerArg(lo.Variable!, TargetName(lo), Out: true),
        EX_InstanceVariable iv => new PointerArg(iv.Variable!, TargetName(iv), Instance: true),
        // 注意：EX_LocalFinalFunction / EX_CallMath 都继承自 EX_FinalFunction，
        // 必须排在 EX_FinalFunction 之前，否则永远匹配不到。
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
        EX_Context ctx when ctx.ContextExpression is EX_FinalFunction inner => new CallArg(
            options.ResolveImport?.Invoke(inner.StackNode) ?? $"<import {inner.StackNode?.Index}>",
            ToArgs(inner.Parameters, options),
            CallShape.Context,
            TargetName(ctx.ObjectExpression),
            inner.StackNode,
            ToArg(ctx.ObjectExpression, options),
            ctx.RValuePointer),
        _ => null
    };

    private static List<KArg> ToArgs(KismetExpression[]? parameters, DecompileOptions options)
    {
        var args = new List<KArg>();
        if (parameters is null)
        {
            return args;
        }

        foreach (var p in parameters)
        {
            var a = ToArg(p, options);
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
        return new AssignBoolNode(target, value, PointerOf(variable), kind);
    }

    /// <summary>属性指针的名字（FFieldPath 最后一段）。</summary>
    private static string PropName(KismetPropertyPointer? pointer)
    {
        var path = pointer?.New?.Path;
        return path is { Length: > 0 } ? path[^1].ToString() : "?";
    }

    /// <summary>把变量表达式还原成指针参数，供回编译原样使用。</summary>
    private static PointerArg? PointerOf(KismetExpression? e) => e switch
    {
        EX_LocalVariable lv => new PointerArg(lv.Variable!, TargetName(lv)),
        EX_LocalOutVariable lo => new PointerArg(lo.Variable!, TargetName(lo), Out: true),
        EX_InstanceVariable iv => new PointerArg(iv.Variable!, TargetName(iv), Instance: true),
        _ => null
    };

    /// <summary>从变量表达式里取出变量名（FFieldPath 的最后一段）。</summary>
    private static string TargetName(KismetExpression? expression)
    {
        var pointer = expression switch
        {
            EX_LocalVariable lv => lv.Variable,
            EX_LocalOutVariable lo => lo.Variable,
            EX_InstanceVariable iv => iv.Variable,
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
