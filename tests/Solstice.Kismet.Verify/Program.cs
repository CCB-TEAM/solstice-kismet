using Solstice.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

// 反编译器的自检：手工构造表达式树，不依赖任何游戏资产。
// 退出码 0 = 全部通过。

var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  " + detail)}");
    if (!ok) { failures++; }
}

string? ResolveImport(FPackageIndex idx) => idx.Index switch
{
    -19 => "IsValid",
    -18 => "EqualEqual_ByteByte",
    _ => null
};

// 与编译器一致的尺寸表（真实长度要用 GetSize(asset)，这里给等价常量）
uint Measure(KismetExpression e) => e switch
{
    EX_IntConst => 5,
    EX_True or EX_False or EX_Nothing or EX_Return or EX_EndOfScript => 1,
    EX_Jump => 5,
    EX_JumpIfNot => 5,   // 条件内嵌，这里用固定值使测试可控
    EX_FinalFunction => 5,
    _ => 1
};

var options = new DecompileOptions(Measure, ResolveImport);

// ---- 1. 扁平序列 ----
{
    var exprs = new KismetExpression[]
    {
        new EX_FinalFunction { StackNode = new FPackageIndex(-19), Parameters = [] },
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    Check("扁平: 节点数", nodes.Count == 2, $"{nodes.Count}");
    Check("扁平: 首节点是调用", nodes[0] is CallNode, nodes[0].GetType().Name);
    Check("扁平: 函数名解析", nodes[0] is CallNode c && c.Function == "IsValid", (nodes[0] as CallNode)?.Function);
    Check("扁平: 末节点是返回", nodes[1] is ReturnNode);
}

// ---- 2. 带参数的调用 ----
{
    var exprs = new KismetExpression[]
    {
        new EX_FinalFunction
        {
            StackNode = new FPackageIndex(-18),
            Parameters = [new EX_IntConst { Value = 7 }, new EX_True()]
        },
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    var call = nodes[0] as CallNode;
    Check("参数: 个数", call?.Args.Count == 2, $"{call?.Args.Count}");
    Check("参数: 首参是常量 7", call?.Args[0] is IntArg { Value: 7 });
    Check("参数: 次参是布尔真", call?.Args[1] is BoolArg { Value: true });
}

// ---- 3. if 结构 ----
// 布局: [0]JumpIfNot(5) [1]Call(5) [2]Jump(5) [3]Nothing(1) [4]Return(1) [5]EndOfScript(1)
// 偏移:  0           5         10        15          16           17
{
    var exprs = new KismetExpression[]
    {
        new EX_JumpIfNot { CodeOffset = 15, BooleanExpression = new EX_True() },
        new EX_FinalFunction { StackNode = new FPackageIndex(-19), Parameters = [] },
        new EX_Jump { CodeOffset = 16 },
        new EX_Nothing(),
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    Check("if: 节点数", nodes.Count == 2, $"{nodes.Count}");
    Check("if: 首节点是条件", nodes[0] is IfNode, nodes[0].GetType().Name);

    if (nodes[0] is IfNode ifNode)
    {
        Check("if: 体内一条", ifNode.Then.Count == 1, $"{ifNode.Then.Count}");
        Check("if: 体内是调用", ifNode.Then[0] is CallNode);
    }

    Check("if: 之后是返回", nodes[1] is ReturnNode);
}

// ---- 4. loop 结构（回跳）----
// 偏移: [0]0 [1]5 [2]10 [3]15 [4]16 [5]17
// JumpIfNot 落点必须落在偏移表内（15 = 第 3 条）。写 20 这种越界值等于造假的字节码：
// 真实资产里落点越界意味着目标在更外层，反编译器会（正确地）放弃结构化。
{
    var exprs = new KismetExpression[]
    {
        new EX_JumpIfNot { CodeOffset = 15, BooleanExpression = new EX_True() },
        new EX_FinalFunction { StackNode = new FPackageIndex(-19), Parameters = [] },
        new EX_Jump { CodeOffset = 0 },   // 跳回起点 → 循环
        new EX_Nothing(),
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    Check("loop: 首节点是循环", nodes[0] is LoopNode, nodes[0].GetType().Name);
    Check("loop: 体内一条", nodes[0] is LoopNode lp && lp.Body.Count == 1);
    Check("loop: 记录落点占位", nodes[0] is LoopNode { EndsWithNothing: true });
}

// ---- 5. 没有 measure 时不炸，跳转退化为原文 ----
{
    var exprs = new KismetExpression[]
    {
        new EX_JumpIfNot { CodeOffset = 0, BooleanExpression = new EX_True() },
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, new DecompileOptions());
    Check("无 measure: 不抛异常", true, $"{nodes.Count} 节点");
    Check("无 measure: 跳转保留为原文", nodes[0].GetType().Name.Contains("Raw") || nodes[0].Title.StartsWith("<"), nodes[0].Title);
}

// ---- 6. EX_InterfaceContext 的包装层必须原样保留 ----
// 它在 EX_Context.ObjectExpression 里包住接口变量。脱掉后回编译只剩里面的
// EX_LocalVariable，每条少 1 字节（BP_LevelManager.ExecuteUbergraph 曾因此 37 vs 36）。
{
    var exprs = new KismetExpression[]
    {
        new EX_FinalFunction
        {
            StackNode = new FPackageIndex(-19),
            Parameters =
            [
                new EX_InterfaceContext
                {
                    InterfaceValue = new EX_ObjectConst { Value = new FPackageIndex(-5) }
                }
            ]
        },
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    var call = nodes[0] as CallNode;
    Check("接口上下文: 反编译保留包装", call?.Args[0] is InterfaceContextArg, call?.Args[0].GetType().Name);

    var rebuilt = KismetCompiler.CompileToExpressions(nodes, CompileContext.Empty);
    var param = (rebuilt[0] as EX_FinalFunction)?.Parameters?.FirstOrDefault();
    Check("接口上下文: 回编译仍是 EX_InterfaceContext", param is EX_InterfaceContext, param?.Inst);
    Check("接口上下文: 内层对象常量未丢",
        param is EX_InterfaceContext { InterfaceValue: EX_ObjectConst { Value.Index: -5 } });
}

// ---- 7. 执行流栈 / 计算跳转 / 数组赋值：从 Raw 提升成可读节点后仍要字节一致 ----
{
    var exprs = new KismetExpression[]
    {
        new EX_PushExecutionFlow { PushingAddress = 209 },
        new EX_PopExecutionFlowIfNot { BooleanExpression = new EX_True() },
        new EX_ComputedJump { CodeOffsetExpression = new EX_IntConst { Value = 3 } },
        new EX_PopExecutionFlow(),
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    Check("执行流: Push 还原成节点", nodes[0] is PushFlowNode { Target: 209 }, nodes[0].GetType().Name);
    Check("执行流: Push 标题带偏移", nodes[0].Title.Contains("209"), nodes[0].Title);
    Check("执行流: PopIfNot 保留条件", nodes[1] is PopFlowIfNotNode { Condition: BoolArg { Value: true } });
    Check("计算跳转: 还原成节点", nodes[2] is ComputedJumpNode { Target: IntArg { Value: 3 } }, nodes[2].Title);
    Check("执行流: Pop 还原成节点", nodes[3] is PopFlowNode);

    var rebuilt = KismetCompiler.CompileToExpressions(nodes, CompileContext.Empty);
    Check("执行流: 回编译 Push 偏移不变",
        rebuilt[0] is EX_PushExecutionFlow { PushingAddress: 209 });
    Check("执行流: 回编译 PopIfNot 条件不变",
        rebuilt[1] is EX_PopExecutionFlowIfNot { BooleanExpression: EX_True });
    Check("计算跳转: 回编译目标不变",
        rebuilt[2] is EX_ComputedJump { CodeOffsetExpression: EX_IntConst { Value: 3 } });
    Check("执行流: 回编译 Pop 不变", rebuilt[3] is EX_PopExecutionFlow);
}

// ---- 8. EX_SetArray：元素与内层索引都要原样带回 ----
{
    var exprs = new KismetExpression[]
    {
        new EX_SetArray
        {
            AssigningProperty = new EX_ObjectConst { Value = new FPackageIndex(-4) },
            ArrayInnerProp = new FPackageIndex(-7),
            Elements = [new EX_IntConst { Value = 11 }, new EX_IntConst { Value = 22 }]
        },
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    var setArray = nodes[0] as SetArrayNode;
    Check("数组赋值: 还原成节点", setArray is not null, nodes[0].GetType().Name);
    Check("数组赋值: 元素个数", setArray?.Elements.Count == 2, $"{setArray?.Elements.Count}");

    var rebuilt = KismetCompiler.CompileToExpressions(nodes, CompileContext.Empty);
    var back = rebuilt[0] as EX_SetArray;
    Check("数组赋值: 回编译内层索引不变", back?.ArrayInnerProp?.Index == -7, $"{back?.ArrayInnerProp?.Index}");
    Check("数组赋值: 回编译元素不变",
        back?.Elements is [EX_IntConst { Value: 11 }, EX_IntConst { Value: 22 }]);
}

// ---- 9. Ubergraph 结构分析：调度头 + 基本块 + 尾声 ----
// 布局（自定尺寸）：
//   [0] Push(5)@0      -> 尾声 31
//   [1] ComputedJump(6)@5
//   [2] IntConst(5)@11     ┐ 段 0
//   [3] Jump(5)@16 -> 31   ┘ (JumpToEpilogue)
//   [4] IntConst(5)@21     ┐ 段 1（被 [5] 的跳转指向，所以是 leader）
//   [5] Jump(5)@26 -> 21   ┘ (JumpToOtherSection)
//   [6] Return(1)@31        尾声
//   [7] EndOfScript(1)@32
{
    static uint UberMeasure(KismetExpression x) => x switch
    {
        EX_PushExecutionFlow => 5,
        EX_ComputedJump => 6,
        EX_IntConst or EX_Jump => 5,
        _ => 1
    };

    var uberOptions = new DecompileOptions(UberMeasure, ResolveImport, null);

    var exprs = new KismetExpression[]
    {
        new EX_PushExecutionFlow { PushingAddress = 31 },
        new EX_ComputedJump { CodeOffsetExpression = new EX_IntConst { Value = 11 } },
        new EX_IntConst { Value = 1 },
        new EX_Jump { CodeOffset = 31 },
        new EX_IntConst { Value = 2 },
        new EX_Jump { CodeOffset = 21 },
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var uber = UberGraph.Analyze(exprs, uberOptions);
    Check("ubergraph: 识别调度头", uber?.DispatchIndex == 1, $"{uber?.DispatchIndex}");
    Check("ubergraph: 调度表达式", uber?.DispatchExpression is IntArg { Value: 11 });
    Check("ubergraph: Push 登记尾声", uber is { PushesEpilogue: true, EpilogueOffset: 31 });
    Check("ubergraph: 基本块个数", uber?.Sections.Count == 2, $"{uber?.Sections.Count}");
    Check("ubergraph: 段 0 结束于尾声",
        uber?.Sections[0] is { Terminator: UberTerminator.JumpToEpilogue, StartOffset: 11 });
    Check("ubergraph: 段 1 是跨段跳转",
        uber?.Sections[1] is { Terminator: UberTerminator.JumpToOtherSection, BranchTarget: 21 });

    // 不是 ubergraph 的函数不应该被误判
    Check("ubergraph: 普通函数不误判",
        UberGraph.Analyze([new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()], uberOptions) is null);
}

Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;
