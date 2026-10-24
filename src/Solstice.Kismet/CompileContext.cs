using UAssetAPI.UnrealTypes;
using UAssetAPI.Kismet.Bytecode;

namespace Solstice.Kismet;

/// <summary>
/// 编译所需的资产本地上下文。
///
/// <para>
/// 生成 Kismet 表达式时，有些字段无法凭空构造，必须借助目标资产：
/// </para>
///
/// <list type="bullet">
/// <item><c>Measure</c> —— <c>EX_Context.Offset</c> 需要子表达式的序列化长度（<c>GetSize(asset)</c>）</item>
/// <item><c>ResolveName</c> —— <c>EX_LocalVirtualFunction.VirtualFunctionName</c> 是 <c>FName</c>，需要资产的名字表</item>
/// </list>
///
/// <para>
/// 这些都通过回调注入，<see cref="KismetCompiler"/> 本身仍然不持有 <c>UAsset</c>，
/// 因此可以脱离真实资产做单元测试。
/// </para>
/// </summary>
public sealed record CompileContext(
    IReadOnlyDictionary<string, KismetPropertyPointer>? Variables = null,
    IReadOnlyDictionary<string, FPackageIndex>? Functions = null,
    Func<KismetExpression, uint>? Measure = null,
    Func<string, FName>? ResolveName = null,
    IReadOnlyDictionary<(string Name, string Owner), FPackageIndex>? FunctionsByOwner = null)
{
    public static CompileContext Empty { get; } = new();
}
