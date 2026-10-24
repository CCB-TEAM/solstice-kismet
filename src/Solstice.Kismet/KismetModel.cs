using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.UnrealTypes;

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

/// <summary>
/// 原样保留的属性指针。反编译时用它，避免多段 FFieldPath / ResolvedOwner 被压平成名字后丢失信息。
/// </summary>
public sealed record PointerArg(KismetPropertyPointer Pointer, string Name, bool Instance = false, bool Out = false)
    : KArg(PinType.Object);

/// <summary>布尔字面量，对应 EX_True / EX_False。</summary>
public sealed record BoolArg(bool Value) : KArg(PinType.Bool);

/// <summary>浮点字面量，对应 EX_DoubleConst / EX_FloatConst。</summary>
public sealed record DoubleArg(double Value) : KArg(PinType.Float);

/// <summary>单精度字面量，对应 EX_FloatConst。</summary>
public sealed record FloatArg(float Value) : KArg(PinType.Float);

/// <summary>跳过偏移常量，对应 EX_SkipOffsetConst。</summary>
public sealed record SkipOffsetArg(uint Value) : KArg(PinType.Int);

/// <summary>向量字面量，对应 EX_VectorConst。</summary>
public sealed record VectorArg(FVector Value) : KArg(PinType.Struct);

/// <summary>旋转字面量，对应 EX_RotationConst。</summary>
public sealed record RotationArg(FRotator Value) : KArg(PinType.Struct);

/// <summary>动态类型转换，对应 EX_DynamicCast（EX_CastBase：Target + ClassPtr）。</summary>
public sealed record DynamicCastArg(KArg Target, FPackageIndex ClassPtr) : KArg(PinType.Object);

/// <summary>转成接口，对应 EX_ObjToInterfaceCast（EX_CastBase）。</summary>
public sealed record InterfaceCastArg(KArg Target, FPackageIndex ClassPtr) : KArg(PinType.Object);

/// <summary>64 位整数字面量，对应 EX_Int64Const。</summary>
public sealed record LongArg(long Value) : KArg(PinType.Int);

/// <summary>基本类型转换，对应 EX_PrimitiveCast（ConversionType + Target）。</summary>
public sealed record PrimitiveCastArg(KArg Target, ECastToken Conversion) : KArg(PinType.Int);

/// <summary>
/// 原样保留的表达式。用于结构复杂、重新构造容易出错的指令
/// （如 EX_SwitchValue 含相对偏移）。回编译时直接发回原对象，保证字节一致。
/// </summary>
public sealed record RawArg(KismetExpression Expression) : KArg(PinType.Object);

/// <summary>数组字面量，对应 EX_ArrayConst。</summary>
public sealed record ArrayConstArg(KismetPropertyPointer Inner, IReadOnlyList<KArg> Elements)
    : KArg(PinType.Struct);

/// <summary>字节字面量，对应 EX_ByteConst。</summary>
public sealed record ByteArg(byte Value) : KArg(PinType.Int);

/// <summary>文本字面量，对应 EX_TextConst。</summary>
public sealed record TextArg(FScriptText Value) : KArg(PinType.String);

/// <summary>结构体字面量，对应 EX_StructConst。</summary>
public sealed record StructConstArg(FPackageIndex Struct, int Size, IReadOnlyList<KArg> Values)
    : KArg(PinType.Struct);

/// <summary>
/// 跨对象读成员，对应 EX_Context + EX_InstanceVariable。
/// EX_Context 的 ContextExpression 不一定都是调用。
/// </summary>
public sealed record ContextMemberArg(KArg Object, string Member, KismetPropertyPointer Pointer)
    : KArg(PinType.Object);

/// <summary>空对象字面量，对应 EX_NoObject。</summary>
public sealed record NoObjectArg() : KArg(PinType.Object);

/// <summary>结构体成员访问，对应 EX_StructMemberContext。</summary>
public sealed record StructMemberArg(KArg Struct, KismetPropertyPointer Member, string Name = "")
    : KArg(PinType.Struct);

/// <summary>对象字面量，对应 EX_ObjectConst。</summary>
public sealed record ObjectArg(FPackageIndex Value) : KArg(PinType.Object);

/// <summary>字符串字面量，对应 EX_StringConst。</summary>
public sealed record StringArg(string Value) : KArg(PinType.String);

/// <summary>隐式 this，对应 EX_Self。</summary>
public sealed record SelfArg() : KArg(PinType.Object);

/// <summary>名字字面量，对应 EX_NameConst。</summary>
public sealed record NameArg(FName Value) : KArg(PinType.String);

