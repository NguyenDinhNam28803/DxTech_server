using System.Reflection;

namespace server.UnitTests.Architecture;

/// <summary>
/// Kiểm tra quy tắc ranh giới module (ADR-001) bằng reflection thuần, không dùng thư viện ngoài.
/// Phạm vi: chữ ký của type — base type, interface, field, property, tham số/kiểu trả về của
/// method và constructor, kể cả generic argument. Không đọc thân method (IL); phần đó do review.
/// </summary>
internal static class ArchitectureRules
{
    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Type trong <c>{root}.Modules.A</c> chỉ được tham chiếu type của module khác nếu type đó nằm
    /// trong <c>{root}.Modules.B.Contracts</c>.
    /// </summary>
    public static IReadOnlyList<string> CrossModuleViolations(Assembly assembly, string rootNamespace)
    {
        string modulesPrefix = rootNamespace + ".Modules.";
        var violations = new List<string>();

        foreach (Type type in SafeGetTypes(assembly).Where(t => t.Namespace?.StartsWith(modulesPrefix) == true))
        {
            string ownModule = ModuleOf(type, modulesPrefix)!;
            foreach (Type referenced in ReferencedTypes(type))
            {
                if (referenced.Namespace?.StartsWith(modulesPrefix) != true)
                {
                    continue;
                }

                string otherModule = ModuleOf(referenced, modulesPrefix)!;
                bool isContract = referenced.Namespace!.StartsWith($"{modulesPrefix}{otherModule}.Contracts");
                if (otherModule != ownModule && !isContract)
                {
                    violations.Add($"{type.FullName} → {referenced.FullName} (chỉ được dùng {modulesPrefix}{otherModule}.Contracts)");
                }
            }
        }

        return violations.Distinct().ToList();
    }

    /// <summary>Type trong namespace <paramref name="source"/> không được phụ thuộc namespace <paramref name="forbidden"/>.</summary>
    public static IReadOnlyList<string> NamespaceDependencyViolations(Assembly assembly, string source, string forbidden)
        => SafeGetTypes(assembly)
            .Where(t => t.Namespace?.StartsWith(source) == true)
            .SelectMany(t => ReferencedTypes(t)
                .Where(r => r.Namespace?.StartsWith(forbidden) == true)
                .Select(r => $"{t.FullName} → {r.FullName}"))
            .Distinct()
            .ToList();

    /// <summary>
    /// Trong module, chỉ Contracts, Api (controller phải public để MVC tìm thấy) và lớp đăng ký
    /// <c>*Module</c> được để public; Domain, Persistence, Features phải là internal.
    /// </summary>
    public static IReadOnlyList<string> PublicSurfaceViolations(Assembly assembly, string rootNamespace)
    {
        string modulesPrefix = rootNamespace + ".Modules.";
        return SafeGetTypes(assembly)
            .Where(t => t.Namespace?.StartsWith(modulesPrefix) == true && t.IsVisible && !IsCompilerGenerated(t))
            .Where(t =>
            {
                string module = ModuleOf(t, modulesPrefix)!;
                string ns = t.Namespace!;
                bool allowedNamespace = ns.StartsWith($"{modulesPrefix}{module}.Contracts") || ns.StartsWith($"{modulesPrefix}{module}.Api");
                bool isRegistration = ns == $"{modulesPrefix}{module}" && t.Name.EndsWith("Module");
                return !allowedNamespace && !isRegistration;
            })
            .Select(t => $"{t.FullName} là public — chỉ Contracts/Api/*Module được public")
            .ToList();
    }

    private static string? ModuleOf(Type type, string modulesPrefix)
    {
        string rest = type.Namespace![modulesPrefix.Length..];
        int dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var direct = new List<Type?> { type.BaseType };
        direct.AddRange(type.GetInterfaces());

        foreach (FieldInfo field in type.GetFields(AllMembers))
        {
            direct.Add(field.FieldType);
        }

        foreach (PropertyInfo property in type.GetProperties(AllMembers))
        {
            direct.Add(property.PropertyType);
        }

        foreach (MethodInfo method in type.GetMethods(AllMembers))
        {
            direct.Add(method.ReturnType);
            direct.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }

        foreach (ConstructorInfo ctor in type.GetConstructors(AllMembers))
        {
            direct.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
        }

        return direct.Where(t => t is not null).SelectMany(t => Expand(t!)).Distinct();
    }

    /// <summary>Mở generic, mảng, by-ref: List&lt;Product&gt; → List, Product.</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            foreach (Type t in Expand(type.GetElementType()!))
            {
                yield return t;
            }

            yield break;
        }

        yield return type.IsGenericType ? type.GetGenericTypeDefinition() : type;

        if (type.IsGenericType)
        {
            foreach (Type arg in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return arg;
            }
        }
    }

    private static bool IsCompilerGenerated(Type type)
        => type.Name.Contains('<') || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute));

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
