using System.Reflection;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Solstice.Kismet;

/// <summary>
/// 递归遍历表达式树。
///
/// <para>
/// Kismet 表达式是嵌套结构：EX_Context 的 ObjectExpression / ContextExpression、
/// EX_JumpIfNot 的 BooleanExpression、EX_Return 的 ReturnExpression 都内嵌子表达式。
/// 只看顶层数组会漏掉大量内容（实测 ExecuteUbergraph 顶层 56 条，递归后 144 条）。
/// </para>
/// </summary>
public static class ExpressionWalker
{
    public static IEnumerable<KismetExpression> Walk(IEnumerable<KismetExpression> roots)
    {
        foreach (var root in roots)
        {
            foreach (var e in Walk(root))
            {
                yield return e;
            }
        }
    }

    public static IEnumerable<KismetExpression> Walk(KismetExpression root)
    {
        if (root is null)
        {
            yield break;
        }

        yield return root;

        foreach (var field in root.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(KismetExpression))
            {
                if (field.GetValue(root) is KismetExpression child)
                {
                    foreach (var e in Walk(child))
                    {
                        yield return e;
                    }
                }
            }
            else if (field.FieldType == typeof(KismetExpression[]))
            {
                if (field.GetValue(root) is KismetExpression[] children)
                {
                    foreach (var c in children)
                    {
                        foreach (var e in Walk(c))
                        {
                            yield return e;
                        }
                    }
                }
            }
        }
    }

    /// <summary>收集所有变量表达式（LocalVariable / InstanceVariable 等）。</summary>
    public static IReadOnlyList<EX_VariableBase> CollectVariables(IEnumerable<KismetExpression> roots)
        => Walk(roots).OfType<EX_VariableBase>().ToArray();

    /// <summary>
    /// 建立「变量名 → 属性指针」索引。
    ///
    /// <para>
    /// FFieldPath.Path 是 FName[]（变量名路径），ResolvedOwner 是 FPackageIndex
    /// （正数为本包导出、负数为导入）。生成 EX_LocalVariable / EX_InstanceVariable
    /// 时直接复用这些指针，不必自己构造 FFieldPath。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, KismetPropertyPointer> BuildVariableIndex(IEnumerable<KismetExpression> roots)
    {
        var index = new Dictionary<string, KismetPropertyPointer>(StringComparer.Ordinal);

        foreach (var v in CollectVariables(roots))
        {
            var pointer = v.Variable;
            if (pointer?.New?.Path is not { Length: > 0 } path)
            {
                continue;
            }

            var full = string.Join(".", path.Select(p => p.ToString()));

            // 同时索引全路径与最后一段。
            // 反编译产出的名字是**最后一段**（TargetName），而 FFieldPath 可能是多段的
            // （结构体成员链），只索引全路径会让按短名的查询全部落空——
            // 编译侧就会退化成空指针，字节看着对、指向错。
            index.TryAdd(full, pointer);
            if (path.Length > 1)
            {
                index.TryAdd(path[^1].ToString(), pointer);
            }
        }

        return index;
    }
}