/// <summary>
/// 调用的指令形态。
///
/// <para>
/// <b>不要从 flags 推断。</b>UE 的判定在
/// <c>KismetCompilerVMBackend.cpp</c> 里，是六个条件的合取，其中
/// <c>GetCallerTargetsUnsupportedByCallee</c>（构建目标兼容性）在 jmap 函数表里根本不存在，
/// 无法完整复现。所以形态是**保留**的：反编译看到哪个就记哪个，回编译原样发回去。
/// </para>
/// </summary>
public enum CallShape
{
    /// <summary>EX_FinalFunction：native 的 final 函数。</summary>
    Final,

    /// <summary>EX_LocalFinalFunction：非 native 的 final 函数（脚本函数）。</summary>
    LocalFinal,

    /// <summary>EX_CallMath：纯运算符式调用，不需要 context。</summary>
    Math,

    /// <summary>EX_LocalVirtualFunction：按 FName 调本资产函数。</summary>
    Virtual,

    /// <summary>EX_VirtualFunction：按 FName 调虚函数（非 Local 形态）。</summary>
    VirtualGlobal,

    /// <summary>EX_CallMulticastDelegate：多播委托调用（继承自 EX_FinalFunction，多一个 Delegate）。</summary>
    MulticastDelegate,

    /// <summary>EX_Context：实例函数，需要对象上下文。</summary>
    Context
}

/// <summary>以另一个调用的返回值作为参数。</summary>
public sealed record CallArg(
    string Function,
    IReadOnlyList<KArg> Args,
    CallShape Shape,
    string? ObjectVariable = null,
    FPackageIndex? StackNode = null,
    KArg? ObjectArg = null,
    KismetPropertyPointer? RValuePointer = null,
    CallShape InnerShape = CallShape.Final,
    KArg? Delegate = null)
    : KArg(PinType.Object);

/// <summary>多播委托调用，编译成 EX_CallMulticastDelegate。</summary>
public sealed record MulticastCallNode(
    string Function,
    IReadOnlyList<KArg> Args,
    KArg? Delegate,
    FPackageIndex? StackNode = null)
    : KNode($"Multicast {Function}");

/// <summary>节点（编辑器层的逻辑单元，一行一个）。</summary>
public abstract record KNode(string Title);

public sealed record CallNode(string Function, IReadOnlyList<KArg> Args, string? Owner = null, FPackageIndex? StackNode = null) : KNode(Function);

public sealed record IfNode(KArg Condition, IReadOnlyList<KNode> Then, IReadOnlyList<KNode>? Else = null)
    : KNode("If");

public sealed record ReturnNode(KArg? Value = null) : KNode("Return");

/// <summary>赋值：把值写进一个变量。</summary>
public sealed record AssignNode(
    string VariableName,
    KArg Value,
    PointerArg? TargetPointer = null,
    KArg? TargetArg = null,
    KismetPropertyPointer? ValuePointer = null)
    : KNode($"Set {VariableName}");

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
    KArg? ObjectArg = null,
    KismetPropertyPointer? RValuePointer = null,
        CallShape InnerShape = CallShape.Final)
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

/// <summary>非 native 的 final 函数调用，编译成 EX_LocalFinalFunction。</summary>
public sealed record LocalFinalCallNode(string Function, IReadOnlyList<KArg> Args, FPackageIndex? StackNode = null)
    : KNode($"LocalFinal {Function}");

/// <summary>运算符式调用，编译成 EX_CallMath（不需要 context）。</summary>
public sealed record MathCallNode(string Function, IReadOnlyList<KArg> Args, FPackageIndex? StackNode = null)
    : KNode($"Math {Function}");

/// <summary>EX_LetBase 家族的赋值种类（都是 VariableExpression + AssignmentExpression 同形）。</summary>
public enum AssignKind
{
    /// <summary>EX_LetBool</summary>
    LetBool,

    /// <summary>EX_LetObj</summary>
    LetObj,

    /// <summary>EX_LetWeakObjPtr</summary>
    LetWeakObjPtr,

    /// <summary>EX_LetDelegate</summary>
    LetDelegate
}

/// <summary>EX_LetBase 家族赋值（VariableExpression + AssignmentExpression）。</summary>
public sealed record AssignBoolNode(
    string VariableName,
    KArg Value,
    PointerArg? TargetPointer = null,
    AssignKind Kind = AssignKind.LetBool,
    KArg? TargetArg = null)
    : KNode($"Set {VariableName}");

/// <summary>写持久帧上的变量，编译成 EX_LetValueOnPersistentFrame。</summary>
public sealed record PersistentAssignNode(KismetPropertyPointer Destination, KArg Value, string Name = "")
    : KNode($"SetPersistent {Name}");

/// <summary>
/// 循环：条件为真时反复执行循环体。
/// 编译成 条件 → JumpIfNot(退出) → 循环体 → Jump(跳回条件) → 退出落点。
/// </summary>
public sealed record LoopNode(KArg Condition, IReadOnlyList<KNode> Body) : KNode("Loop");
