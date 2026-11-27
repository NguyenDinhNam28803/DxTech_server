using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using server.Infrastructure.Persistence;
using server.Modules.Content.Domain;
using server.Modules.Content.Features.Public;
using server.Shared.Results;

namespace server.Modules.Content.Features.Admin;

public sealed record MediaView(Guid Id, string Url, string ContentType, long Bytes, int Width, int Height, string? AltDefault,
    DateTimeOffset CreatedAt, bool Duplicate, int MetadataBytesRemoved);

public sealed record MediaPage(IReadOnlyList<MediaView> Items, string? NextCursor);

/// <summary>
/// Thư viện ảnh (ADR-015 §5, CT-3b). Tải lên: ≤ 5 MB, JPEG/PNG/WebP theo magic bytes, ≤ 6000×6000, bỏ metadata (EXIF/GPS…).
/// Cùng nội dung (SHA-256 sau khi làm sạch) ⇒ trả ảnh đã có thay vì lưu bản thứ hai; ảnh đã xoá mềm ⇒ khôi phục.
/// File ghi vào thư mục tạm cùng ổ rồi đổi tên một lần (không bao giờ có file viết dở dưới tên thật); lưu DB lỗi ⇒ xoá file.
/// Xoá: xoá mềm, file giữ trên đĩa (khôi phục được) — chặn khi còn bài dùng ảnh (ảnh bìa hoặc khối ảnh).
/// </summary>
public sealed class AdminMediaHandler
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ContentOptions _options;

    internal AdminMediaHandler(AppDbContext db, TimeProvider clock, IOptions<ContentOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<Result<MediaView>> UploadAsync(Stream content, long? declaredLength, string? alt, Guid userId, CancellationToken ct)
    {
        if (declaredLength is > MediaFile.MaxBytes)
        {
            return TooLarge();
        }

        alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
        if (alt is { Length: > 200 })
        {
            return Error.Validation("content.invalid_media", "Mô tả ảnh tối đa 200 ký tự.");
        }

        // Đọc tối đa MaxBytes + 1: vượt ⇒ từ chối mà không giữ cả file lớn trong bộ nhớ.
        byte[] raw;
        using (var buffer = new MemoryStream())
        {
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MediaFile.MaxBytes)
                {
                    return TooLarge();
                }
            }

            raw = buffer.ToArray();
        }

        (SanitizedImage? image, string? error) = ImageSanitizer.Sanitize(raw);
        if (image is null)
        {
            return Error.Validation("content.invalid_media", error!);
        }

        if (image.Width > MediaFile.MaxDimension || image.Height > MediaFile.MaxDimension)
        {
            return Error.Validation("content.invalid_media", $"Ảnh tối đa {MediaFile.MaxDimension}×{MediaFile.MaxDimension} px (ảnh này {image.Width}×{image.Height}).");
        }

        byte[] sha = SHA256.HashData(image.Bytes);
        MediaFile? existing = await _db.Set<MediaFile>().IgnoreQueryFilters().SingleOrDefaultAsync(m => m.Sha256 == sha, ct);
        if (existing is not null)
        {
            if (existing.DeletedAt is not null)
            {
                existing.DeletedAt = null;
                await _db.SaveChangesAsync(ct);
            }

            return View(existing, duplicate: true, image.RemovedBytes);
        }

        var media = new MediaFile(image.ContentType, image.Extension, image.Bytes.Length, image.Width, image.Height, sha, userId, _clock.GetUtcNow())
        {
            AltDefault = alt,
        };

        string target = Path.Combine(_options.MediaRoot, media.RelativePath);
        string? dir = Path.GetDirectoryName(target);
        string temp = Path.Combine(_options.MediaRoot, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dir!);
            await File.WriteAllBytesAsync(temp, image.Bytes, ct);
            File.Move(temp, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return Error.BusinessRule("content.media_unavailable", "Chưa lưu được ảnh lên máy chủ (thư mục ảnh chưa sẵn sàng) — báo quản trị hệ thống.");
        }

        _db.Add(media);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Hai người tải cùng một ảnh cùng lúc: bản kia đã vào DB ⇒ bỏ file của mình, trả bản đó.
            TryDelete(target);
            _db.ChangeTracker.Clear();
            MediaFile other = await _db.Set<MediaFile>().IgnoreQueryFilters().SingleAsync(m => m.Sha256 == sha, ct);
            return View(other, duplicate: true, image.RemovedBytes);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return View(media, duplicate: false, image.RemovedBytes);
    }

    public async Task<Result<MediaPage>> ListAsync(string? cursor, int limit, CancellationToken ct)
    {
        IQueryable<MediaFile> q = _db.Set<MediaFile>().AsNoTracking();
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!PublicContentHandler.TryDecodeCursor(cursor, out var c))
            {
                return Error.Validation("content.invalid_query", "Tham số cursor không hợp lệ.");
            }

            q = q.Where(m => m.CreatedAt < c.At || (m.CreatedAt == c.At && m.Id.CompareTo(c.Id) < 0));
        }

        limit = Math.Clamp(limit, 1, 100);
        List<MediaFile> rows = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit + 1).ToListAsync(ct);
        return new MediaPage(rows.Take(limit).Select(m => View(m, false, 0)).ToList(),
            rows.Count > limit ? PublicContentHandler.EncodeCursor(rows[limit - 1].CreatedAt, rows[limit - 1].Id) : null);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        MediaFile? media = await _db.Set<MediaFile>().SingleOrDefaultAsync(m => m.Id == id, ct);
        if (media is null)
        {
            return Error.NotFound("content.media_not_found", "Không tìm thấy ảnh.");
        }

        // Ảnh bìa hoặc khối ảnh (mediaId trong jsonb) — tính cả bài nháp/đã xoá mềm để khôi phục bài không ra ảnh vỡ.
        bool used = await _db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
              SELECT 1 FROM content.posts
               WHERE cover_media_id = {id}
                  OR jsonb_path_exists(blocks, '$[*] ? (@.mediaId == $id)', jsonb_build_object('id', {id.ToString()}))
            ) AS "Value"
            """).SingleAsync(ct);
        if (used)
        {
            return Error.Conflict("content.media_in_use", "Ảnh đang được bài viết dùng — gỡ khỏi bài trước khi xoá.");
        }

        media.DeletedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private MediaView View(MediaFile m, bool duplicate, int removed)
    {
        string baseUrl = _options.MediaBaseUrl.EndsWith('/') ? _options.MediaBaseUrl : _options.MediaBaseUrl + "/";
        return new MediaView(m.Id, baseUrl + m.RelativePath, m.ContentType, m.Bytes, m.Width, m.Height, m.AltDefault, m.CreatedAt, duplicate, removed);
    }

    private static Error TooLarge() => Error.Validation("content.invalid_media", $"Ảnh tối đa {MediaFile.MaxBytes / 1024 / 1024} MB.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bỏ qua: file mồ côi không ảnh hưởng hiển thị (không có dòng DB trỏ tới).
        }
    }
}
