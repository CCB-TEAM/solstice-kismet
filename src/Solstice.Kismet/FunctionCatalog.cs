using System.Text.Json;

namespace Solstice.Kismet;

/// <summary>函数的一个参数（来自 jmap 的函数表）。</summary>
public sealed record FunctionParam(string Name, string Type, string Dir, string Class, string Enum);

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

    public bool HasReturn => Params.Any(p => p.Dir == "return");
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
                        parameters.Add(new FunctionParam(
                            Str(p, "name"),
                            Str(p, "type"),
                            Str(p, "dir"),
                            Str(p, "class"),
                            Str(p, "enum")));
                    }
                }

                var sig = new FunctionSignature(name, owner.Name, flags, parameters);
                catalog._byFullPath[sig.FullPath] = sig;
                catalog._byShortName.TryAdd(name, sig);
            }
        }

        return catalog;
    }

    private static string Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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
