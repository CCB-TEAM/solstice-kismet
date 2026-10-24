using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace Solstice.Kismet;

/// <summary>
/// 从资产的 Import 表建立「函数名 → FPackageIndex」索引，并按需新增条目。
///
/// <para>
/// 实测 Import 链的形状（card_brawl_test1）：
/// </para>
///
/// <code>
/// #42 idx=-43  /Script/CoreUObject   cls=Package   outer=0     包
/// #43 idx=-44  /Script/Engine        cls=Package   outer=0
/// #10 idx=-11  KismetSystemLibrary   cls=Class     outer=-44   类，outer 指向包
/// #18 idx=-19  IsValid               cls=Function  outer=-11   函数，outer 指向类
/// </code>
///
/// <para>
/// 新增条目一律**克隆**同类现有导入，沿用它的 ClassPackage/ClassName/PackageName，
/// 只替换 ObjectName 与 OuterIndex —— 不猜这些字段的值。
/// </para>
/// </summary>
public static class ImportIndex
{
    public static IReadOnlyDictionary<string, FPackageIndex> BuildFunctionIndex(UAsset asset)
    {
        var index = new Dictionary<string, FPackageIndex>(StringComparer.Ordinal);

        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var im = asset.Imports[i];
            if (im.ClassName?.ToString() != "Function")
            {
                continue;
            }

            var name = im.ObjectName?.ToString();
            if (string.IsNullOrEmpty(name) || name == "Default__Function")
            {
                continue;
            }

            index.TryAdd(name, new FPackageIndex(-(i + 1)));
        }

