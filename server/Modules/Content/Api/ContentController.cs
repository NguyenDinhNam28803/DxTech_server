using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Content.Features.Public;
using server.Shared.Results;

namespace server.Modules.Content.Api;

/// <summary>
/// API nội dung công khai (ADR-015, CT-2). Chỉ bài đã đăng; GET cho cache 60 giây (trình duyệt/Nginx) — biên tập viên đăng bài
/// thấy trên web sau tối đa một phút. Lead: rate limit 5/giờ/IP (nhóm "leads").
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/content")]
public sealed class ContentController(PublicContentHandler content, SubmitLeadHandler leads, SitemapHandler sitemap) : ControllerBase
{
    private const string PublicCache = "public, max-age=60";

    /// <summary>Cây chủ đề của một loại (menu Giải pháp, trang Đào tạo, Tài nguyên) kèm các trang cấp 1 của từng chủ đề.</summary>
    [HttpGet("topics")]
    [ProducesResponseType(typeof(IReadOnlyList<TopicNode>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Topics([FromQuery] string? type, CancellationToken ct)
        => Cached(await content.TopicTreeAsync(type, ct));

    /// <summary>Danh sách đã đăng: lọc loại, chủ đề (gồm chủ đề con), tìm không dấu <c>q</c>; mới nhất trước + cursor, hoặc <c>sort=order</c>.</summary>
    [HttpGet("posts")]
    [ProducesResponseType(typeof(PostPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Posts([FromQuery] ListPostsQuery query, CancellationToken ct)
        => Cached(await content.ListAsync(query, ct));

    /// <summary>Chi tiết bài. Slug cũ ⇒ 301 tới slug mới. Bản nháp, hẹn giờ chưa tới, đã xoá ⇒ 404.</summary>
    [HttpGet("posts/{type}/{slug}")]
    [ProducesResponseType(typeof(PostDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status301MovedPermanently)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Post(string type, string slug, CancellationToken ct)
    {
        Result<PostLookup> result = await content.GetAsync(type, slug, ct);
        if (result.IsSuccess && result.Value.MovedToSlug is { } moved)
        {
            Response.Headers.CacheControl = PublicCache;
            return RedirectPermanent($"/api/content/posts/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(moved)}");
        }

        return Cached(result.IsSuccess ? Result<PostDetail>.Success(result.Value.Post!) : Result<PostDetail>.Failure(result.Error));
    }

    /// <summary>Bài nổi bật của một loại (thẻ trang chủ — thay link gắn cứng).</summary>
    [HttpGet("featured")]
    [ProducesResponseType(typeof(IReadOnlyList<PostSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Featured([FromQuery] string? type, [FromQuery] int limit = 6, CancellationToken ct = default)
        => Cached(await content.FeaturedAsync(type, limit, ct));

    /// <summary>Lịch khai giảng còn mở/đã đủ chỗ của một khoá đào tạo, từ hôm nay (giờ Việt Nam).</summary>
    [HttpGet("courses/{slug}/sessions")]
    [ProducesResponseType(typeof(IReadOnlyList<CourseSessionView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sessions(string slug, CancellationToken ct)
        => Cached(await content.SessionsAsync(slug, ct));

    /// <summary>Đăng ký tư vấn / khoá học. 202 khi nhận (kể cả khi bị lọc là bot), 400 thiếu/sai thông tin, 422 chưa mở nhận đăng ký.</summary>
    [HttpPost("leads")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Lead([FromBody] SubmitLeadCommand command, CancellationToken ct)
    {
        Result<LeadAccepted> result = await leads.HandleAsync(command, HttpContext.Connection.RemoteIpAddress,
            Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null, ct);
        return result.IsSuccess ? Accepted(new { received = true }) : this.ToProblem(result.Error);
    }

    /// <summary>Sitemap XML (Nginx trỏ <c>dxtechs.com/sitemap.xml</c> về đây).</summary>
    [HttpGet("sitemap.xml")]
    [Produces("application/xml")]
    public async Task<IActionResult> SitemapXml(CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Content(await sitemap.BuildAsync(ct), "application/xml; charset=utf-8");
    }

    private IActionResult Cached<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            Response.Headers.CacheControl = PublicCache;
        }

        return this.ToActionResult(result);
    }
}
