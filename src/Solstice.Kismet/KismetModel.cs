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
public sealed record PointerArg(
    KismetPropertyPointer Pointer,
    string Name,
    bool Instance = false,
    bool Out = false,
    bool Default = false)
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

/// <summary>元类转换（类引用之间的转换），对应 EX_MetaCast（EX_CastBase）。</summary>
public sealed record MetaCastArg(KArg Target, FPackageIndex ClassPtr) : KArg(PinType.Object);

/// <summary>按引用取数组元素，对应 EX_ArrayGetByRef（数组表达式 + 索引表达式）。</summary>
public sealed record ArrayGetByRefArg(KArg Array, KArg Index) : KArg(PinType.Object);

/// <summary>UTF-16 字符串字面量，对应 EX_UnicodeStringConst（含非 ASCII 时用它）。</summary>
public sealed record UnicodeStringArg(string Value) : KArg(PinType.String);

/// <summary>函数/委托实例引用，对应 EX_InstanceDelegate（只有 FName）。</summary>
public sealed record InstanceDelegateArg(FName FunctionName) : KArg(PinType.Object);

/// <summary>变换字面量，对应 EX_TransformConst。</summary>
public sealed record TransformArg(FTransform Value) : KArg(PinType.Struct);

/// <summary>软对象引用字面量，对应 EX_SoftObjectConst（载荷本身是一个表达式）。</summary>
public sealed record SoftObjectArg(KArg Value) : KArg(PinType.Object);

/// <summary>Map 字面量，对应 EX_MapConst（两个内层属性指针 + 元素）。</summary>
public sealed record MapConstArg(
    KismetPropertyPointer Key,
    KismetPropertyPointer Value,
    IReadOnlyList<KArg> Elements)
    : KArg(PinType.Struct);

/// <summary>
/// 接口上下文，对应 EX_InterfaceContext（只有子表达式，没有其他字段）。
///
/// <para>
/// 它出现在 <c>EX_Context.ObjectExpression</c> 里包住一个接口变量：
/// 反编译若把它脱掉，回编译就只剩里面的 <c>EX_LocalVariable</c>，每条少 1 字节。
/// 这是「保留」而非「推断」的又一个例子——形态必须原样记住。
/// </para>
/// </summary>
public sealed record InterfaceContextArg(KArg Value) : KArg(PinType.Object);

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
public sealed record ContextMemberArg(
    KArg Object,
    string Member,
    KismetPropertyPointer Pointer,
    bool IsDefault = false,
    ContextKind Kind = ContextKind.Normal)
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

/// <summary>
/// <c>EX_Context</c> 的三种形态。它们操作数完全相同，但令牌不同，
/// 回编译必须发回原来那一个，否则字节就对不上。
/// </summary>
public enum ContextKind
{
    /// <summary>EX_Context：普通对象上下文。</summary>
    Normal,

    /// <summary>EX_ClassContext：上下文是类默认对象（CDO）。</summary>
    ClassContext,

    /// <summary>EX_Context_FailSilent：上下文为空时静默跳过（只用于无返回值函数）。</summary>
    FailSilent
}

/// <summary>以另一个调用的返回值作为参数。</summary>
public sealed record CallArg(
    FunctionRef Callee,
    IReadOnlyList<KArg> Args,
    CallShape Shape,
    string? ObjectVariable = null,
    KArg? ObjectArg = null,
    KismetPropertyPointer? RValuePointer = null,
    CallShape InnerShape = CallShape.Final,
    KArg? Delegate = null,
    ContextKind ContextKind = ContextKind.Normal)
    : KArg(PinType.Object)
{
    /// <summary>兼容旧写法：只给名字。</summary>
    public CallArg(
        string Function,
        IReadOnlyList<KArg> Args,
        CallShape Shape,
        string? ObjectVariable = null,
        FPackageIndex? StackNode = null,
        KArg? ObjectArg = null,
        KismetPropertyPointer? RValuePointer = null,
        CallShape InnerShape = CallShape.Final,
        KArg? Delegate = null,
        ContextKind ContextKind = ContextKind.Normal,
        string? Owner = null)
        : this(
            FunctionRef.Named(Function, Owner, StackNode),
            Args,
            Shape,
            ObjectVariable,
            ObjectArg,
            RValuePointer,
            InnerShape,
            Delegate,
            ContextKind)
    {
    }

    public string Function => Callee.Name;

    public string? Owner => string.IsNullOrEmpty(Callee.OwnerPath) ? null : Callee.OwnerPath;

    public FPackageIndex? StackNode => Callee.HasIndex ? Callee.Index : null;
}

