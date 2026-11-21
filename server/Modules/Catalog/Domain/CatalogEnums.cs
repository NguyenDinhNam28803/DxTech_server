namespace server.Modules.Catalog.Domain;

/// <summary>Loại giấy phép của sản phẩm — dùng để lọc trên trang danh mục. Lưu DB: perpetual | subscription | service.</summary>
internal enum LicenseType
{
    Perpetual,
    Subscription,
    Service,
}

/// <summary>
/// Cách giao hàng của một gói — quyết định luồng sau thanh toán ở Phase 3 (cấp key tự động, cấp tài khoản,
/// hay nhân viên xử lý). Lưu DB: license_key | account | manual.
/// </summary>
internal enum DeliveryType
{
    LicenseKey,
    Account,
    Manual,
}

/// <summary>Hệ điều hành/nền tảng hợp lệ trong <c>products.platforms</c> (CHECK trong DB dùng đúng danh sách này).</summary>
internal static class Platforms
{
    public const string Windows = "windows";
    public const string MacOs = "macos";
    public const string Linux = "linux";
    public const string Ios = "ios";
    public const string Android = "android";
    public const string Web = "web";

    public static readonly IReadOnlyList<string> All = [Windows, MacOs, Linux, Ios, Android, Web];
}
