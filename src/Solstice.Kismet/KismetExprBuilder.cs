using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace Solstice.Kismet;

/// <summary>把数据流参数（KArg）转成 Kismet 表达式。</summary>
public static class KismetExprBuilder
{
    public static KismetExpression ToExpression(
        KArg arg, IReadOnlyDictionary<string, KismetPropertyPointer>? variables)
        => arg switch
        {
            IntArg i => new EX_IntConst { Value = i.Value },
            LocalArg l when variables is not null && variables.TryGetValue(l.Name, out var p)
                => new EX_LocalVariable { Variable = p },
            _ => new EX_Nothing()
        };
}