/// <summary>多播委托调用，编译成 EX_CallMulticastDelegate。</summary>
public sealed record MulticastCallNode(FunctionRef Callee, IReadOnlyList<KArg> Args, KArg? Delegate)
    : KNode($"Multicast {Callee.Name}")
{
    /// <summary>兼容旧写法：只给名字。</summary>
    public MulticastCallNode(
        string Function,
        IReadOnlyList<KArg> Args,
        KArg? Delegate,
        FPackageIndex? StackNode = null,
        string? Owner = null)
        : this(FunctionRef.Named(Function, Owner, StackNode), Args, Delegate)
    {
    }

    public string Function => Callee.Name;

    public FPackageIndex? StackNode => Callee.HasIndex ? Callee.Index : null;
}

/// <summary>节点（编辑器层的逻辑单元，一行一个）。</summary>
public abstract record KNode(string Title);

/// <summary>
/// 函数调用：**直接持有函数引用**（名字 + 归属 + 包索引），不再「名字字符串 + 可选 StackNode」两处真相。
///
/// <para>
/// 这和 UE 的编码方式一致：<c>EX_FinalFunction</c> 把 StackNode 和参数放在同一条指令里，
/// 所以模型里也应该是「调用带着被调函数」。用 <see cref="FunctionRef.Named"/> 或
/// <see cref="KismetWorkspace.ResolveFunction"/> 得到引用再构造。
/// </para>
/// </summary>
public sealed record CallNode(FunctionRef Callee, IReadOnlyList<KArg> Args)
    : KNode(Callee.Name)
{
    /// <summary>兼容旧写法：只给名字（索引为 0，编译时按归属/唯一同名解析）。</summary>
    public CallNode(string function, IReadOnlyList<KArg> args, string? owner = null, FPackageIndex? stackNode = null)
        : this(FunctionRef.Named(function, owner, stackNode), args)
    {
    }

    public string Function => Callee.Name;

    public string? Owner => string.IsNullOrEmpty(Callee.OwnerPath) ? null : Callee.OwnerPath;

    public FPackageIndex? StackNode => Callee.HasIndex ? Callee.Index : null;
}

public sealed record IfNode(
    KArg Condition,
    IReadOnlyList<KNode> Then,
    IReadOnlyList<KNode>? Else = null,
    bool EndsWithNothing = false)
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
    FunctionRef Callee,
    IReadOnlyList<KArg> Args,
    KArg? ObjectArg = null,
    KismetPropertyPointer? RValuePointer = null,
    CallShape InnerShape = CallShape.Final,
    ContextKind ContextKind = ContextKind.Normal)
    : KNode($"{ObjectVariable}.{Callee.Name}")
{
    /// <summary>兼容旧写法：只给名字。</summary>
    public ContextCallNode(
        string ObjectVariable,
        string Function,
        IReadOnlyList<KArg> Args,
        string? Owner = null,
        KArg? ObjectArg = null,
        KismetPropertyPointer? RValuePointer = null,
        CallShape InnerShape = CallShape.Final,
        FPackageIndex? StackNode = null,
        ContextKind ContextKind = ContextKind.Normal)
        : this(
            ObjectVariable,
            FunctionRef.Named(Function, Owner, StackNode),
            Args,
            ObjectArg,
            RValuePointer,
            InnerShape,
            ContextKind)
    {
    }

    public string Function => Callee.Name;

    public string? Owner => string.IsNullOrEmpty(Callee.OwnerPath) ? null : Callee.OwnerPath;

    public FPackageIndex? StackNode => Callee.HasIndex ? Callee.Index : null;
}

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
public sealed record LocalFinalCallNode(FunctionRef Callee, IReadOnlyList<KArg> Args)
    : KNode($"LocalFinal {Callee.Name}")
{
    /// <summary>兼容旧写法：只给名字。</summary>
    public LocalFinalCallNode(string Function, IReadOnlyList<KArg> Args, FPackageIndex? StackNode = null, string? Owner = null)
        : this(FunctionRef.Named(Function, Owner, StackNode), Args)
    {
    }

    public string Function => Callee.Name;

    public FPackageIndex? StackNode => Callee.HasIndex ? Callee.Index : null;
}

