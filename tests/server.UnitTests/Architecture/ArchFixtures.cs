// Các type mẫu CỐ Ý vi phạm/tuân thủ quy tắc module, chỉ để kiểm tra ArchitectureRules.
// Không liên quan tới code thật của server.

namespace ArchFixtures.Modules.Catalog
{
    public static class CatalogModule;
}

namespace ArchFixtures.Modules.Catalog.Domain
{
    public sealed class Product; // vi phạm: Domain không được public
}

namespace ArchFixtures.Modules.Catalog.Contracts
{
    public sealed record ProductDto(Guid Id);
}

namespace ArchFixtures.Modules.Sales.Features
{
    internal sealed class BadOrderService
    {
        // vi phạm: Sales dùng thẳng entity nội bộ của Catalog
        public List<ArchFixtures.Modules.Catalog.Domain.Product> Load() => [];
    }

    internal sealed class GoodOrderService
    {
        // đúng: chỉ dùng Contracts của Catalog
        public ArchFixtures.Modules.Catalog.Contracts.ProductDto? Load() => null;
    }
}
