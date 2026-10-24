using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Solstice.Kismet;

/// <summary>一个入口段的结束方式。</summary>
public enum UberTerminator
{
    /// <summary>以 <c>EX_Jump</c> 跳到尾声（Return）——最常见的结尾。</summary>
    JumpToEpilogue,

    /// <summary>以 <c>EX_PopExecutionFlow</c> 回到压栈时登记的尾声。</summary>
    PopExecutionFlow,

    /// <summary>以 <c>EX_PopExecutionFlowIfNot</c> 条件性回到尾声（Delay/等待类）。</summary>
    PopExecutionFlowIfNot,

    /// <summary>以 <c>EX_Jump</c> 跳到另一个入口段（跨段跳转，状态机的典型特征）。</summary>
    JumpToOtherSection,

    /// <summary>直接落到尾声，没有终止跳转。</summary>
    FallsThrough
}

/// <summary>ubergraph 里的一个入口段。<see cref="StartOffset"/> 就是调用方要传给 EntryPoint 的那个值。</summary>
public sealed record UberSection(
    int Ordinal,
    int StartIndex,
    uint StartOffset,
    uint EndOffset,
    IReadOnlyList<KNode> Nodes,
    UberTerminator Terminator,
    uint? BranchTarget,
    KArg? TerminatorCondition = null);

/// <summary>
/// Ubergraph（<c>ExecuteUbergraph_*</c>）的结构分析。
///
/// <para>
/// 这类函数的形状是固定的：<b>调度头</b>（把 EntryPoint 当字节偏移直接跳过去）
/// → 若干<b>入口段</b>（每个事件一段，段尾跳到尾声） → <b>尾声</b>（Return）。
/// </para>
///
/// <para>
/// <b>重要边界</b>：入口段起始偏移这张表**不在字节码里**，它存在 UClass 的事件映射里
/// （由编辑器在烘焙时写入）。所以这里只能靠段尾终止符反推「段边界」；
/// 真正的 entry 序号 ↔ 偏移对应关系，外部必须自己维护——这正是重新烘焙时
/// 需要调用方提供 Flag 的原因。
/// </para>
/// </summary>
public sealed record UberGraphAnalysis(
    KArg? DispatchExpression,
    int DispatchIndex,
    bool PushesEpilogue,
    uint EpilogueOffset,
    int EpilogueIndex,
    IReadOnlyList<UberSection> Sections,
    IReadOnlyList<string> Notes);

