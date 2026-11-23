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

namespace ArchFixtures.Modules.Catalog.Persistence
{
    public sealed class ProductConfiguration; // vi phạm: Persistence không được public
}

namespace ArchFixtures.Modules.Catalog.Features
{
    public sealed record ProductListItem(Guid Id); // được phép public: controller của chính module dùng
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

    internal sealed class FeatureLeakOrderService
    {
        // vi phạm: Features của Catalog public nhưng vẫn là nội bộ module — module khác không được dùng
        public ArchFixtures.Modules.Catalog.Features.ProductListItem? Load() => null;
    }

    internal sealed class GoodOrderService
    {
        // đúng: chỉ dùng Contracts của Catalog
        public ArchFixtures.Modules.Catalog.Contracts.ProductDto? Load() => null;
    }
}