/// <summary>运算符式调用，编译成 EX_CallMath（不需要 context）。</summary>
public sealed record MathCallNode(FunctionRef Callee, IReadOnlyList<KArg> Args)
    : KNode($"Math {Callee.Name}")
{
    /// <summary>兼容旧写法：只给名字。</summary>
    public MathCallNode(string Function, IReadOnlyList<KArg> Args, FPackageIndex? StackNode = null, string? Owner = null)
        : this(FunctionRef.Named(Function, Owner, StackNode), Args)
    {
    }

    public string Function => Callee.Name;

    public FPackageIndex? StackNode => Callee.HasIndex ? Callee.Index : null;
}

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
public sealed record LoopNode(
    KArg Condition,
    IReadOnlyList<KNode> Body,
    bool EndsWithNothing = false)
    : KNode("Loop");

/// <summary>
/// 底部测条件的循环（UE 编译 <c>for</c> 循环的形态）。
///
/// <para>
/// 回跳目标是**循环体开头**，不是条件，所以不能套用 <see cref="LoopNode"/>
/// （那个是「条件在顶部」）。实际布局：
/// </para>
/// <code>
/// body_start:  [Body]  条件  JumpIfNot -> exit  [Tail]  Jump -> body_start
/// exit:
/// </code>
/// <para>
/// 其中 <c>Tail</c> 通常是自增/步进部分。把它单独建节点，回编译才能把回跳补回
/// body_start 而不是条件处（差一处就会整段结构退化）。
/// </para>
/// </summary>
public sealed record BottomLoopNode(
    IReadOnlyList<KNode> Body,
    KArg Condition,
    IReadOnlyList<KNode> Tail)
    : KNode($"LoopDoWhile {ArgRender.Of(Condition)}");

/// <summary>
/// 算出来的跳转目标，对应 <c>EX_ComputedJump</c>。
///
/// <para>
/// 和 <c>EX_Jump</c> 不同：目标不是常量偏移，而是一个表达式（通常是整数常量或变量）。
/// 蓝图里的分发器（按状态跳到对应分支）就靠它。
/// </para>
/// </summary>
public sealed record ComputedJumpNode(KArg Target) : KNode($"JumpTo {ArgRender.Of(Target)}");

/// <summary>
/// 把「以后回到这里」压进执行流栈，对应 <c>EX_PushExecutionFlow</c>。
///
/// <para>
/// 它<b>不改变</b>当前执行位置（这点和 <c>EX_Jump</c> 相反），只是登记一个恢复点；
/// 之后遇到 <see cref="PopFlowNode"/> 才会跳回去。异步/延迟节点（Delay、Timeline）依赖它。
/// </para>
/// </summary>
public sealed record PushFlowNode(uint Target) : KNode($"PushFlow -> {Target}");

/// <summary>跳到执行流栈顶登记的地址，对应 <c>EX_PopExecutionFlow</c>。</summary>
public sealed record PopFlowNode() : KNode("PopFlow");

/// <summary>条件不成立时才跳回执行流栈顶，对应 <c>EX_PopExecutionFlowIfNot</c>（Delay 的等待循环）。</summary>
public sealed record PopFlowIfNotNode(KArg Condition) : KNode($"PopFlowIfNot {ArgRender.Of(Condition)}");

