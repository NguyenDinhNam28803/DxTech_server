namespace server.Modules.Inventory.Domain;

/// <summary>
/// Trạng thái một key (phase3-schema.md mục 3.7). Lưu DB: available | reserved | delivered | revoked.
/// available → reserved (tạo đơn) → delivered (tiền về); reserved → available (đơn huỷ/hết hạn); bất kỳ → revoked.
/// </summary>
internal enum LicenseKeyStatus
{
    Available,
    Reserved,
    Delivered,
    Revoked,
}

/// <summary>Nguồn nhập lô key. Lưu DB: cli | admin.</summary>
internal enum ImportSource
{
    /// <summary>Lệnh <c>--import-keys</c> trên VPS (PR-11) — không có tài khoản người nhập.</summary>
    Cli,
    /// <summary>Màn hình admin nhập CSV (Phase 4).</summary>
    Admin,
}
