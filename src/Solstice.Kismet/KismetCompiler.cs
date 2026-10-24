using UAssetAPI.UnrealTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Solstice.Kismet;

/// <summary>
/// UKAPI 最小编译器：平铺节点 → EX_ 指令序列。
///
/// <para>
/// <b>跳转用字节偏移，不是表达式下标。</b>实测 card_brawl_test1 的 ExecuteUbergraph：
/// JumpIfNot 的 CodeOffset 是 199 / 619，Jump 的是 1433，而函数只有 56 条表达式。
/// 所以这里先按占位偏移生成，再用每条指令的编码长度累加回填。
/// </para>
///
/// <para>
/// 长度表来自 UAssetAPI 的 <c>KismetExpression.GetSize</c>：Jump=5（1 opcode + 4 偏移）。
/// </para>
/// </summary>
public static class KismetCompiler
{
    private sealed class Raw
    {
        public string Opcode = "";
        public string Operand = "";
        public string? Label;
        public string? TargetLabel;
        public int Size = 1;
    }

    /// <summary>各操作码的编码字节数（不含跳转偏移以外的可变部分）。</summary>
    private static int SizeOf(string opcode) => opcode switch
    {
        "EX_LocalVariable" or "EX_InstanceVariable" => 9,  // opcode + FKismetPropertyPointer(8)
        "EX_IntConst" => 5,                                 // opcode + int32
        "EX_FinalFunction" or "EX_VirtualFunction" => 5,    // opcode + int32 package index
        "EX_Jump" => 5,                                     // opcode + int32 偏移
        "EX_JumpIfNot" => 5,
        _ => 1                                              // Nothing / Return / EndOfScript / EndFunctionParms
    };

    public static IReadOnlyList<KismetInstruction> Compile(IReadOnlyList<KNode> nodes)
    {
        var raw = new List<Raw>();
        var labelSeq = 0;

        void Emit(string opcode, string operand = "", string? label = null, string? target = null)
            => raw.Add(new Raw { Opcode = opcode, Operand = operand, Label = label, TargetLabel = target, Size = SizeOf(opcode) });

        void EmitArg(KArg arg)
        {
            switch (arg)
            {
                case IntArg i: Emit("EX_IntConst", i.Value.ToString()); break;
                case LocalArg l: Emit("EX_LocalVariable", l.Name); break;
                default: Emit("EX_Nothing", "<unsupported>"); break;
            }
        }

        void EmitNode(KNode node)
        {
            switch (node)
            {
                case CallNode c:
                    foreach (var a in c.Args) EmitArg(a);
                    Emit("EX_FinalFunction", c.Function);
                    Emit("EX_EndFunctionParms");
                    break;

                case IfNode f:
                {
                    var elseLabel = $"L{labelSeq++}";
                    var endLabel = $"L{labelSeq++}";
                    EmitArg(f.Condition);
                    Emit("EX_JumpIfNot", "", null, elseLabel);
                    foreach (var n in f.Then) EmitNode(n);
                    Emit("EX_Jump", "", null, endLabel);
                    Emit("EX_Nothing", "", elseLabel);
                    Emit("EX_Nothing", "", endLabel);
                    break;
                }

                case ReturnNode:
                    Emit("EX_Return");
                    break;
            }
        }

        foreach (var n in nodes) EmitNode(n);
        Emit("EX_EndOfScript");

        // 第一遍：算出每条指令的起始字节偏移
        var offsets = new int[raw.Count];
        var acc = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            offsets[i] = acc;
            acc += raw[i].Size;
        }

        var labelOffset = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
            if (raw[i].Label is { } l) labelOffset[l] = offsets[i];

        var result = new KismetInstruction[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            int? target = r.TargetLabel is { } tl && labelOffset.TryGetValue(tl, out var o) ? o : null;
            result[i] = new KismetInstruction(r.Opcode, r.Operand, target);
        }

