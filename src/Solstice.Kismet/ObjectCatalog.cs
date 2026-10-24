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
