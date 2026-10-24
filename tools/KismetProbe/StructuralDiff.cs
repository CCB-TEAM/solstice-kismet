using System.Reflection;
using UAssetAPI;
using UAssetAPI.Kismet.Bytecode;

namespace KismetProbe;

/// <summary>
/// 直接比对两棵解析后的表达式树，报出第一处结构差异。
/// 比逐字节 diff 有用得多：能直接看出是哪个子表达式的类型/参数不同。
/// </summary>
public static class StructuralDiff
{
    public static bool Report(
        IReadOnlyList<KismetExpression> original,
        IReadOnlyList<KismetExpression> rebuilt,
        UAsset asset)
    {
        Console.WriteLine();
        Console.WriteLine("=== 结构对比 ===");
        Console.WriteLine($"  原始 {original.Count} 条 / 回编译 {rebuilt.Count} 条");

        var n = Math.Min(original.Count, rebuilt.Count);
        for (var i = 0; i < n; i++)
        {
            if (!Compare(original[i], rebuilt[i], asset, i.ToString(), 1))
            {
                return false;
            }
        }

        if (original.Count != rebuilt.Count)
        {
            Console.WriteLine($"  条数不同：原始 {original.Count}，回编译 {rebuilt.Count}");
            var longer = original.Count > rebuilt.Count ? original : rebuilt;
            var from = n;
            for (var i = from; i < longer.Count; i++)
            {
                Console.WriteLine($"    [{(original.Count > rebuilt.Count ? "仅原始" : "仅回编译")}] {i}: {longer[i].Inst}  size={longer[i].GetSize(asset)}");
            }

            return false;
        }

        Console.WriteLine("  结构完全一致");
        return true;
    }

    private static bool Compare(KismetExpression a, KismetExpression b, UAsset asset, string path, int depth)
    {
        var pad = new string(' ', depth * 2 + 2);

        if (a.Inst != b.Inst)
        {
            Console.WriteLine($"{pad}✗ [{path}] 类型不同：原始 {a.Inst}(size={a.GetSize(asset)})  vs  回编译 {b.Inst}(size={b.GetSize(asset)})");
            Dump(a, asset, pad + "  原始: ");
            Dump(b, asset, pad + "  回编译: ");
            return false;
        }

        var sizeA = a.GetSize(asset);
        var sizeB = b.GetSize(asset);
        if (sizeA != sizeB)
        {
            Console.WriteLine($"{pad}✗ [{path}] {a.Inst} 长度不同：{sizeA} vs {sizeB}");
        }

        var fields = a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        var ok = sizeA == sizeB;

        foreach (var field in fields)
        {
            if (field.FieldType == typeof(KismetExpression))
            {
                var ca = field.GetValue(a) as KismetExpression;
                var cb = field.GetValue(b) as KismetExpression;
                if (ca is null && cb is null) { continue; }
                if (ca is null || cb is null)
                {
                    Console.WriteLine($"{pad}✗ [{path}.{field.Name}] 一侧为空：{ca?.Inst ?? "null"} vs {cb?.Inst ?? "null"}");
                    ok = false;
                    continue;
                }

                if (!Compare(ca, cb, asset, $"{path}.{field.Name}", depth + 1)) { ok = false; }
            }
            else if (field.FieldType == typeof(KismetExpression[]))
            {
                var ca = field.GetValue(a) as KismetExpression[] ?? [];
                var cb = field.GetValue(b) as KismetExpression[] ?? [];
                if (ca.Length != cb.Length)
                {
                    Console.WriteLine($"{pad}✗ [{path}.{field.Name}] 子表达式个数不同：{ca.Length} vs {cb.Length}");
                    Console.WriteLine($"{pad}   原始: {string.Join(", ", ca.Select(x => x.Inst))}");
                    Console.WriteLine($"{pad}   回编译: {string.Join(", ", cb.Select(x => x.Inst))}");
                    ok = false;
                    continue;
                }

                for (var i = 0; i < ca.Length; i++)
                {
                    if (!Compare(ca[i], cb[i], asset, $"{path}.{field.Name}[{i}]", depth + 1)) { ok = false; }
                }
            }
        }

        return ok;
    }

    private static void Dump(KismetExpression e, UAsset asset, string prefix)
    {
        var fields = e.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(KismetExpression) || f.FieldType == typeof(KismetExpression[]))
            .ToList();

        var kids = new List<string>();
        foreach (var f in fields)
        {
            if (f.GetValue(e) is KismetExpression c) { kids.Add($"{f.Name}={c.Inst}"); }
            else if (f.GetValue(e) is KismetExpression[] arr) { kids.Add($"{f.Name}[{string.Join(",", arr.Select(x => x.Inst))}]"); }
        }

        Console.WriteLine($"{prefix}{e.Inst} size={e.GetSize(asset)} {string.Join(" ", kids)}");
    }
}