/// <summary>
/// 给数组赋值（把元素写进已有数组变量），对应 <c>EX_SetArray</c>。
/// 注意区别于 <c>EX_ArrayConst</c>——那只是字面量。
/// </summary>
public sealed record SetArrayNode(KArg Target, FPackageIndex Inner, IReadOnlyList<KArg> Elements)
    : KNode($"SetArray {ArgRender.Of(Target)}[{Elements.Count}]");

/// <summary>
/// ubergraph 的一个入口段。
///
/// <para>
/// 「展开」后的产物：一段就是蓝图里的一个事件（或一个基本块），
/// 段尾由 <see cref="Terminator"/> 决定怎么离开——跳尾声、回执行流栈、或跳到另一段。
/// </para>
/// </summary>
public sealed record UberSectionNode(
    uint StartOffset,
    IReadOnlyList<KNode> Nodes,
    UberTerminator Terminator,
    int? TargetOrdinal = null,
    KArg? Condition = null,
    string? Label = null)
    : KNode(
        "Section @" + StartOffset +
        (Label is { Length: > 0 } ? " " + Label : "") +
        " " + Terminator +
        (TargetOrdinal is { } t ? $" -> #{t}" : ""));

/// <summary>
/// 基本块图：函数无法还原成嵌套树时的可读表示（同一指令流的划分，只把跳转换成块标号）。
/// </summary>
public sealed record BlockGraphNode(IReadOnlyList<BasicBlockNode> Blocks)
    : KNode($"BlockGraph ({Blocks.Count} 块)");

/// <summary>基本块图里的一块：直线代码 + 出口。</summary>
public sealed record BasicBlockNode(
    int Index,
    uint StartOffset,
    IReadOnlyList<KNode> Nodes,
    BlockExit Exit,
    int? TargetIndex = null,
    KArg? Condition = null)
    : KNode(
        $"Block #{Index} @{StartOffset} " + Exit +
        (TargetIndex is { } t ? $" -> #{t}" : "") +
        (Condition is { } c ? $" ({ArgRender.Of(c)})" : ""));

/// <summary>
/// 整个 ubergraph：调度头 + 各入口段 + 尾声。
///
/// <para>
/// <b>入口表不在字节码里</b>，所以这里记的 <c>StartOffset</c> 只是原布局的偏移。
/// 重新烘焙时按 <see cref="Sections"/> 的顺序生成、所有跳转目标重算；
/// 真正的入口偏移烘在**调用点**（<c>LocalFinal ExecuteUbergraph_X(&lt;IntConst&gt;)</c>）里，
/// 由 <c>KismetWorkspace.UbergraphCallSites</c> 扫出、<c>CheckUbergraphEntryPoints</c> 校验。
/// </para>
/// </summary>
public sealed record UberGraphNode(
    KArg DispatchExpression,
    bool PushesEpilogue,
    IReadOnlyList<UberSectionNode> Sections)
    : KNode($"UberGraph ({Sections.Count} 段)");

/// <summary>把参数渲染成简短可读文本（节点标题与反编译输出共用）。</summary>
public static class ArgRender
{
    public static string Of(KArg? arg) => arg switch
    {
        null => "?",
        IntArg i => i.Value.ToString(),
        LongArg l => l.Value.ToString(),
        ByteArg b => b.Value.ToString(),
        FloatArg f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DoubleArg d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BoolArg b => b.Value ? "true" : "false",
        LocalArg l => l.Name,
        PointerArg p => p.Name,
        StringArg s => $"\"{s.Value}\"",
        NameArg n => n.Value.ToString(),
        ObjectArg o => $"<obj {o.Value.Index}>",
        NoObjectArg => "<none>",
        SelfArg => "this",
        CallArg c => $"{c.Callee.Name}(...)",
        _ => arg.GetType().Name.Replace("Arg", "")
    };

    /// <summary>渲染一串参数：<c>(a, b, c)</c>。</summary>
    public static string List(IReadOnlyList<KArg> args)
        => args.Count == 0 ? "()" : "(" + string.Join(", ", args.Select(Of)) + ")";
}
