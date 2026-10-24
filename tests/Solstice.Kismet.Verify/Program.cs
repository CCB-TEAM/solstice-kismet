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
{
    var exprs = new KismetExpression[]
    {
        new EX_JumpIfNot { CodeOffset = 20, BooleanExpression = new EX_True() },
        new EX_FinalFunction { StackNode = new FPackageIndex(-19), Parameters = [] },
        new EX_Jump { CodeOffset = 0 },   // 跳回起点 → 循环
        new EX_Nothing(),
        new EX_Return { ReturnExpression = new EX_Nothing() },
        new EX_EndOfScript()
    };

    var nodes = KismetDecompiler.Decompile(exprs, options);
    Check("loop: 首节点是循环", nodes[0] is LoopNode, nodes[0].GetType().Name);
    Check("loop: 体内一条", nodes[0] is LoopNode lp && lp.Body.Count == 1);
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

Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;
