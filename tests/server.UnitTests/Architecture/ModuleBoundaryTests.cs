using System.Reflection;
using server.Infrastructure.Persistence;

namespace server.UnitTests.Architecture;

/// <summary>Quy tắc kiến trúc của ADR-001 áp lên assembly <c>server</c>.</summary>
public class ModuleBoundaryTests
{
    private static readonly Assembly ServerAssembly = typeof(AppDbContext).Assembly;

    [Fact]
    public void Modules_only_use_other_modules_through_contracts()
    {
        Assert.Empty(ArchitectureRules.CrossModuleViolations(ServerAssembly, "server"));
    }

    [Fact]
    public void Shared_kernel_does_not_depend_on_modules_or_infrastructure()
    {
        Assert.Empty(ArchitectureRules.NamespaceDependencyViolations(ServerAssembly, "server.Shared", "server.Modules"));
        Assert.Empty(ArchitectureRules.NamespaceDependencyViolations(ServerAssembly, "server.Shared", "server.Infrastructure"));
    }

    [Fact]
    public void Module_internals_are_not_public()
    {
        Assert.Empty(ArchitectureRules.PublicSurfaceViolations(ServerAssembly, "server"));
    }

    /// <summary>
    /// ADR-012 §5: mọi controller phải ghi rõ [Authorize] hoặc [AllowAnonymous] (ở class hoặc từng action public).
    /// Mặc định đã yêu cầu đăng nhập (AuthorizeFilter toàn cục) — quy tắc này bắt người viết quyết định có chủ đích.
    /// </summary>
    [Fact]
    public void Every_controller_action_declares_its_authorization()
    {
        static bool HasAuth(MemberInfo m) => m.GetCustomAttributes(inherit: true)
            .Any(a => a.GetType().Name is "AuthorizeAttribute" or "AllowAnonymousAttribute");

        static bool IsController(Type t)
        {
            for (Type? b = t.BaseType; b is not null; b = b.BaseType)
            {
                if (b.Name == "ControllerBase") return true;
            }

            return false;
        }

        List<string> missing = ServerAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && IsController(t) && !HasAuth(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && !HasAuth(m))
                .Select(m => $"{t.FullName}.{m.Name}"))
            .ToList();

        Assert.Empty(missing);
        Assert.Contains(ServerAssembly.GetTypes(), t => t.Name == "AuthController"); // quy tắc thực sự quét được controller
    }
}

/// <summary>
/// Chứng minh bộ kiểm tra thực sự bắt được vi phạm — chạy trên các type mẫu đặt trong
/// namespace <c>ArchFixtures.Modules.*</c> của chính assembly test.
/// </summary>
public class ArchitectureRulesSelfTests
{
    private static readonly Assembly TestAssembly = typeof(ArchitectureRulesSelfTests).Assembly;

    [Fact]
    public void Detects_reference_to_another_modules_internal_type()
    {
        IReadOnlyList<string> violations = ArchitectureRules.CrossModuleViolations(TestAssembly, "ArchFixtures");

        Assert.Contains(violations, v => v.Contains("BadOrderService") && v.Contains("Catalog.Domain.Product"));
        Assert.Contains(violations, v => v.Contains("FeatureLeakOrderService") && v.Contains("Catalog.Features.ProductListItem"));
        Assert.DoesNotContain(violations, v => v.Contains("GoodOrderService"));
    }

    [Fact]
    public void Detects_public_types_outside_contracts_and_api()
    {
        IReadOnlyList<string> violations = ArchitectureRules.PublicSurfaceViolations(TestAssembly, "ArchFixtures");

        Assert.Contains(violations, v => v.Contains("ArchFixtures.Modules.Catalog.Domain.Product"));
        Assert.Contains(violations, v => v.Contains("ArchFixtures.Modules.Catalog.Persistence.ProductConfiguration"));
        Assert.DoesNotContain(violations, v => v.Contains("Catalog.Features.ProductListItem"));
        Assert.DoesNotContain(violations, v => v.Contains("Contracts.ProductDto"));
        Assert.DoesNotContain(violations, v => v.Contains("CatalogModule"));
    }
}
