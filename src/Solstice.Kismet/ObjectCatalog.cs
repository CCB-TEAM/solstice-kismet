using System.Text.Json;

namespace Solstice.Kismet;

/// <summary>对象表里的一条：类型、outer、class、super。</summary>
public sealed record ObjectInfo(string Path, string Type, string? Outer, string? Class, string? Super);

/// <summary>
/// jmap 的对象表（<c>objects.json</c>）：对象全路径 → 类型 / outer / class / super。
///
/// <para>
/// 用途是把「归属类全路径」拆成 包 + 类 时不必靠字符串猜：
/// 表里能查到该路径确实是 <c>Class</c>，以及它的 <c>outer</c> 是哪个包。
/// 这比 <c>LastIndexOf('.')</c> 可靠——类名里带点、包是嵌套路径时字符串拆分会错。
/// </para>
/// </summary>
public sealed class ObjectCatalog
{
    private readonly Dictionary<string, ObjectInfo> _byPath = new(StringComparer.Ordinal);

    public int Count => _byPath.Count;

    public static ObjectCatalog Load(string json)
    {
        var catalog = new ObjectCatalog();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("objects", out var objects))
        {
            return catalog;
        }

        foreach (var entry in objects.EnumerateObject())
        {
            catalog._byPath[entry.Name] = new ObjectInfo(
                entry.Name,
                Str(entry.Value, "type"),
                StrOrNull(entry.Value, "outer"),
                StrOrNull(entry.Value, "class"),
                StrOrNull(entry.Value, "super"));
        }

        return catalog;
    }

    public ObjectInfo? Find(string path) => _byPath.TryGetValue(path, out var info) ? info : null;

    /// <summary>
    /// 把归属类全路径拆成 包路径 + 类名。对象表里查得到就按 <c>outer</c> 走，
    /// 查不到才退回字符串拆分（最后一个点）。
    /// </summary>
    public (string PackagePath, string ClassName) SplitOwner(string ownerClassPath)
    {
        if (_byPath.TryGetValue(ownerClassPath, out var info) && !string.IsNullOrEmpty(info.Outer))
        {
            return (info.Outer, LastSegment(ownerClassPath));
        }

        var dot = ownerClassPath.LastIndexOf('.');
        return dot < 0
            ? (ownerClassPath, ownerClassPath)
            : (ownerClassPath[..dot], ownerClassPath[(dot + 1)..]);
    }

    /// <summary>该路径在表里是不是一个类（用于在新增调用前校验归属）。</summary>
    public bool IsClass(string path)
        => _byPath.TryGetValue(path, out var info) && string.Equals(info.Type, "Class", StringComparison.Ordinal);

    /// <summary>
    /// 把「类短名或全路径」解析成类全路径。
    ///
    /// <para>
    /// 全路径（含点且表里存在）原样返回；否则按短名匹配，**唯一才返回**，
    /// 多个则抛异常列出候选——不让调用方在歧义上瞎猜。
    /// </para>
    /// </summary>
    public string ResolveClass(string classRef)
    {
        if (classRef.Contains('.') && _byPath.TryGetValue(classRef, out var exact)
            && string.Equals(exact.Type, "Class", StringComparison.Ordinal))
        {
            return classRef;
        }

        var shortName = LastSegment(classRef);
        var matches = _byPath
            .Where(kv => string.Equals(kv.Value.Type, "Class", StringComparison.Ordinal)
                         && string.Equals(LastSegment(kv.Key), shortName, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToArray();

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"对象表里没有名为 {classRef} 的类");
        }

        throw new InvalidOperationException(
            $"{classRef} 匹配到 {matches.Length} 个类，无法确定：\n  " + string.Join("\n  ", matches));
    }

    private static string LastSegment(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? StrOrNull(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
