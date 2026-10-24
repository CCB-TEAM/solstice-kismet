using UAssetAPI.Unversioned;

namespace Solstice.Kismet;

/// <summary>结构体/类 schema 里的一个成员。</summary>
public sealed record StructMember(string Name, string TypeName, int ArrayIndex, int ArraySize);

/// <summary>
/// usmap 的结构体 schema 查询。
///
/// <para>
/// 字节码里的 <c>EX_StructMemberContext</c> 只带「成员字段路径 + 归属索引」，
/// 不告诉你是哪个结构的哪个类型。要按名字改结构体成员、或判断成员类型，
/// 必须查 usmap 的 schema——这就是这个类的用途。
/// </para>
/// </summary>
public sealed class UsmapCatalog
{
    private readonly Usmap _usmap;

    private UsmapCatalog(Usmap usmap) => _usmap = usmap;

    public static UsmapCatalog Load(string path) => new(new Usmap(path));

    public int SchemaCount => _usmap.Schemas.Count;

    /// <summary>全部 schema 名（通常是 <c>/Script/Module.Name</c> 全路径）。</summary>
    public IReadOnlyList<string> SchemaNames => _usmap.Schemas.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 把「短名或全路径」解析成 schema 全路径。唯一才接受，多个列出候选。
    /// </summary>
    public string ResolveSchema(string nameOrPath)
    {
        if (_usmap.Schemas.ContainsKey(nameOrPath))
        {
            return nameOrPath;
        }

        var shortName = nameOrPath.Contains('.') ? nameOrPath[(nameOrPath.LastIndexOf('.') + 1)..] : nameOrPath;
        var matches = _usmap.Schemas.Keys
            .Where(k => string.Equals(k[(k.LastIndexOf('.') + 1)..], shortName, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"usmap 里没有名为 {nameOrPath} 的结构");
        }

        throw new InvalidOperationException(
            $"{nameOrPath} 匹配到 {matches.Length} 个结构：\n  " + string.Join("\n  ", matches));
    }

    /// <summary>列出某个结构的成员（名字 + 类型），按声明顺序。</summary>
    public IReadOnlyList<StructMember> MembersOf(string nameOrPath)
    {
        var schema = _usmap.Schemas[ResolveSchema(nameOrPath)];
        return schema.Properties
            .OrderBy(kv => kv.Key)
            .Select(kv => new StructMember(
                kv.Value.Name,
                kv.Value.PropertyData?.Type.ToString() ?? "?",
                kv.Value.ArrayIndex,
                kv.Value.ArraySize))
            .ToArray();
    }

    /// <summary>查某个结构的某个成员；找不到返回 null。</summary>
    public StructMember? FindMember(string nameOrPath, string memberName)
        => MembersOf(nameOrPath)
            .FirstOrDefault(m => string.Equals(m.Name, memberName, StringComparison.Ordinal));

    /// <summary>该结构的所有祖先（沿 SuperType 往上）。</summary>
    public IReadOnlyList<string> SuperChainOf(string nameOrPath)
    {
        var chain = new List<string>();
        var current = ResolveSchema(nameOrPath);

        while (_usmap.Schemas.TryGetValue(current, out var schema))
        {
            var super = schema.SuperType;
            if (string.IsNullOrEmpty(super) || chain.Contains(super))
            {
                break;
            }

            chain.Add(super);
            current = super;
        }

        return chain;
    }
}
