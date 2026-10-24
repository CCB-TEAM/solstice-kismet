namespace Solstice.Kismet;

/// <summary>插销类型。颜色由 UI 按此映射，类型推断也读它。</summary>
public enum PinType
{
    Exec,
    Int,
    Bool,
    Float,
    String,
    Object,
    Struct,
    Enum
}

/// <summary>
/// 用到的操作码及其真实字节值。取值来源：UAssetAPI 的 EExprToken。
/// 实测确认 EX_EndOfScript 存在：card_event_burning_fields_skirm 的
/// OnPlayedFromHand 是 LetValueOnPersistentFrame → LocalFinalFunction → Return → EndOfScript。
/// </summary>
public static class KismetOpcode
{
    public const byte LocalVariable = 0x00;
    public const byte InstanceVariable = 0x01;
    public const byte Return = 0x04;
    public const byte Jump = 0x06;
    public const byte JumpIfNot = 0x07;
    public const byte Nothing = 0x0B;
    public const byte Let = 0x0F;
    public const byte EndFunctionParms = 0x16;
    public const byte VirtualFunction = 0x1B;
    public const byte FinalFunction = 0x1C;
    public const byte IntConst = 0x1D;
    public const byte IntZero = 0x25;
    public const byte IntOne = 0x26;
    public const byte True = 0x27;
    public const byte False = 0x28;

    public static byte? Lookup(string name) => name switch
    {
        "EX_LocalVariable" => LocalVariable,
        "EX_InstanceVariable" => InstanceVariable,
        "EX_Return" => Return,
        "EX_Jump" => Jump,
        "EX_JumpIfNot" => JumpIfNot,
        "EX_Nothing" => Nothing,
        "EX_Let" => Let,
        "EX_EndFunctionParms" => EndFunctionParms,
        "EX_VirtualFunction" => VirtualFunction,
        "EX_FinalFunction" => FinalFunction,
        "EX_IntConst" => IntConst,
        "EX_IntZero" => IntZero,
        "EX_IntOne" => IntOne,
        "EX_True" => True,
        "EX_False" => False,
        _ => null
    };
}

/// <summary>一条编译产物：操作码 + 操作数。</summary>
public sealed record KismetInstruction(string Opcode, string Operand, int? JumpTarget = null)
{
    public byte? Token => KismetOpcode.Lookup(Opcode);

    public string TokenHex => Token is { } t ? $"0x{t:X2}" : "??";

    public string Render() => JumpTarget is { } t
        ? $"{Opcode,-24} -> {t:000}"
        : string.IsNullOrEmpty(Operand) ? Opcode : $"{Opcode,-24} {Operand}";
}

/// <summary>表达式参数（数据流上游）。</summary>
public abstract record KArg(PinType Type);

public sealed record IntArg(int Value) : KArg(PinType.Int);

public sealed record LocalArg(string Name) : KArg(PinType.Int);

/// <summary>调用的形态（由签名与归属推断，不需要用户选）。</summary>
public enum CallShape
{
    /// <summary>EX_FinalFunction：静态函数，StackNode 指向 Import。</summary>
    Final,

    /// <summary>EX_LocalVirtualFunction：本资产函数，按 FName 调用。</summary>
    Virtual,

    /// <summary>EX_Context：实例函数，需要对象上下文。</summary>
    Context
}

/// <summary>以另一个调用的返回值作为参数。</summary>
public sealed record CallArg(string Function, IReadOnlyList<KArg> Args, CallShape Shape, string? ObjectVariable = null)
    : KArg(PinType.Object);

/// <summary>节点（编辑器层的逻辑单元，一行一个）。</summary>
public abstract record KNode(string Title);

public sealed record CallNode(string Function, IReadOnlyList<KArg> Args, string? Owner = null) : KNode(Function);

public sealed record IfNode(KArg Condition, IReadOnlyList<KNode> Then) : KNode("If");

public sealed record ReturnNode() : KNode("Return");

/// <summary>赋值：把常量写进一个已有变量。</summary>
public sealed record AssignNode(string VariableName, KArg Value) : KNode($"Set {VariableName}");

/// <summary>
/// 跨对象调用：在某个对象上调用函数（编译成 EX_Context + EX_FinalFunction）。
///
/// <para>
/// 生成 EX_Context 需要 Offset = ContextExpression 的序列化长度，
/// 而长度依赖资产（FName 索引宽度），所以编译器需要调用方提供 measure 回调。
/// </para>
/// </summary>
public sealed record ContextCallNode(
    string ObjectVariable,
    string Function,
    IReadOnlyList<KArg> Args,
    string? Owner = null,
    KArg? ObjectArg = null)
    : KNode($"{ObjectVariable}.{Function}");

/// <summary>
/// 虚函数调用：编译成 EX_LocalVirtualFunction。
///
/// <para>
/// 函数名以 FName 形式内嵌，必须已存在于目标资产的名字表里，
/// 因此需要 <see cref="CompileContext.ResolveName"/>。
/// </para>
/// </summary>
public sealed record VirtualCallNode(string Function, IReadOnlyList<KArg> Args)
    : KNode($"Virtual {Function}");

/// <summary>布尔赋值：编译成 EX_LetBool（VariableExpression + AssignmentExpression）。</summary>
public sealed record AssignBoolNode(string VariableName, bool Value)
    : KNode($"Set {VariableName} = {(Value ? "true" : "false")}");

/// <summary>
/// 循环：条件为真时反复执行循环体。
/// 编译成 条件 → JumpIfNot(退出) → 循环体 → Jump(跳回条件) → 退出落点。
/// </summary>
public sealed record LoopNode(KArg Condition, IReadOnlyList<KNode> Body) : KNode("Loop");
