using UAssetAPI.UnrealTypes;

namespace Solstice.Kismet;

/// <summary>
/// 一次函数调用的**引用**：名字 + 归属类 + 已解析的包索引。
///
/// <para>
/// 为什么要它：只给函数名意味着「按字符串查表」，而同名函数可以挂在不同类下
/// （两个类都有 <c>GetSession</c>），查表就只能取第一个——那是猜。
/// 引用把归属和索引一起带过来，调用方解析一次，之后所有操作都无歧义。
/// </para>
///
/// <para>
/// 由 <see cref="KismetWorkspace.ResolveFunction(string, string?)"/> 产出。
/// </para>
/// </summary>
/// <param name="Name">函数短名（如 <c>Greater_IntInt</c>）。</param>
/// <param name="OwnerPath">归属类全路径（如 <c>/Script/Engine.KismetMathLibrary</c>）；本资产函数为空。</param>
/// <param name="Index">包索引：负数为 Import，正数为本包 Export。</param>
/// <param name="IsExported">true 表示这是本资产导出的函数（编译成 <c>EX_LocalVirtualFunction</c>）。</param>
/// <param name="Signature">jmap 签名（可选，决定调用形态与参数默认值）。</param>
public sealed record FunctionRef(
    string Name,
    string OwnerPath,
    FPackageIndex Index,
    bool IsExported,
    FunctionSignature? Signature = null)
{
    /// <summary>完整路径，如 <c>/Script/Engine.KismetMathLibrary.Greater_IntInt</c>。</summary>
    public string FullPath => string.IsNullOrEmpty(OwnerPath) ? Name : $"{OwnerPath}.{Name}";

    public override string ToString() => FullPath;
}
