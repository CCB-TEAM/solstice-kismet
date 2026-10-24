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

    public static IReadOnlyList<KismetExpression> CompileToExpressions(
        IReadOnlyList<KNode> nodes,
        IReadOnlyDictionary<string, KismetPropertyPointer>? variables = null,
        IReadOnlyDictionary<string, FPackageIndex>? functions = null,
        Func<KismetExpression, uint>? measure = null,
        Func<string, FName>? resolveName = null)
    {
        var result = new List<KismetExpression>();

        /// <summary>
        /// 把数据流参数转成表达式。支持嵌套调用（<see cref="CallArg"/>），
        /// 这样一个调用的返回值可以直接作为另一个调用的参数或对象。
        /// </summary>
        KismetExpression ToExpr(KArg arg)
        {
            switch (arg)
            {
                case IntArg i:
                    return new EX_IntConst { Value = i.Value };

                case LocalArg l when variables is not null && variables.TryGetValue(l.Name, out var pointer):
                    // 复用现有字节码里的属性指针：FFieldPath 自带变量名与归属索引
                    return new EX_LocalVariable { Variable = pointer };

                case CallArg c:
                {
                    var inner = c.Args.Select(ToExpr).ToArray();

                    if (c.Shape == CallShape.Virtual && resolveName is not null)
                    {
                        return new EX_LocalVirtualFunction
                        {
                            VirtualFunctionName = resolveName(c.Function),
                            Parameters = inner
                        };
                    }

                    var final = new EX_FinalFunction
                    {
                        StackNode = functions is not null && functions.TryGetValue(c.Function, out var fi)
                            ? fi
                            : new FPackageIndex(0),
                        Parameters = inner
                    };

                    if (c.Shape == CallShape.Context
                        && c.ObjectVariable is { } objVar
                        && variables is not null
                        && variables.TryGetValue(objVar, out var objPtr))
                    {
                        return new EX_Context
                        {
                            ObjectExpression = new EX_LocalVariable { Variable = objPtr },
                            ContextExpression = final,
                            RValuePointer = objPtr,
                            Offset = measure?.Invoke(final) ?? 0
                        };
                    }

                    return final;
                }

                default:
                    return new EX_Nothing();
            }
        }

        void EmitArg(KArg arg) => result.Add(ToExpr(arg));

        KismetExpression[] BuildArgs(CallNode c) => c.Args.Select(ToExpr).ToArray();

        var patches = new List<(KismetExpression Jump, int Target)>();

        void EmitNode(KNode node)
        {
            switch (node)
            {
                case CallNode c when functions is not null && functions.TryGetValue(c.Function, out var fnIndex):
                    // 函数调用：StackNode 指向 Import 表里的函数，参数在 Parameters 里
                    result.Add(new EX_FinalFunction { StackNode = fnIndex, Parameters = BuildArgs(c) });
                    break;

                case CallNode c:
                    foreach (var a in c.Args)
                    {
                        EmitArg(a);
                    }

                    result.Add(new EX_Nothing());
                    break;

                case AssignNode a when variables is not null && variables.TryGetValue(a.VariableName, out var ap):
                    // EX_Let 需要 Value（指针）与 Variable（表达式）同时给
                    result.Add(new EX_Let
                    {
                        Value = ap,
                        Variable = new EX_LocalVariable { Variable = ap },
                        Expression = KismetExprBuilder.ToExpression(a.Value, variables)
                    });
                    break;

                case ContextCallNode cc
                    when variables is not null && variables.TryGetValue(cc.ObjectVariable, out var objPtr)
                         && functions is not null && functions.TryGetValue(cc.Function, out var ctxFn):
                {
                    var inner = new EX_FinalFunction { StackNode = ctxFn, Parameters = BuildArgs(new CallNode(cc.Function, cc.Args)) };
                    result.Add(new EX_Context
                    {
                        // 对象可以是变量，也可以是另一个调用的返回值
                        ObjectExpression = cc.ObjectArg is { } oa
                            ? ToExpr(oa)
                            : new EX_LocalVariable { Variable = objPtr },
                        ContextExpression = inner,
                        RValuePointer = objPtr,
                        Offset = measure?.Invoke(inner) ?? 0
                    });
                    break;
                }

                case IfNode f:
                {
                    // 条件内嵌在 EX_JumpIfNot 里，不单独入列
                    KismetExpression cond = f.Condition switch
                    {
                        IntArg i => new EX_IntConst { Value = i.Value },
                        LocalArg l when variables is not null && variables.TryGetValue(l.Name, out var lp)
                            => new EX_LocalVariable { Variable = lp },
                        _ => new EX_Nothing()
                    };

                    var jn = new EX_JumpIfNot { BooleanExpression = cond };
                    result.Add(jn);
                    var elseIdx = result.Count;

                    foreach (var n in f.Then)
                    {
                        EmitNode(n);
                    }

                    var j = new EX_Jump();
                    result.Add(j);
                    var endIdx = result.Count;

                    result.Add(new EX_Nothing()); // else 落点
                    result.Add(new EX_Nothing()); // end 落点

                    patches.Add((jn, elseIdx));
                    patches.Add((j, endIdx));
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

                case LoopNode lp:
                {
                    // 循环：条件 → JumpIfNot(退出) → 循环体 → Jump(回条件) → 退出落点
                    var startIdx = result.Count;

                    KismetExpression cond = lp.Condition switch
                    {
                        IntArg i => new EX_IntConst { Value = i.Value },
                        LocalArg l when variables is not null && variables.TryGetValue(l.Name, out var lpPtr)
                            => new EX_LocalVariable { Variable = lpPtr },
                        _ => new EX_Nothing()
                    };

                    var jn = new EX_JumpIfNot { BooleanExpression = cond };
                    result.Add(jn);

                    foreach (var n in lp.Body)
                    {
                        EmitNode(n);
                    }

                    var back = new EX_Jump();
                    result.Add(back);
                    var afterIdx = result.Count;
                    result.Add(new EX_Nothing()); // 退出落点

                    patches.Add((jn, afterIdx));
                    patches.Add((back, startIdx)); // 反向跳回条件处
                    break;
                }

                case AssignBoolNode ab when variables is not null && variables.TryGetValue(ab.VariableName, out var bp):
                    // EX_LetBool：变量用表达式表示（不像 EX_Let 还要单独的 Value 指针）
                    result.Add(new EX_LetBool
                    {
                        VariableExpression = new EX_LocalVariable { Variable = bp },
                        AssignmentExpression = ab.Value ? new EX_True() : new EX_False()
                    });
                    break;

                case ReturnNode:
                    result.Add(new EX_Return { ReturnExpression = new EX_Nothing() });
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
            }
        }

        return result;
    }
}
