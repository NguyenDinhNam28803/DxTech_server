namespace server.Modules.Catalog.Contracts;

/// <summary>Tra gói theo SKU cho việc quản trị (nhập key) — gồm cả gói đang tắt, trừ gói đã xoá mềm.</summary>
public interface IVariantDirectory
{
    Task<VariantRef?> FindBySkuAsync(string sku, CancellationToken ct);
}

/// <param name="DeliveryType">license_key | account | manual.</param>
public sealed record VariantRef(Guid Id, string Sku, string Name, string DeliveryType, bool IsActive);
