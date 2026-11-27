using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using server.Infrastructure.Persistence;
using server.Modules.Content.Domain;
using server.Shared.Results;

namespace server.Modules.Content.Features.Public;

/// <summary>
/// Body <c>POST /api/content/leads</c>. <see cref="Website"/> là trường bẫy (ẩn với người thật); <see cref="ElapsedMs"/> = thời gian từ lúc
/// mở form tới lúc gửi do web đo — bot đơn giản gửi ngay. Cả hai chỉ lọc rẻ; chặn thật là rate limit 5/giờ/IP.
/// </summary>
public sealed record SubmitLeadCommand(
    string? Kind, string? FullName, string? Email, string? Phone, string? Company, string? Message,
    string? PostSlug, Guid? SessionId, bool Consent, string? Website, int? ElapsedMs);

public sealed record LeadAccepted(Guid? Id);

/// <summary>
/// Đăng ký tư vấn / khoá học (ADR-015 §6). Lưu phiên bản văn bản đồng ý + thời điểm (NĐ 13/2023). Bot bị lọc nhận 202 như thật
/// (không gợi ý cho bot biết đã bị phát hiện) nhưng không ghi gì. Không gửi email ở đây (Phase 4, khi có nhà cung cấp mail).
/// </summary>
public sealed partial class SubmitLeadHandler
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ContentOptions _options;

    internal SubmitLeadHandler(AppDbContext db, TimeProvider clock, IOptions<ContentOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    [GeneratedRegex(@"^\+?[0-9]{8,15}$")]
    private static partial Regex PhoneDigits();

    public async Task<Result<LeadAccepted>> HandleAsync(SubmitLeadCommand c, IPAddress? ip, string? userAgent, CancellationToken ct)
    {
        string? consentVersion = _options.Leads.ConsentVersion?.Trim();
        if (string.IsNullOrEmpty(consentVersion))
        {
            return Error.BusinessRule("content.leads_unavailable", "Chưa mở nhận đăng ký trực tuyến — vui lòng gọi hotline hoặc gửi email.");
        }

        if (!string.IsNullOrEmpty(c.Website) || c.ElapsedMs is not { } ms || ms < _options.Leads.MinFillSeconds * 1000)
        {
            return new LeadAccepted(null); // bot: giả như đã nhận
        }

        var errors = new Dictionary<string, string[]>();
        LeadKind kind = c.Kind switch
        {
            "consultation" or null => LeadKind.Consultation,
            "course" => LeadKind.Course,
            _ => Invalid(errors, "kind", "consultation hoặc course.", LeadKind.Consultation),
        };

        string fullName = (c.FullName ?? "").Trim();
        if (fullName.Length is < 1 or > 100)
        {
            errors["fullName"] = ["Họ tên 1–100 ký tự."];
        }

        string? email = Clean(c.Email);
        if (email is not null)
        {
            email = email.ToLowerInvariant();
            if (email.Length > 254 || !MailAddress.TryCreate(email, out MailAddress? addr) || addr.Address != email || !addr.Host.Contains('.'))
            {
                errors["email"] = ["Email không hợp lệ."];
            }
        }

        string? phone = Clean(c.Phone);
        if (phone is not null)
        {
            phone = Regex.Replace(phone, @"[\s.\-()]", "");
            if (phone.StartsWith("+84"))
            {
                phone = "0" + phone[3..];
            }

            if (!PhoneDigits().IsMatch(phone))
            {
                errors["phone"] = ["Số điện thoại không hợp lệ."];
            }
        }

        if (email is null && phone is null)
        {
            errors["contact"] = ["Cần email hoặc số điện thoại."];
        }

        string? company = Clean(c.Company);
        if (company is { Length: > 200 })
        {
            errors["company"] = ["Tối đa 200 ký tự."];
        }

        string? message = Clean(c.Message);
        if (message is { Length: > Lead.MessageMaxLength })
        {
            errors["message"] = [$"Tối đa {Lead.MessageMaxLength} ký tự."];
        }

        if (!c.Consent)
        {
            errors["consent"] = ["Cần đồng ý để chúng tôi liên hệ và xử lý thông tin."];
        }

        Guid? postId = null;
        if (!string.IsNullOrWhiteSpace(c.PostSlug))
        {
            DateTimeOffset now = _clock.GetUtcNow();
            PostType wanted = kind == LeadKind.Course ? PostType.Training : PostType.Solution;
            postId = await _db.Set<Post>().AsNoTracking()
                .Where(p => p.Slug == c.PostSlug.Trim() && p.Status == PostStatus.Published && p.PublishedAt <= now
                    && (p.PostType == wanted || (kind == LeadKind.Consultation && p.PostType == PostType.Training)))
                .OrderBy(p => p.PostType == wanted ? 0 : 1)
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (postId is null)
            {
                errors["postSlug"] = ["Không có bài/khoá học này."];
            }
        }
        else if (kind == LeadKind.Course)
        {
            errors["postSlug"] = ["Chọn khoá học."];
        }

        if (c.SessionId is { } sid && postId is { } pid
            && !await _db.Set<CourseSession>().AnyAsync(s => s.Id == sid && s.PostId == pid && s.Status == CourseSessionStatus.Open, ct))
        {
            errors["sessionId"] = ["Lịch khai giảng không còn nhận đăng ký."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_lead", "Thông tin đăng ký chưa hợp lệ.", errors);
        }

        var lead = new Lead(kind, fullName, consentVersion, _clock.GetUtcNow())
        {
            Email = email,
            Phone = phone,
            Company = company,
            Message = message,
            PostId = postId,
            SessionId = c.SessionId,
            SourceIp = ip,
            UserAgent = userAgent is { Length: > 500 } ? userAgent[..500] : userAgent,
        };
        _db.Add(lead);
        await _db.SaveChangesAsync(ct);
        return new LeadAccepted(lead.Id);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T Invalid<T>(Dictionary<string, string[]> errors, string field, string message, T fallback)
    {
        errors[field] = [message];
        return fallback;
    }
}