        return result;
    }

    /// <summary>
    /// 编译成 UAssetAPI 的表达式对象，可直接交给 <c>ExpressionSerializer</c> 序列化。
    ///
    /// <para>
    /// 当前覆盖常量、返回、结束。变量与调用需要属性指针 / FPackageIndex，
    /// 由后续阶段接入（见 <see cref="ExpressionWalker.BuildVariableIndex"/>）。
    /// </para>
    /// </summary>
    /// <summary>
    /// 用 <see cref="CompileContext"/> 收敛所有资产本地依赖的重载。
    /// 新代码应优先使用这个签名，避免参数表继续膨胀。
    /// </summary>
    public static IReadOnlyList<KismetExpression> CompileToExpressions(
        IReadOnlyList<KNode> nodes,
        CompileContext context)
        => CompileToExpressions(nodes, context.Variables, context.Functions, context.Measure, context.ResolveName);

    /// <summary>重新烘焙 ubergraph 的结果：表达式 + 需要写回 UClass 的入口偏移表。</summary>
    public sealed record UbergraphBakeResult(
        IReadOnlyList<KismetExpression> Expressions,
        IReadOnlyList<uint> EntryOffsets,
        uint EpilogueOffset);

    /// <summary>
    /// 重新烘焙 ubergraph，并给出新的**入口偏移表**。
    ///
    /// <para>
    /// 入口表（事件 → 字节偏移）不在字节码里，原表存在 UClass 的事件引用中。
    /// 重新烘焙后偏移一般会变，返回的 <see cref="UbergraphBakeResult.EntryOffsets"/>
    /// 必须按同样的顺序写回去，否则运行时 <c>ComputedJump</c> 会跳到错误位置。
    /// </para>
    ///
    /// <para>段内容与顺序不变时，偏移表应与原始一致（这一点由自检锁住）。</para>
    /// </summary>
    public static UbergraphBakeResult? BakeUbergraph(IReadOnlyList<KNode> nodes, CompileContext context)
    {
        var uber = nodes.OfType<UberGraphNode>().FirstOrDefault();
        if (uber is null || context.Measure is null)
        {
            return null;
        }

        var trace = new BakeTrace();
        var expressions = CompileCore(
            nodes, context.Variables, context.Functions, context.Measure, context.ResolveName, trace);

        var offsets = new uint[expressions.Count + 1];
        uint acc = 0;
        for (var i = 0; i < expressions.Count; i++)
        {
            offsets[i] = acc;
            acc += context.Measure(expressions[i]);
        }

        offsets[expressions.Count] = acc;

        var entries = trace.SectionStarts
            .Select(i => i < offsets.Length ? offsets[i] : acc)
            .ToArray();

        var epilogue = trace.EpilogueIndex >= 0 && trace.EpilogueIndex < offsets.Length
            ? offsets[trace.EpilogueIndex]
            : acc;

        return new UbergraphBakeResult(expressions, entries, epilogue);
    }

    /// <summary>烘焙过程中收集的观察点（段起始下标、尾声下标）。</summary>
    private sealed class BakeTrace
    {
        public List<int> SectionStarts { get; } = new();

        public int EpilogueIndex { get; set; } = -1;
    }

    public static IReadOnlyList<KismetExpression> CompileToExpressions(
        IReadOnlyList<KNode> nodes,
        IReadOnlyDictionary<string, KismetPropertyPointer>? variables = null,
        IReadOnlyDictionary<string, FPackageIndex>? functions = null,
        Func<KismetExpression, uint>? measure = null,
        Func<string, FName>? resolveName = null)
        => CompileCore(nodes, variables, functions, measure, resolveName, null);

    private static IReadOnlyList<KismetExpression> CompileCore(
        IReadOnlyList<KNode> nodes,
        IReadOnlyDictionary<string, KismetPropertyPointer>? variables,
        IReadOnlyDictionary<string, FPackageIndex>? functions,
        Func<KismetExpression, uint>? measure,
        Func<string, FName>? resolveName,
        BakeTrace? trace)
    {
        var result = new List<KismetExpression>();

        /// <summary>
        /// 把数据流参数转成表达式。支持嵌套调用（<see cref="CallArg"/>），
        /// 这样一个调用的返回值可以直接作为另一个调用的参数或对象。
        /// </summary>
        /// <summary>优先用节点保留的 StackNode，否则按名字查 Import 索引。</summary>
        FPackageIndex StackOf(string name, FPackageIndex? explicitIndex)
            => explicitIndex ?? (functions is not null && functions.TryGetValue(name, out var idx) ? idx : new FPackageIndex(0));

        /// <summary>变量表达式的三种形态：局部 / 实例 / 函数输出参数。</summary>
        static KismetExpression TargetExpr(PointerArg? kind, KismetPropertyPointer pointer)
            => kind is { Out: true } ? new EX_LocalOutVariable { Variable = pointer }
             : kind is { Default: true } ? new EX_DefaultVariable { Variable = pointer }
             : kind is { Instance: true } ? new EX_InstanceVariable { Variable = pointer }
             : new EX_LocalVariable { Variable = pointer };

        /// <summary>
        /// 按保留的形态构造 EX_Context 系列。三者字段完全相同、只有令牌不同，
        /// 发错令牌字节就对不上。
        /// </summary>
        KismetExpression MakeContext(
            ContextKind kind,
            KismetExpression objectExpression,
            KismetExpression inner,
            KismetPropertyPointer pointer)
        {
            var offset = measure?.Invoke(inner) ?? 0;
            return kind switch
            {
                ContextKind.ClassContext => new EX_ClassContext
                {
                    ObjectExpression = objectExpression,
                    ContextExpression = inner,
                    RValuePointer = pointer,
                    Offset = offset
                },
                ContextKind.FailSilent => new EX_Context_FailSilent
                {
                    ObjectExpression = objectExpression,
                    ContextExpression = inner,
                    RValuePointer = pointer,
                    Offset = offset
                },
                _ => new EX_Context
                {
                    ObjectExpression = objectExpression,
                    ContextExpression = inner,
                    RValuePointer = pointer,
                    Offset = offset
                }
            };
        }

        KismetExpression ToExpr(KArg arg)
        {
            switch (arg)
            {
                case IntArg i:
                    return new EX_IntConst { Value = i.Value };

                case BoolArg b:
                    return b.Value ? new EX_True() : new EX_False();

                case PointerArg p:
                    return TargetExpr(p, p.Pointer);

                case LocalArg l when variables is not null && variables.TryGetValue(l.Name, out var pointer):
                    // 复用现有字节码里的属性指针：FFieldPath 自带变量名与归属索引
                    return new EX_LocalVariable { Variable = pointer };

                case SelfArg:
                    return new EX_Self();

                case ObjectArg oc:
                    return new EX_ObjectConst { Value = oc.Value };

                case NoObjectArg:
                    return new EX_NoObject();

                case ContextMemberArg cm:
                {
                    // CDO 默认值读取用 EX_DefaultVariable，普通成员用 EX_InstanceVariable
                    KismetExpression member = cm.IsDefault
                        ? new EX_DefaultVariable { Variable = cm.Pointer }
                        : new EX_InstanceVariable { Variable = cm.Pointer };
                    return MakeContext(cm.Kind, ToExpr(cm.Object), member, cm.Pointer);
                }

                case DoubleArg d:
                    return new EX_DoubleConst { Value = d.Value };

                case FloatArg f2:
                    return new EX_FloatConst { Value = f2.Value };

                case ByteArg b:
                    return new EX_ByteConst { Value = b.Value };

                case SkipOffsetArg so:
                    return new EX_SkipOffsetConst { Value = so.Value };

                case VectorArg v:
                    return new EX_VectorConst { Value = v.Value };

                case RotationArg r:
                    return new EX_RotationConst { Value = r.Value };

                case DynamicCastArg dc:
                    return new EX_DynamicCast
                    {
                        ClassPtr = dc.ClassPtr,
                        Target = ToExpr(dc.Target)
                    };

                case InterfaceCastArg ic:
                    return new EX_ObjToInterfaceCast
                    {
                        ClassPtr = ic.ClassPtr,
                        Target = ToExpr(ic.Target)
                    };

                case InterfaceContextArg icx:
                    return new EX_InterfaceContext
                    {
                        InterfaceValue = ToExpr(icx.Value)
                    };

                case MetaCastArg mc:
                    return new EX_MetaCast
                    {
                        ClassPtr = mc.ClassPtr,
                        Target = ToExpr(mc.Target)
                    };

                case ArrayGetByRefArg ag:
                    return new EX_ArrayGetByRef
                    {
                        ArrayVariable = ToExpr(ag.Array),
                        ArrayIndex = ToExpr(ag.Index)
                    };

                case UnicodeStringArg us:
                    return new EX_UnicodeStringConst { Value = us.Value };

                case SoftObjectArg soft:
                    return new EX_SoftObjectConst { Value = ToExpr(soft.Value) };

                case InstanceDelegateArg idl:
                    return new EX_InstanceDelegate { FunctionName = idl.FunctionName };

                case TransformArg tr:
                    return new EX_TransformConst { Value = tr.Value };

                case MapConstArg mpc:
                    return new EX_MapConst
                    {
                        KeyProperty = mpc.Key,
                        ValueProperty = mpc.Value,
                        Elements = mpc.Elements.Select(ToExpr).ToArray()
                    };

                case LongArg la:
                    return new EX_Int64Const { Value = la.Value };

                case PrimitiveCastArg pc:
                    return new EX_PrimitiveCast
                    {
                        ConversionType = pc.Conversion,
                        Target = ToExpr(pc.Target)
                    };

                case RawArg ra:
                    // 原样发回：结构复杂的指令不做重建
                    return ra.Expression;

                case ArrayConstArg ac:
                    return new EX_ArrayConst
                    {
                        InnerProperty = ac.Inner,
                        Elements = ac.Elements.Select(ToExpr).ToArray()
                    };

                case TextArg t:
                    return new EX_TextConst { Value = t.Value };

                case StructConstArg st:
                    return new EX_StructConst
                    {
                        Struct = st.Struct,
                        StructSize = st.Size,
                        Value = st.Values.Select(ToExpr).ToArray()
                    };

                case StructMemberArg sm:
                    return new EX_StructMemberContext
                    {
                        StructMemberExpression = sm.Member,
                        StructExpression = ToExpr(sm.Struct)
                    };

                case StringArg sc:
                    return new EX_StringConst { Value = sc.Value };

                case NameArg n:
                    return new EX_NameConst { Value = n.Value };


                case CallArg c:
                {
                    var inner = c.Args.Select(ToExpr).ToArray();
                    var stack = StackOf(c.Function, c.StackNode);

                    // 外层要不要包 EX_Context，由 Shape 决定；
                    // 内层用哪个指令，由 InnerShape 决定（两者是独立的语义）。
                    var effective = c.Shape == CallShape.Context ? c.InnerShape : c.Shape;

                    KismetExpression call = effective switch
                    {
                        CallShape.Math => new EX_CallMath { StackNode = stack, Parameters = inner },
                        CallShape.LocalFinal => new EX_LocalFinalFunction { StackNode = stack, Parameters = inner },
                        CallShape.Virtual when resolveName is not null => new EX_LocalVirtualFunction
                        {
                            VirtualFunctionName = resolveName(c.Function),
                            Parameters = inner
                        },
                        CallShape.VirtualGlobal when resolveName is not null => new EX_VirtualFunction
                        {
                            VirtualFunctionName = resolveName(c.Function),
                            Parameters = inner
                        },
                        CallShape.MulticastDelegate => new EX_CallMulticastDelegate
                        {
                            StackNode = stack,
                            Parameters = inner,
                            Delegate = c.Delegate is { } dg ? ToExpr(dg) : new EX_Nothing()
                        },
                        _ => new EX_FinalFunction { StackNode = stack, Parameters = inner }
                    };

                    if (c.Shape != CallShape.Context)
                    {
                        return call;
                    }

                    // 对象可以是变量（有指针），也可以是别的表达式（取成员 / 调用返回值）
                    var objPtr = c.ObjectArg is PointerArg pa
                        ? pa.Pointer
                        : c.ObjectVariable is { } objVar
                          && variables is not null
                          && variables.TryGetValue(objVar, out var found)
                            ? found
                            : null;

                    // 优先用反编译保留的原始指针
                    objPtr = c.RValuePointer ?? objPtr;
                    if (objPtr is null)
                    {
                        return call;
                    }

                    return MakeContext(
                        c.ContextKind,
                        c.ObjectArg is { } oa ? ToExpr(oa) : new EX_LocalVariable { Variable = objPtr },
                        call,
                        objPtr);
                }

                default:
                    // 不认识的参数类型必须报出来——静默变成 EX_Nothing 会让整条表达式退化，
                    // 而且看不出是哪一步丢的。
                    throw new InvalidOperationException($"参数类型 {arg.GetType().Name} 无法编译成表达式");
            }
        }

        void EmitArg(KArg arg) => result.Add(ToExpr(arg));

        KismetExpression[] BuildArgs(CallNode c) => c.Args.Select(ToExpr).ToArray();

        var patches = new List<(KismetExpression Jump, int Target)>();

        void EmitNode(KNode node)
        {
            switch (node)
            {
                case CallNode c:
                    // 函数调用：StackNode 指向 Import 表里的函数，参数在 Parameters 里
                    result.Add(new EX_FinalFunction { StackNode = StackOf(c.Function, c.StackNode), Parameters = BuildArgs(c) });
                    break;


                case AssignNode a:
                {
                    // 目标可以是变量（有指针），也可以是结构体成员这类表达式
                    var pointer = a.ValuePointer
                        ?? a.TargetPointer?.Pointer
                        ?? (a.TargetArg as StructMemberArg)?.Member
                        ?? (variables is not null && variables.TryGetValue(a.VariableName, out var ap) ? ap : null);

                    KismetExpression target = a.TargetArg is { } ta
                        ? ToExpr(ta)
                        : pointer is not null
                            ? TargetExpr(a.TargetPointer, pointer)
                            : throw new InvalidOperationException($"赋值目标 {a.VariableName} 无法构造");

                    // EX_Let 需要 Value（指针）与 Variable（表达式）同时给
                    result.Add(new EX_Let
                    {
                        Value = pointer!,
                        Variable = target,
                        Expression = ToExpr(a.Value)
                    });
                    break;
                }

                case ContextCallNode cc:
                {
                    var objPtr = cc.ObjectArg is PointerArg pa
                        ? pa.Pointer
                        : variables is not null && variables.TryGetValue(cc.ObjectVariable, out var found) ? found : null;

                    // 优先用反编译保留的原始指针
                    objPtr = cc.RValuePointer ?? objPtr;
                    if (objPtr is null)
                    {
                        throw new InvalidOperationException($"跨对象调用 {cc.Function} 缺少 RValuePointer");
                    }

                    // ContextExpression 的形态由 InnerShape 决定，不一定都是 FinalFunction。
                    // StackNode 必须用反编译保留的那个：同名导入可能有多个，按名字查会取错索引。
                    var ctxArgs = BuildArgs(new CallNode(cc.Function, cc.Args));
                    var ctxStack = StackOf(cc.Function, cc.StackNode);

                    KismetExpression inner = cc.InnerShape switch
                    {
                        CallShape.Virtual when resolveName is not null => new EX_LocalVirtualFunction
                        {
                            VirtualFunctionName = resolveName(cc.Function),
                            Parameters = ctxArgs
                        },
                        CallShape.Math => new EX_CallMath { StackNode = ctxStack, Parameters = ctxArgs },
                        CallShape.LocalFinal => new EX_LocalFinalFunction { StackNode = ctxStack, Parameters = ctxArgs },
                        _ => new EX_FinalFunction { StackNode = ctxStack, Parameters = ctxArgs }
                    };

                    result.Add(MakeContext(
                        cc.ContextKind,
                        cc.ObjectArg is { } oa ? ToExpr(oa) : new EX_LocalVariable { Variable = objPtr },
                        inner,
                        objPtr));
                    break;
                }

                case IfNode f:
                {
                    // 条件内嵌在 EX_JumpIfNot 里，不单独入列
                    KismetExpression cond = ToExpr(f.Condition);

                    var jn = new EX_JumpIfNot { BooleanExpression = cond };
                    result.Add(jn);

                    foreach (var n in f.Then)
                    {
                        EmitNode(n);
                    }

                    if (f.Else is { Count: > 0 } elseNodes)
                    {
                        // if/else：条件为假跳到 else 起点，then 结束后跳过 else
                        //   跳目标必须在 then 体**之后**取，否则会跳到 then 体上
                        var j = new EX_Jump();
                        result.Add(j);

                        var elseIdx = result.Count;
                        foreach (var n in elseNodes)
                        {
                            EmitNode(n);
                        }

                        var endIdx = result.Count;
                        if (f.EndsWithNothing)
                        {
                            result.Add(new EX_Nothing()); // 原字节码里本来就有的落点占位
                        }

                        patches.Add((jn, elseIdx));
                        patches.Add((j, endIdx));
                    }
                    else
                    {
                        // 无 else：条件为假直接落到 then 体之后。
                        // UE 这里**不会**再补一个 EX_Jump，补了就是 5 个多余字节。
                        var exitIdx = result.Count;
                        if (f.EndsWithNothing)
                        {
                            result.Add(new EX_Nothing());
                        }

                        patches.Add((jn, exitIdx));
                    }

                    break;
                }

                case VirtualCallNode vc when resolveName is not null:
                    // 函数名以 FName 内嵌；名字必须已在资产名字表里
                    result.Add(new EX_LocalVirtualFunction
                    {
                        VirtualFunctionName = resolveName(vc.Function),
                        Parameters = BuildArgs(new CallNode(vc.Function, vc.Args))
                    });
                    break;

                case MathCallNode mc:
                    // 运算符式调用：不需要 context
                    result.Add(new EX_CallMath
                    {
                        StackNode = StackOf(mc.Function, mc.StackNode),
                        Parameters = BuildArgs(new CallNode(mc.Function, mc.Args))
                    });
                    break;

                case MulticastCallNode mcn:
                    result.Add(new EX_CallMulticastDelegate
                    {
                        StackNode = StackOf(mcn.Function, mcn.StackNode),
                        Parameters = BuildArgs(new CallNode(mcn.Function, mcn.Args)),
                        Delegate = mcn.Delegate is { } md ? ToExpr(md) : new EX_Nothing()
                    });
                    break;

                case LocalFinalCallNode lf:
                    // 非 native 的 final 函数（脚本函数）
                    result.Add(new EX_LocalFinalFunction
                    {
                        StackNode = StackOf(lf.Function, lf.StackNode),
                        Parameters = BuildArgs(new CallNode(lf.Function, lf.Args))
                    });
                    break;

                case BottomLoopNode bl:
                {
                    // 底部测条件：体 → 条件 → JumpIfNot(exit) → 尾段 → Jump(体开头)
                    var startIdx = result.Count;

                    foreach (var n in bl.Body)
                    {
                        EmitNode(n);
                    }

                    var test = new EX_JumpIfNot { BooleanExpression = ToExpr(bl.Condition) };
                    result.Add(test);

                    foreach (var n in bl.Tail)
                    {
                        EmitNode(n);
                    }

                    var back = new EX_Jump();
                    result.Add(back);

                    var exitIdx = result.Count;
                    patches.Add((test, exitIdx));
                    patches.Add((back, startIdx)); // 回跳目标是循环体开头，不是条件
                    break;
                }

                case LoopNode lp:
                {
                    // 循环：条件 → JumpIfNot(退出) → 循环体 → Jump(回条件) → 退出落点
                    var startIdx = result.Count;

                    KismetExpression cond = ToExpr(lp.Condition);

                    var jn = new EX_JumpIfNot { BooleanExpression = cond };
                    result.Add(jn);

                    foreach (var n in lp.Body)
                    {
                        EmitNode(n);
                    }

                    var back = new EX_Jump();
                    result.Add(back);
                    var afterIdx = result.Count;
                    if (lp.EndsWithNothing)
                    {
                        result.Add(new EX_Nothing());
                    }

                    patches.Add((jn, afterIdx));
                    patches.Add((back, startIdx)); // 反向跳回条件处
                    break;
                }

                case AssignBoolNode ab:
                {
                    // 目标可以是变量（有指针），也可以是结构体成员 / 跨对象成员这类表达式
                    var pointer = ab.TargetPointer?.Pointer
                        ?? (ab.TargetArg as StructMemberArg)?.Member
                        ?? (ab.TargetArg as ContextMemberArg)?.Pointer
                        ?? (variables is not null && variables.TryGetValue(ab.VariableName, out var bp) ? bp : null);

                    KismetExpression varExpr = ab.TargetArg is { } bta
                        ? ToExpr(bta)
                        : pointer is not null
                            ? TargetExpr(ab.TargetPointer, pointer)
                            : throw new InvalidOperationException($"布尔赋值目标 {ab.VariableName} 无法构造");

                    // EX_LetBase 家族：变量用表达式表示（不像 EX_Let 还要单独的 Value 指针）
                    KismetExpression value = ToExpr(ab.Value);

                    KismetExpression assign = ab.Kind switch
                    {
                        AssignKind.LetObj => new EX_LetObj { VariableExpression = varExpr, AssignmentExpression = value },
                        AssignKind.LetWeakObjPtr => new EX_LetWeakObjPtr { VariableExpression = varExpr, AssignmentExpression = value },
                        AssignKind.LetDelegate => new EX_LetDelegate { VariableExpression = varExpr, AssignmentExpression = value },
                        _ => new EX_LetBool { VariableExpression = varExpr, AssignmentExpression = value }
                    };

                    result.Add(assign);
                    break;
                }

                case PersistentAssignNode pa:
                    result.Add(new EX_LetValueOnPersistentFrame
                    {
                        DestinationProperty = pa.Destination,
                        AssignmentExpression = ToExpr(pa.Value)
                    });
                    break;

                case ComputedJumpNode cj:
                    result.Add(new EX_ComputedJump { CodeOffsetExpression = ToExpr(cj.Target) });
                    break;

                case PushFlowNode pf:
                    // 登记的字节偏移原样发回：节点顺序不变时它就是对的，
                    // 变了会被 DecompileVerified 的字节比对拦住，不会静默写错。
                    result.Add(new EX_PushExecutionFlow { PushingAddress = pf.Target });
                    break;

                case PopFlowNode:
                    result.Add(new EX_PopExecutionFlow());
                    break;

                case PopFlowIfNotNode pif:
                    result.Add(new EX_PopExecutionFlowIfNot { BooleanExpression = ToExpr(pif.Condition) });
                    break;

                case SetArrayNode sa:
                    result.Add(new EX_SetArray
                    {
                        AssigningProperty = ToExpr(sa.Target),
                        ArrayInnerProp = sa.Inner,
                        Elements = sa.Elements.Select(ToExpr).ToArray()
                    });
                    break;

                case UberGraphNode uber:
                {
                    // 按段顺序重新烘焙：调度头 → 各段（段尾单独发）→ 尾声。
                    // 所有跳转目标按新布局重算，所以入口偏移会变，调用方要拿新的写回 UClass。
                    var sectionStart = new int[uber.Sections.Count];
                    var epilogueJumps = new List<KismetExpression>();
                    var sectionJumps = new List<(EX_Jump Jump, int Ordinal)>();
                    EX_PushExecutionFlow? push = null;

                    if (uber.PushesEpilogue)
                    {
                        push = new EX_PushExecutionFlow();
                        result.Add(push);
                    }

                    result.Add(new EX_ComputedJump { CodeOffsetExpression = ToExpr(uber.DispatchExpression) });

                    for (var i = 0; i < uber.Sections.Count; i++)
                    {
                        var section = uber.Sections[i];
                        sectionStart[i] = result.Count;
                        trace?.SectionStarts.Add(result.Count);

                        foreach (var n in section.Nodes)
                        {
                            EmitNode(n);
                        }

                        switch (section.Terminator)
                        {
                            case UberTerminator.JumpToEpilogue:
                            {
                                var jump = new EX_Jump();
                                result.Add(jump);
                                epilogueJumps.Add(jump);
                                break;
                            }

                            case UberTerminator.JumpToOtherSection:
                            {
                                var jump = new EX_Jump();
                                result.Add(jump);
                                if (section.TargetOrdinal is { } ord && ord >= 0 && ord < sectionStart.Length)
                                {
                                    // 目标段可能在后面，起始下标还没算出来——留到全部段发完再统一打补丁
                                    sectionJumps.Add((jump, ord));
                                }
                                else
                                {
                                    throw new InvalidOperationException(
                                        $"ubergraph 段 #{i} 的跨段跳转缺少目标段（TargetOrdinal={section.TargetOrdinal}）");
                                }

                                break;
                            }

                            case UberTerminator.PopExecutionFlow:
                                result.Add(new EX_PopExecutionFlow());
                                break;

                            case UberTerminator.PopExecutionFlowIfNot:
                                result.Add(new EX_PopExecutionFlowIfNot
                                {
                                    BooleanExpression = section.Condition is { } cond ? ToExpr(cond) : new EX_Nothing()
                                });
                                break;
                        }
                    }

                    // 尾声就在这一段之后（外层紧接着发声明的 Return）
                    var epilogueIndex = result.Count;
                    if (trace is not null)
                    {
                        trace.EpilogueIndex = epilogueIndex;
                    }
                    foreach (var jump in epilogueJumps)
                    {
                        patches.Add((jump, epilogueIndex));
                    }

                    foreach (var (jump, ord) in sectionJumps)
                    {
                        patches.Add((jump, sectionStart[ord]));
                    }

                    if (push is not null)
                    {
                        patches.Add((push, epilogueIndex));
                    }

                    break;
                }

                case BlockGraphNode graph:
                {
                    // 基本块图：块的顺序就是指令顺序，只把跳转目标按块标号重算，
                    // 所以产出的字节与原始完全一致。
                    var blockStart = new int[graph.Blocks.Count];
                    var pending = new List<(KismetExpression Jump, int Target)>();
                    var endIdx = result.Count;

                    for (var i = 0; i < graph.Blocks.Count; i++)
                    {
                        var block = graph.Blocks[i];
                        blockStart[i] = result.Count;

                        foreach (var n in block.Nodes)
                        {
                            EmitNode(n);
                        }

                        switch (block.Exit)
                        {
                            case BlockExit.Jump:
                            {
                                var jump = new EX_Jump();
                                result.Add(jump);
                                if (block.TargetIndex is { } target)
                                {
                                    pending.Add((jump, target));
                                }

                                break;
                            }

                            case BlockExit.JumpIfNot:
                            {
                                var jumpIfNot = new EX_JumpIfNot
                                {
                                    BooleanExpression = block.Condition is { } cond ? ToExpr(cond) : new EX_Nothing()
                                };
                                result.Add(jumpIfNot);
                                if (block.TargetIndex is { } target)
                                {
                                    pending.Add((jumpIfNot, target));
                                }

                                break;
                            }
                        }
                    }

                    endIdx = result.Count;
                    foreach (var (jump, target) in pending)
                    {
                        patches.Add((jump, target < blockStart.Length ? blockStart[target] : endIdx));
                    }

                    break;
                }

                case KismetDecompiler.RawNode raw:
                    // 未归类的表达式（如孤立的 EX_Jump）原样发回去，
                    // 丢掉会让后面所有指令错位。
                    result.Add(raw.Expression);
                    break;

                case ReturnNode rn:
                    result.Add(new EX_Return
                    {
                        ReturnExpression = rn.Value is { } rv ? ToExpr(rv) : new EX_Nothing()
                    });
                    break;
            }
        }

        foreach (var node in nodes)
        {
            EmitNode(node);
        }

        result.Add(new EX_EndOfScript());

        // 第二遍：按字节回填跳转。
        // EX_Jump / EX_JumpIfNot 的操作数是序列化字节流的偏移，不是表达式下标。
        if (measure is not null && patches.Count > 0)
        {
            var offsets = new uint[result.Count];
            uint acc = 0;
            for (var i = 0; i < result.Count; i++)
            {
                offsets[i] = acc;
                acc += measure(result[i]);
            }

            foreach (var (jump, target) in patches)
            {
                var value = target < offsets.Length ? offsets[target] : acc;
                if (jump is EX_Jump jm)
                {
                    jm.CodeOffset = value;
                }
                else if (jump is EX_JumpIfNot jn2)
                {
                    jn2.CodeOffset = value;
                }
                else if (jump is EX_PushExecutionFlow pf)
                {
                    pf.PushingAddress = value;
                }
            }
        }

        return result;
    }
}
