using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Solstice.Kismet;

/// <summary>基本块的出口形态。</summary>
public enum BlockExit
{
    /// <summary>顺序落到下一块。</summary>
    FallThrough,

    /// <summary>无条件跳转（<c>EX_Jump</c>）。</summary>
    Jump,

    /// <summary>条件跳转（<c>EX_JumpIfNot</c>）：条件为假时跳到目标块。</summary>
    JumpIfNot
}

/// <summary>基本块：一段直线代码 + 一个出口。</summary>
public sealed record BasicBlock(
    int Index,
    uint StartOffset,
    int StartIndex,
    IReadOnlyList<KNode> Nodes,
    BlockExit Exit,
    uint? BranchTarget,
    KArg? Condition = null);

/// <summary>
/// 基本块图：当函数**无法**还原成嵌套树时（共享尾部、跨段跳转、蹦床），
/// 退而求其次的可读表示。
///
/// <para>
/// 它是同一段指令流的**划分**，不重排任何指令——所以只要把跳转目标按块标号重算，
/// 产出的字节与原函数完全一致。这是它能作为「结构化」的一条合法尝试的原因，
/// 而不是像扁平模式那样放弃结构。
/// </para>
///
/// <para>
/// 划分规则（标准基本块）：leader = 第 0 条 ∪ 所有跳转目标 ∪ 每条跳转的下一条。
/// </para>
/// </summary>
public static class BlockGraph
{
    public static IReadOnlyList<BasicBlock> Build(
        IReadOnlyList<KismetExpression> expressions,
        DecompileOptions options)
    {
        var blocks = new List<BasicBlock>();
        if (expressions.Count == 0)
        {
            return blocks;
        }

        var offsets = ComputeOffsets(expressions, options.Measure);
        var leaders = new SortedSet<int> { 0 };

        for (var i = 0; i < expressions.Count; i++)
        {
            uint? target = expressions[i] switch
            {
                EX_Jump jump => jump.CodeOffset,
                EX_JumpIfNot jumpIfNot => jumpIfNot.CodeOffset,
                _ => null
            };

            if (target is null)
            {
                continue;
            }

            var leader = offsets is null ? -1 : IndexOfOffset(offsets, target.Value);
            if (leader >= 0 && leader < expressions.Count)
            {
                leaders.Add(leader);
            }

            if (i + 1 < expressions.Count)
            {
                leaders.Add(i + 1);
            }
        }

        var ordered = leaders.ToList();
        for (var k = 0; k < ordered.Count; k++)
        {
            var from = ordered[k];
            var to = k + 1 < ordered.Count ? ordered[k + 1] : expressions.Count;
            if (from >= to)
            {
                continue;
            }

            var last = expressions[to - 1];
            var (exit, target, condition) = last switch
            {
                EX_Jump jump => (BlockExit.Jump, (uint?)jump.CodeOffset, (KArg?)null),
                EX_JumpIfNot jumpIfNot => (
                    BlockExit.JumpIfNot,
                    jumpIfNot.CodeOffset,
                    KismetDecompiler.ArgOf(jumpIfNot.BooleanExpression, options)),
                _ => (BlockExit.FallThrough, null, null)
            };

            // 终止符单独表示，不进块体（否则回编译会发两遍）
            var bodyEnd = exit == BlockExit.FallThrough ? to : to - 1;
            var slice = new List<KismetExpression>(Math.Max(0, bodyEnd - from));
            for (var i = from; i < bodyEnd; i++)
            {
                slice.Add(expressions[i]);
            }

            // 块内没有内部跳转（有跳转就会切成新块），所以用扁平模式反编译即可
            var body = slice.Count == 0
                ? []
                : KismetDecompiler.Decompile(
                    slice,
                    options with { Flatten = true },
                    offsets is null ? 0 : offsets[from]);

            var startOffset = offsets is null ? (uint)from : offsets[from];
            blocks.Add(new BasicBlock(k, startOffset, from, body, exit, target, condition));
        }

        return blocks;
    }

    private static uint[]? ComputeOffsets(IReadOnlyList<KismetExpression> expressions, Func<KismetExpression, uint>? measure)
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

    private static int IndexOfOffset(uint[] offsets, uint target)
    {
        for (var i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] >= target)
            {
                return i;
            }
        }

        return offsets.Length;
    }
}
