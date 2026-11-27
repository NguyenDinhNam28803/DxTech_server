namespace server.Modules.Sales.Features.Orders;

/// <summary>Giới hạn chống giữ kho (phase3-schema.md mục 4). Cấu hình <c>Sales:*</c>.</summary>
internal sealed class SalesOptions
{
    public const string SectionName = "Sales";

    /// <summary>Số đơn đang chờ thanh toán tối đa của một tài khoản.</summary>
    public int MaxPendingOrdersPerUser { get; set; } = 3;

    /// <summary>Số đơn tạo tối đa trong 24 giờ của một tài khoản.</summary>
    public int MaxOrdersPerUserPerDay { get; set; } = 20;
}
