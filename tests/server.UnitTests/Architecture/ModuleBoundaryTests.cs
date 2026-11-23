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
