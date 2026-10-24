using System.Globalization;

namespace Solstice.Kismet;

/// <summary>
/// 节点路径：用一串下标（<c>"0.2.1"</c>）在节点树里寻址。
///
/// <para>
/// 反编译输出里每个节点前面都会带自己的路径（<c>#0.2</c>），
/// agent 拿这个路径就能精确地删一个节点、在某个节点前插一次调用、或替换掉一次调用。
/// 路径只依赖**当前反编译结果的结构**，不依赖字节偏移——所以改完之后再反编译，路径会变，
/// 这是预期的（和 UE 里改了图之后引脚 ID 变化一样）。
/// </para>
///
/// <para>
/// 复合节点的子节点按「展开后的顺序」编号：<see cref="IfNode"/> 是 Then 接着 Else，
/// <see cref="BottomLoopNode"/> 是 Body 接着 Tail。寻址与修改都按这个顺序，
/// 但重建时会还原到原来那个子列表里——不会把 Then 的节点错放进 Else。
/// </para>
/// </summary>
public static class NodePaths
{
    public static bool TryParse(string path, out int[] indices)
    {
        indices = [];
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var parts = path.Trim().TrimStart('#').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                return false;
            }

            parsed[i] = value;
        }

        indices = parsed;
        return true;
    }

    /// <summary>按路径取节点；路径非法或越界时抛异常（消息里带上合法范围）。</summary>
    public static KNode Find(IReadOnlyList<KNode> roots, string path)
    {
        var indices = Parse(path);
        var list = roots;
        KNode? current = null;

        for (var depth = 0; depth < indices.Length; depth++)
        {
            CheckIndex(list, indices[depth], path);
            current = list[indices[depth]];

            if (depth < indices.Length - 1)
            {
                var (childList, offset, _) = Select(current, indices[depth + 1], path);
                list = childList;
                indices[depth + 1] -= offset;   // 换成「该子列表内的下标」
            }
        }

        return current!;
    }

    /// <summary>删掉路径指向的节点。</summary>
    public static IReadOnlyList<KNode> Delete(IReadOnlyList<KNode> roots, string path)
        => MapAt(roots, Parse(path), 0, path, (list, index) =>
        {
            CheckIndex(list, index, path);
            return list.Where((_, i) => i != index).ToArray();
        });

    /// <summary>在路径指向的节点**之前**插入一个节点。</summary>
    public static IReadOnlyList<KNode> InsertBefore(IReadOnlyList<KNode> roots, string path, KNode node)
        => MapAt(roots, Parse(path), 0, path, (list, index) =>
        {
            CheckIndex(list, index, path);
            var copy = list.ToList();
            copy.Insert(index, node);
            return copy;
        });

    /// <summary>替换路径指向的节点。</summary>
    public static IReadOnlyList<KNode> Replace(IReadOnlyList<KNode> roots, string path, KNode node)
        => MapAt(roots, Parse(path), 0, path, (list, index) =>
        {
            CheckIndex(list, index, path);
            var copy = list.ToList();
            copy[index] = node;
            return copy;
        });

    private static int[] Parse(string path)
        => TryParse(path, out var indices)
            ? indices
            : throw new InvalidOperationException($"节点路径 {path} 不合法（形如 \"0\" 或 \"0.2.1\"）");

    private static void CheckIndex(IReadOnlyList<KNode> list, int index, string path)
    {
        if (index >= list.Count)
        {
            throw new InvalidOperationException(
                $"路径 {path} 越界：当前层只有 {list.Count} 个节点（0..{list.Count - 1}）");
        }
    }

    /// <summary>
    /// 从父节点里挑出「第 <paramref name="index"/> 个子节点」所在的子列表，
    /// 返回该列表、列表在展开顺序里的起点偏移、以及「用新的子列表重建父节点」的方法。
    /// </summary>
    private static (IReadOnlyList<KNode> List, int Offset, Func<IReadOnlyList<KNode>, KNode> Rebuild) Select(
        KNode parent, int index, string path) => parent switch
    {
        IfNode f => index < f.Then.Count
            ? (f.Then, 0, children => f with { Then = children })
            : (f.Else ?? [], f.Then.Count, children => f with { Else = children }),

        BottomLoopNode b => index < b.Body.Count
            ? (b.Body, 0, children => b with { Body = children })
            : (b.Tail, b.Body.Count, children => b with { Tail = children }),

        LoopNode l => (l.Body, 0, children => l with { Body = children }),
        BasicBlockNode b => (b.Nodes, 0, children => b with { Nodes = children }),
        BlockGraphNode g => (g.Blocks, 0, children => g with { Blocks = children.Cast<BasicBlockNode>().ToArray() }),
        UberGraphNode u => (u.Sections, 0, children => u with { Sections = children.Cast<UberSectionNode>().ToArray() }),
        UberSectionNode s => (s.Nodes, 0, children => s with { Nodes = children }),
        _ => throw new InvalidOperationException(
            $"路径 {path} 想下钻到 {parent.GetType().Name}（{parent.Title}），但它没有子节点")
    };

    /// <summary>递归到路径的父层，用 <paramref name="transform"/> 改那一层。</summary>
    private static IReadOnlyList<KNode> MapAt(
        IReadOnlyList<KNode> list,
        int[] indices,
        int depth,
        string path,
        Func<IReadOnlyList<KNode>, int, IReadOnlyList<KNode>> transform)
    {
        if (depth == indices.Length - 1)
        {
            return transform(list, indices[depth]);
        }

        CheckIndex(list, indices[depth], path);
        var (childList, offset, rebuild) = Select(list[indices[depth]], indices[depth + 1], path);

        var adjusted = (int[])indices.Clone();
        adjusted[depth + 1] -= offset;
        var newChild = MapAt(childList, adjusted, depth + 1, path, transform);

        var copy = list.ToList();
        copy[indices[depth]] = rebuild(newChild);
        return copy;
    }
}
