using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Conversions;
using server.Modules.Content.Domain;
using server.Modules.Content.Features.Public;
using server.Shared.Results;

namespace server.Modules.Content.Features.Admin;

public sealed record TopicInput(string? Type, string? Slug, string? Name, string? Description, Guid? ParentId, int SortOrder, bool IsActive);

public sealed record AdminTopicView(Guid Id, string Type, Guid? ParentId, string Slug, string Name, string? Description,
    string? ImageUrl, int SortOrder, bool IsActive, uint Version);

public sealed record SessionInput(DateOnly StartsOn, DateOnly? EndsOn, string? Format, string? Location, int? Seats, long? PriceVnd, string? Status);

public sealed record AdminSessionView(Guid Id, Guid PostId, DateOnly StartsOn, DateOnly? EndsOn, string Format, string? Location,
    int? Seats, long? PriceVnd, string Status);

public sealed record LeadUpdate(string? Status, string? Note);

public sealed record AdminLeadView(Guid Id, string Kind, string FullName, string? Email, string? Phone, string? Company, string? Message,
    Guid? PostId, string? PostTitle, Guid? SessionId, string ConsentVersion, DateTimeOffset ConsentAt, string Status, string? Note,
    Guid? HandledBy, DateTimeOffset CreatedAt, bool Anonymized);

public sealed record AdminLeadPage(IReadOnlyList<AdminLeadView> Items, string? NextCursor);