        return index;
    }

    /// <summary>
    /// 保证函数在 Import 表里有条目，返回它的 FPackageIndex。
    ///
    /// <para>
    /// 给了 <paramref name="ownerClassPath"/>（形如 <c>/Script/Engine.Actor</c>）时，
    /// 会先补齐 包 → 类 两级导入，再把函数挂到该类下面；
    /// 没给时退化为沿用模板的 Outer（只适用于与模板同类的函数）。
    /// </para>
    /// </summary>
    /// <summary>
    /// 按 <c>(函数名, 归属类全路径)</c> 建索引。归属类由导入链推出：
    /// Function 的 outer 是 Class，Class 的 outer 是 Package。
    /// </summary>
    public static Dictionary<(string Name, string Owner), FPackageIndex> BuildFunctionIndexByOwner(UAsset asset)
    {
        var index = new Dictionary<(string, string), FPackageIndex>();

        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var entry = asset.Imports[i];
            if (entry.ClassName?.ToString() != "Function")
            {
                continue;
            }

            var name = entry.ObjectName?.ToString();
            if (string.IsNullOrEmpty(name) || name == "Default__Function")
            {
                continue;
            }

            index.TryAdd((name, OwnerPathOf(asset, entry.OuterIndex)), new FPackageIndex(-(i + 1)));
        }

        return index;
    }

    private static string OwnerPathOf(UAsset asset, FPackageIndex? index)
    {
        var raw = index?.Index ?? 0;
        if (raw >= 0)
        {
            return raw == 0 || raw > asset.Exports.Count
                ? ""
                : asset.Exports[raw - 1].ObjectName?.ToString() ?? "";
        }

        var cursor = -raw - 1;
        if (cursor < 0 || cursor >= asset.Imports.Count)
        {
            return "";
        }

        var classImport = asset.Imports[cursor];
        var className = classImport.ObjectName?.ToString() ?? "";
        var outer = classImport.OuterIndex?.Index ?? 0;

        if (outer < 0 && -outer - 1 < asset.Imports.Count)
        {
            var package = asset.Imports[-outer - 1].ObjectName?.ToString() ?? "";
            return $"{package}.{className}";
        }

        return className;
    }

    /// <summary>
    /// 保证函数在 Import 表里有条目，返回它的 FPackageIndex。
    ///
    /// <para>
    /// 给了 <paramref name="ownerClassPath"/>（形如 <c>/Script/Engine.Actor</c>）时，
    /// 按 <c>(名字, 归属)</c> 精确匹配——同名函数可能挂在不同类下，
    /// 只按名字查会取到别人的那一个，新调用就指向了错误的函数。
    /// 找不到时才补齐 包 → 类 两级导入并新建。
    /// </para>
    /// </summary>
    public static FPackageIndex EnsureFunction(UAsset asset, string functionName, string? ownerClassPath = null)
    {
        if (!string.IsNullOrWhiteSpace(ownerClassPath))
        {
            if (BuildFunctionIndexByOwner(asset).TryGetValue((functionName, ownerClassPath), out var exact))
            {
                return exact;
            }
        }
        else
        {
            var existing = BuildFunctionIndex(asset);
            if (existing.TryGetValue(functionName, out var found))
            {
                return found;
            }
        }

        var funcTemplate = FindTemplate(asset, "Function", "Default__Function")
            ?? throw new InvalidOperationException("资产里没有可作模板的函数导入");

        FPackageIndex outer;

        if (!string.IsNullOrWhiteSpace(ownerClassPath))
        {
            var (packagePath, className) = SplitOwner(ownerClassPath);
            var packageIndex = EnsurePackage(asset, packagePath);
            outer = EnsureClass(asset, className, packageIndex);
        }
        else
        {
            outer = funcTemplate.OuterIndex ?? new FPackageIndex(0);
        }

        var clone = CloneAs(funcTemplate, new FName(asset, functionName), outer);
        asset.Imports.Add(clone);
        return new FPackageIndex(-asset.Imports.Count);
    }

    /// <summary>确保包导入存在（cls=Package，outer=0）。</summary>
    public static FPackageIndex EnsurePackage(UAsset asset, string packagePath)
    {
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var im = asset.Imports[i];
            if (im.ClassName?.ToString() == "Package" && im.ObjectName?.ToString() == packagePath)
            {
                return new FPackageIndex(-(i + 1));
            }
        }

        var template = FindTemplate(asset, "Package", null)
            ?? throw new InvalidOperationException("资产里没有可作模板的包导入");

        var clone = CloneAs(template, new FName(asset, packagePath), new FPackageIndex(0));
        asset.Imports.Add(clone);
        return new FPackageIndex(-asset.Imports.Count);
    }

    /// <summary>确保类导入存在（cls=Class，outer=包）。</summary>
    public static FPackageIndex EnsureClass(UAsset asset, string className, FPackageIndex packageIndex)
        => EnsureImportedObject(asset, className, packageIndex, "Class");

    /// <summary>确保枚举导入存在（cls=Enum，outer=包）——枚举属性（FEnumProperty.Enum）要它。</summary>
    public static FPackageIndex EnsureEnum(UAsset asset, string enumName, FPackageIndex packageIndex)
        => EnsureImportedObject(asset, enumName, packageIndex, "Enum");

    /// <summary>
    /// 确保一个「包里的对象」导入存在。<paramref name="importClass"/> 是 Import 的 ClassName
    /// （<c>Class</c> / <c>Enum</c> / …），按它找模板并克隆，不猜 ClassPackage。
    /// </summary>
    public static FPackageIndex EnsureImportedObject(
        UAsset asset, string objectName, FPackageIndex packageIndex, string importClass)
    {
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var im = asset.Imports[i];
            if (im.ClassName?.ToString() == importClass
                && im.ObjectName?.ToString() == objectName
                && im.OuterIndex?.Index == packageIndex.Index)
            {
                return new FPackageIndex(-(i + 1));
            }
        }

        var template = FindTemplate(asset, importClass, null);
        var clone = CloneAs(template, new FName(asset, objectName), packageIndex);
        asset.Imports.Add(clone);
        return new FPackageIndex(-asset.Imports.Count);
    }

    private static Import FindTemplate(UAsset asset, string className, string? excludeObject)
        => asset.Imports.FirstOrDefault(im =>
            im.ClassName?.ToString() == className &&
            im.ObjectName?.ToString() is { Length: > 0 } n &&
            n != excludeObject)
           // 接口类资产可能一条该类型的导入都没有，这时按 UE 的固定约定新建
           // （包/类/函数三级导入的 ClassPackage 都是 /Script/CoreUObject）
           ?? new Import(
               new FName(asset, "/Script/CoreUObject"),
               new FName(asset, className),
               new FPackageIndex(0),
               new FName(asset, className),
               false);

    private static Import CloneAs(Import template, FName objectName, FPackageIndex outer)
        => new(template.ClassPackage, template.ClassName, outer, objectName, template.bImportOptional)
        {
            PackageName = template.PackageName
        };

    /// <summary>把 <c>/Script/Engine.Actor</c> 拆成包路径与类名。</summary>
    public static (string PackagePath, string ClassName) SplitOwner(string ownerClassPath)
    {
        var dot = ownerClassPath.LastIndexOf('.');
        return dot < 0
            ? (ownerClassPath, ownerClassPath)
            : (ownerClassPath[..dot], ownerClassPath[(dot + 1)..]);
    }
}
