using System.ComponentModel.DataAnnotations;

namespace server.Modules.Content.Features;

/// <summary>Cấu hình module Content (<c>Content:*</c>).</summary>
public sealed class ContentOptions
{
    public const string SectionName = "Content";

    /// <summary>Địa chỉ web công khai — dùng cho sitemap (URL tuyệt đối).</summary>
    [Required, Url]
    public string SiteUrl { get; set; } = "https://dxtechs.com";

    /// <summary>Tiền tố URL ảnh trong thư viện (Nginx phục vụ thư mục media, ADR-015 §5).</summary>
    [Required]
    public string MediaBaseUrl { get; set; } = "/media/";

    public LeadOptions Leads { get; set; } = new();
}

public sealed class LeadOptions
{
    /// <summary>
    /// Phiên bản văn bản đồng ý xử lý dữ liệu (NĐ 13/2023) mà form đang hiển thị — lưu cùng mỗi lead.
    /// Trống ⇒ chưa nhận đăng ký (422 <c>content.leads_unavailable</c>) cho tới khi pháp chế duyệt văn bản.
    /// </summary>
    [MaxLength(40)]
    public string? ConsentVersion { get; set; }

    /// <summary>Gửi sớm hơn số giây này sau khi mở form ⇒ coi là bot (lọc rẻ, không thay rate limit).</summary>
    [Range(0, 60)]
    public int MinFillSeconds { get; set; } = 3;
}
