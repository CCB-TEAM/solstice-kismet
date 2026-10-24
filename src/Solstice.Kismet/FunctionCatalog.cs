using System.Text.Json;

namespace Solstice.Kismet;

/// <summary>函数的一个参数（来自 jmap 的函数表）。</summary>
/// <param name="Class">
/// 对象类的路径；**结构体则是结构路径**（jmap 的 <c>struct</c> 字段，老产物里为 null）。
/// </param>
/// <param name="Enum">枚举路径（ByteProperty / EnumProperty）。</param>
/// <param name="Inner">数组的元素类型（jmap 的 <c>inner</c>）。</param>
/// <param name="Key">Set 的元素类型 / Map 的键类型（jmap 的 <c>key_prop</c>）。</param>
/// <param name="Value">Map 的值类型（jmap 的 <c>value_prop</c>）。</param>
public sealed record FunctionParam(
    string Name,
    string Type,
    string Dir,
    string Class,
    string Enum,
    int Offset = 0,
    int Size = 0,
    FunctionParam? Inner = null,
    FunctionParam? Key = null,
    FunctionParam? Value = null);

/// <summary>函数签名。</summary>
public sealed record FunctionSignature(
    string Name,
    string OwnerPath,
    string Flags,
    IReadOnlyList<FunctionParam> Params)
{
    public string FullPath => string.IsNullOrEmpty(OwnerPath) ? Name : $"{OwnerPath}.{Name}";

    public bool IsStatic => Flags.Contains("FUNC_Static", StringComparison.Ordinal);

    /// <summary>需要传进去的参数（dir = in）。</summary>
    public IReadOnlyList<FunctionParam> Inputs
        => Params.Where(p => string.Equals(p.Dir, "in", StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>产出的值（dir = return / out）。</summary>
    public IReadOnlyList<FunctionParam> Outputs
        => Params.Where(p => p.Dir is "return" or "out").ToArray();

    /// <summary>
    /// 返回值参数（<c>dir = "return"</c>）。
    ///
    /// <para>
    /// 调用产生的临时变量（<c>CallFunc_XXX_ReturnValue</c>）的类型就是它——
    /// 不需要去读资产的局部变量表。注意 <c>StructProperty</c> 的 <c>Class</c> 在 jmap 里
    /// 常为 null（只知道是结构体与字节大小，不知道是哪个结构）。
    /// </para>
    /// </summary>
    public FunctionParam? ReturnParam
        => Params.FirstOrDefault(p => string.Equals(p.Dir, "return", StringComparison.Ordinal));

    public bool HasReturn => ReturnParam is not null;
}

/// <summary>
/// jmap 函数表（<c>functions.json</c> / <c>functions.kards.json</c>）。
///
/// <para>结构：</para>
/// <code>
/// { "objects": { "/Script/Engine.KismetSystemLibrary": {
///       "functions": { "IsValid": { "flags": "FUNC_...", "params": [ { "name","type","dir","class","enum" } ] } } } } }
/// </code>
///
/// <para>
/// 注意：有的产物里 <c>functions</c> 的 key 是函数短名，有的是完整路径，这里两种都接受。
/// </para>
/// </summary>
public sealed class FunctionCatalog
{
    private readonly Dictionary<string, FunctionSignature> _byFullPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSignature> _byShortName = new(StringComparer.Ordinal);

    public int Count => _byFullPath.Count;

    public static FunctionCatalog Load(string json)
    {
        var catalog = new FunctionCatalog();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("objects", out var objects))
        {
            return catalog;
        }

        foreach (var owner in objects.EnumerateObject())
        {
            if (!owner.Value.TryGetProperty("functions", out var funcs))
            {
                continue;
            }

            foreach (var fn in funcs.EnumerateObject())
            {
                var name = fn.Name.Contains('.')
                    ? fn.Name[(fn.Name.LastIndexOf('.') + 1)..]
                    : fn.Name;

                var flags = fn.Value.TryGetProperty("flags", out var f) ? f.GetString() ?? "" : "";

                var parameters = new List<FunctionParam>();
                if (fn.Value.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in ps.EnumerateArray())
                    {
                        parameters.Add(ReadParam(p));
                    }
                }

                var sig = new FunctionSignature(name, owner.Name, flags, parameters);
                catalog._byFullPath[sig.FullPath] = sig;
                catalog._byShortName.TryAdd(name, sig);
            }
        }

        return catalog;
    }

    /// <summary>
    /// 读一个参数，连容器里的元素类型一起读。
    /// 老产物没有 <c>inner</c> / <c>key</c> / <c>value</c>，读出来是 null——上层要能容忍。
    /// </summary>
    private static FunctionParam ReadParam(JsonElement p) => new(
        Str(p, "name"),
        Str(p, "type"),
        Str(p, "dir"),
        Str(p, "class"),
        Str(p, "enum"),
        Int(p, "offset"),
        Int(p, "size"),
        Nested(p, "inner"),
        Nested(p, "key"),
        Nested(p, "value"));

    private static FunctionParam? Nested(JsonElement p, string name)
        => p.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadParam(nested)
            : null;

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>优先按完整路径找，找不到再退回短名（同名函数可能有多份，取第一个）。</summary>
    public FunctionSignature? Find(string? ownerPath, string name)
    {
        if (!string.IsNullOrEmpty(ownerPath)
            && _byFullPath.TryGetValue($"{ownerPath}.{name}", out var byPath))
        {
            return byPath;
        }

        return _byShortName.TryGetValue(name, out var byName) ? byName : null;
    }

    public FunctionSignature? FindByShortName(string name)
        => _byShortName.TryGetValue(name, out var s) ? s : null;

    public IReadOnlyCollection<FunctionSignature> All => _byFullPath.Values;
}