/// <summary>Quản trị chủ đề, lịch khai giảng, lead (CT-3). Chủ đề cũng chống ghi đè bằng xmin (<c>If-Match</c>).</summary>
public sealed class AdminContentHandler
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    internal AdminContentHandler(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ---------- Chủ đề ----------

    public async Task<Result<IReadOnlyList<AdminTopicView>>> TopicsAsync(string? type, CancellationToken ct)
    {
        IQueryable<Topic> q = _db.Set<Topic>().AsNoTracking();
        if (type is not null)
        {
            if (!PublicContentHandler.TryParseType(type, out PostType t))
            {
                return Error.Validation("content.invalid_type", "Loại nội dung không hợp lệ.");
            }

            q = q.Where(x => x.PostType == t);
        }

        return (await q.OrderBy(x => x.PostType).ThenBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct)).Select(View).ToList();
    }

    public async Task<Result<AdminTopicView>> CreateTopicAsync(TopicInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!PublicContentHandler.TryParseType(input.Type, out PostType type))
        {
            errors["type"] = ["Loại nội dung không hợp lệ."];
        }

        string slug = (input.Slug ?? "").Trim();
        if (!AdminPostsHandler.IsSlug(slug))
        {
            errors["slug"] = ["Slug không hợp lệ."];
        }

        var topic = new Topic(type, slug, "x");
        await ApplyTopicAsync(topic, input, errors, ct);
        if (errors.Count == 0 && await _db.Set<Topic>().AnyAsync(t => t.PostType == type && t.ParentId == input.ParentId && t.Slug == slug, ct))
        {
            errors["slug"] = ["Đã có chủ đề cùng cấp dùng slug này."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_topic", "Chủ đề chưa hợp lệ.", errors);
        }

        _db.Add(topic);
        await _db.SaveChangesAsync(ct);
        return View(topic);
    }

    public async Task<Result<AdminTopicView>> UpdateTopicAsync(Guid id, uint expectedVersion, TopicInput input, CancellationToken ct)
    {
        Topic? topic = await _db.Set<Topic>().SingleOrDefaultAsync(t => t.Id == id, ct);
        if (topic is null)
        {
            return Error.NotFound("content.topic_not_found", "Không tìm thấy chủ đề.");
        }

        if (topic.Version != expectedVersion)
        {
            return TopicConflict();
        }

        var errors = new Dictionary<string, string[]>();
        await ApplyTopicAsync(topic, input, errors, ct);
        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_topic", "Chủ đề chưa hợp lệ.", errors);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TopicConflict();
        }

        return View(topic);
    }

    public async Task<Result> DeleteTopicAsync(Guid id, CancellationToken ct)
    {
        Topic? topic = await _db.Set<Topic>().SingleOrDefaultAsync(t => t.Id == id, ct);
        if (topic is null)
        {
            return Error.NotFound("content.topic_not_found", "Không tìm thấy chủ đề.");
        }

        if (await _db.Set<Topic>().AnyAsync(t => t.ParentId == id, ct) || await _db.Set<Post>().AnyAsync(p => p.TopicId == id, ct))
        {
            return Error.Conflict("content.topic_in_use", "Chủ đề còn chủ đề con hoặc bài viết — chuyển chúng đi trước, hoặc tắt chủ đề thay vì xoá.");
        }

        topic.DeletedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task ApplyTopicAsync(Topic topic, TopicInput input, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        string name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > Topic.NameMaxLength)
        {
            errors["name"] = [$"Tên 1–{Topic.NameMaxLength} ký tự."];
        }

        if (input.Description is { Length: > Topic.DescriptionMaxLength })
        {
            errors["description"] = [$"Tối đa {Topic.DescriptionMaxLength} ký tự."];
        }

        if (input.ParentId is { } parentId)
        {
            // Không cho tạo vòng: cha không được là chính nó hoặc con cháu của nó.
            List<Topic> all = await _db.Set<Topic>().AsNoTracking().Where(t => t.PostType == topic.PostType).ToListAsync(ct);
            Topic? parent = all.FirstOrDefault(t => t.Id == parentId);
            Guid? walk = parentId;
            int depth = 0;
            bool cycle = false;
            while (walk is { } w && depth++ < 20)
            {
                if (w == topic.Id)
                {
                    cycle = true;
                    break;
                }

                walk = all.FirstOrDefault(t => t.Id == w)?.ParentId;
            }

            if (parent is null || cycle)
            {
                errors["parentId"] = ["Chủ đề cha không hợp lệ (không tồn tại, khác loại, hoặc tạo vòng)."];
            }
        }

        if (errors.Count > 0)
        {
            return;
        }

        topic.Name = name;
        topic.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        topic.ParentId = input.ParentId;
        topic.SortOrder = input.SortOrder;
        topic.IsActive = input.IsActive;
    }

    private static Error TopicConflict() => Error.Conflict("content.edit_conflict", "Chủ đề đã được người khác sửa — tải lại rồi sửa tiếp.");

    private static AdminTopicView View(Topic t) => new(t.Id, AdminPostsHandler.TypeName(t.PostType), t.ParentId, t.Slug, t.Name,
        t.Description, t.ImageUrl, t.SortOrder, t.IsActive, t.Version);

    // ---------- Lịch khai giảng ----------

    public async Task<Result<IReadOnlyList<AdminSessionView>>> SessionsAsync(Guid postId, CancellationToken ct)
    {
        if (!await _db.Set<Post>().AnyAsync(p => p.Id == postId && p.PostType == PostType.Training, ct))
        {
            return Error.NotFound("content.course_not_found", "Không tìm thấy khoá học.");
        }

        return (await _db.Set<CourseSession>().AsNoTracking().Where(s => s.PostId == postId).OrderByDescending(s => s.StartsOn).ToListAsync(ct))
            .Select(View).ToList();
    }

    public async Task<Result<AdminSessionView>> CreateSessionAsync(Guid postId, SessionInput input, CancellationToken ct)
    {
        if (!await _db.Set<Post>().AnyAsync(p => p.Id == postId && p.PostType == PostType.Training, ct))
        {
            return Error.NotFound("content.course_not_found", "Không tìm thấy khoá học.");
        }

        var session = new CourseSession(postId, input.StartsOn, CourseFormat.Online);
        if (ApplySession(session, input) is { } error)
        {
            return error;
        }

        _db.Add(session);
        await _db.SaveChangesAsync(ct);
        return View(session);
    }

    public async Task<Result<AdminSessionView>> UpdateSessionAsync(Guid id, SessionInput input, CancellationToken ct)
    {
        CourseSession? session = await _db.Set<CourseSession>().SingleOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound("content.session_not_found", "Không tìm thấy lịch khai giảng.");
        }

        if (ApplySession(session, input) is { } error)
        {
            return error;
        }

        await _db.SaveChangesAsync(ct);
        return View(session);
    }

    private static Error? ApplySession(CourseSession s, SessionInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!SnakeCaseEnumConverter<CourseFormat>.DbValues.Contains(input.Format ?? ""))
        {
            errors["format"] = ["online, offline hoặc hybrid."];
        }

        if (!SnakeCaseEnumConverter<CourseSessionStatus>.DbValues.Contains(input.Status ?? "open"))
        {
            errors["status"] = ["open, full hoặc closed."];
        }

        if (input.EndsOn is { } end && end < input.StartsOn)
        {
            errors["endsOn"] = ["Ngày kết thúc trước ngày khai giảng."];
        }

        if (input.Seats is <= 0)
        {
            errors["seats"] = ["Số chỗ phải lớn hơn 0."];
        }

        if (input.PriceVnd is < 0)
        {
            errors["priceVnd"] = ["Học phí không âm."];
        }

        if (input.Location is { Length: > 200 })
        {
            errors["location"] = ["Tối đa 200 ký tự."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("content.invalid_session", "Lịch khai giảng chưa hợp lệ.", errors);
        }

        s.StartsOn = input.StartsOn;
        s.EndsOn = input.EndsOn;
        s.Format = SnakeCaseEnumConverter<CourseFormat>.FromProvider(input.Format!);
        s.Status = SnakeCaseEnumConverter<CourseSessionStatus>.FromProvider(input.Status ?? "open");
        s.Location = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim();
        s.Seats = input.Seats;
        s.PriceVnd = input.PriceVnd;
        return null;
    }

    private static AdminSessionView View(CourseSession s) => new(s.Id, s.PostId, s.StartsOn, s.EndsOn,
        SnakeCaseEnumConverter<CourseFormat>.ToProvider(s.Format), s.Location, s.Seats, s.PriceVnd,
        SnakeCaseEnumConverter<CourseSessionStatus>.ToProvider(s.Status));

    // ---------- Lead ----------

    public async Task<Result<AdminLeadPage>> LeadsAsync(string? status, string? cursor, int limit, CancellationToken ct)
    {
        IQueryable<Lead> q = _db.Set<Lead>().AsNoTracking();
        if (status is not null)
        {
            if (!SnakeCaseEnumConverter<LeadStatus>.DbValues.Contains(status))
            {
                return Error.Validation("content.invalid_status", "Trạng thái: new, contacted hoặc closed.");
            }

            LeadStatus s = SnakeCaseEnumConverter<LeadStatus>.FromProvider(status);
            q = q.Where(l => l.Status == s);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!PublicContentHandler.TryDecodeCursor(cursor, out var c))
            {
                return Error.Validation("content.invalid_query", "Tham số cursor không hợp lệ.");
            }

            q = q.Where(l => l.CreatedAt < c.At || (l.CreatedAt == c.At && l.Id.CompareTo(c.Id) < 0));
        }

        limit = Math.Clamp(limit, 1, 100);
        var rows = await q.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Take(limit + 1)
            .Select(l => new { Lead = l, PostTitle = _db.Set<Post>().Where(p => p.Id == l.PostId).Select(p => p.Title).FirstOrDefault() })
            .ToListAsync(ct);
        return new AdminLeadPage(
            rows.Take(limit).Select(r => View(r.Lead, r.PostTitle)).ToList(),
            rows.Count > limit ? PublicContentHandler.EncodeCursor(rows[limit - 1].Lead.CreatedAt, rows[limit - 1].Lead.Id) : null);
    }

    public async Task<Result<AdminLeadView>> UpdateLeadAsync(Guid id, LeadUpdate input, Guid userId, CancellationToken ct)
    {
        Lead? lead = await _db.Set<Lead>().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (lead is null)
        {
            return Error.NotFound("content.lead_not_found", "Không tìm thấy đăng ký.");
        }

        if (input.Status is not null && !SnakeCaseEnumConverter<LeadStatus>.DbValues.Contains(input.Status))
        {
            return Error.Validation("content.invalid_status", "Trạng thái: new, contacted hoặc closed.");
        }

        if (input.Note is { Length: > 2000 })
        {
            return Error.Validation("content.invalid_note", "Ghi chú tối đa 2000 ký tự.");
        }

        if (input.Status is not null)
        {
            lead.Status = SnakeCaseEnumConverter<LeadStatus>.FromProvider(input.Status);
        }

        lead.Note = string.IsNullOrWhiteSpace(input.Note) ? lead.Note : input.Note.Trim();
        lead.HandledBy = userId;
        await _db.SaveChangesAsync(ct);
        return View(lead, null);
    }

    private static AdminLeadView View(Lead l, string? postTitle) => new(l.Id, SnakeCaseEnumConverter<LeadKind>.ToProvider(l.Kind),
        l.FullName, l.Email, l.Phone, l.Company, l.Message, l.PostId, postTitle, l.SessionId, l.ConsentVersion, l.ConsentAt,
        SnakeCaseEnumConverter<LeadStatus>.ToProvider(l.Status), l.Note, l.HandledBy, l.CreatedAt, l.AnonymizedAt is not null);
}