public static class UberGraph
{
    /// <summary>分析 ubergraph；不是 ubergraph 形状时返回 null。</summary>
    public static UberGraphAnalysis? Analyze(IReadOnlyList<KismetExpression> bytecode, DecompileOptions options)
    {
        if (bytecode.Count < 3 || options.Measure is null)
        {
            return null;
        }

        var offsets = new uint[bytecode.Count];
        uint acc = 0;
        for (var i = 0; i < bytecode.Count; i++)
        {
            offsets[i] = acc;
            acc += options.Measure(bytecode[i]);
        }

        // 调度头：EX_ComputedJump（可能前面还有一条 EX_PushExecutionFlow 登记尾声）。
        // 必须出现在最前面两条之内——UE 的 ubergraph 调度头永远在函数开头，
        // 放宽松会把普通函数里的 ComputedJump 误判成 ubergraph。
        var dispatchIndex = -1;
        for (var i = 0; i < Math.Min(2, bytecode.Count); i++)
        {
            if (bytecode[i] is EX_ComputedJump)
            {
                dispatchIndex = i;
                break;
            }
        }

        if (dispatchIndex < 0 || bytecode[dispatchIndex] is not EX_ComputedJump computedJump)
        {
            return null;
        }

        var notes = new List<string>();
        var pushesEpilogue = dispatchIndex > 0 && bytecode[dispatchIndex - 1] is EX_PushExecutionFlow;

        // 尾声：函数末尾的 EX_Return
        var epilogueIndex = -1;
        for (var i = bytecode.Count - 1; i > dispatchIndex; i--)
        {
            if (bytecode[i] is EX_Return)
            {
                epilogueIndex = i;
                break;
            }
        }

        if (epilogueIndex < 0)
        {
            return null;
        }

        var epilogueOffset = offsets[epilogueIndex];

        // 有 Push 时核对它登记的地址确实是尾声，否则只记录一条说明
        if (pushesEpilogue && bytecode[dispatchIndex - 1] is EX_PushExecutionFlow push)
        {
            if (push.PushingAddress != epilogueOffset)
            {
                notes.Add($"PushExecutionFlow 登记 {push.PushingAddress}，但尾声在 {epilogueOffset}");
            }
        }

        var sections = new List<UberSection>();

        // 基本块划分：leader = 调度头之后的第一条 ∪ 所有跳转目标。
        // 不能「遇到终止符就切段」——段尾连续出现两条 Jump 时会切出 0 节点的假入口
        // （BP_Chat 的 offset 721/726 就是这种蹦床跳转）。
        var leaders = new SortedSet<int> { dispatchIndex + 1 };
        for (var i = dispatchIndex + 1; i < epilogueIndex; i++)
        {
            uint? target = bytecode[i] switch
            {
                EX_Jump jump => jump.CodeOffset,
                EX_JumpIfNot ifNot => ifNot.CodeOffset,
                _ => null
            };

            if (target is { } t)
            {
                var leader = IndexOfOffset(offsets, t);
                if (leader > dispatchIndex + 1 && leader < epilogueIndex)
                {
                    leaders.Add(leader);
                }
            }
        }

        var ordered = leaders.ToList();
        for (var k = 0; k < ordered.Count; k++)
        {
            var from = ordered[k];
            var to = k + 1 < ordered.Count ? ordered[k + 1] : epilogueIndex;
            if (from >= to)
            {
                continue;
            }

            var last = bytecode[to - 1];
            var terminator = TerminatorOf(last, epilogueOffset, out var targetOffset);

            // 终止符单独存在段尾，不进段体——否则回编译会把它发两遍
            var bodyEnd = terminator is null ? to : to - 1;
            var condition = (last as EX_PopExecutionFlowIfNot) is { } popIfNot
                ? KismetDecompiler.ArgOf(popIfNot.BooleanExpression, options)
                : null;

            sections.Add(MakeSection(
                bytecode, options, offsets, k, from, bodyEnd, to,
                terminator ?? UberTerminator.FallsThrough, targetOffset, condition));
        }

        return new UberGraphAnalysis(
            KismetDecompiler.ArgOf(computedJump.CodeOffsetExpression, options),
            dispatchIndex,
            pushesEpilogue,
            epilogueOffset,
            epilogueIndex,
            sections,
            notes);
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

    private static UberTerminator? TerminatorOf(KismetExpression e, uint epilogueOffset, out uint? target)
    {
        target = null;
        switch (e)
        {
            case EX_Jump jump when jump.CodeOffset == epilogueOffset:
                target = jump.CodeOffset;
                return UberTerminator.JumpToEpilogue;

            case EX_Jump jump:
                target = jump.CodeOffset;
                return UberTerminator.JumpToOtherSection;

            case EX_PopExecutionFlow:
                return UberTerminator.PopExecutionFlow;

            case EX_PopExecutionFlowIfNot:
                return UberTerminator.PopExecutionFlowIfNot;

            default:
                return null;
        }
    }

    private static UberSection MakeSection(
        IReadOnlyList<KismetExpression> bytecode,
        DecompileOptions options,
        uint[] offsets,
        int ordinal,
        int from,
        int bodyEnd,
        int toExclusive,
        UberTerminator terminator,
        uint? target,
        KArg? condition)
    {
        var slice = new List<KismetExpression>(Math.Max(0, bodyEnd - from));
        for (var i = from; i < bodyEnd; i++)
        {
            slice.Add(bytecode[i]);
        }

        var body = slice.Count == 0 ? [] : KismetDecompiler.Decompile(slice, options, offsets[from]);
        return new UberSection(
            ordinal,
            from,
            offsets[from],
            toExclusive < offsets.Length ? offsets[toExclusive] : offsets[^1],
            body,
            terminator,
            target,
            condition);
    }
}
