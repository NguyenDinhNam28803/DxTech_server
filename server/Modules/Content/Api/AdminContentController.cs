using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using server.Infrastructure.Http;
using server.Modules.Content.Features.Admin;
using server.Modules.Identity.Contracts;
using server.Shared.Results;

namespace server.Modules.Content.Api;

/// <summary>
/// API quản trị nội dung (ADR-015 §4, CT-3) — policy <c>content</c>: editor hoặc admin đang hoạt động (kiểm lại DB mỗi request).
/// Chống ghi đè: GET trả header <c>ETag</c> = version; mọi thao tác sửa bài/chủ đề bắt buộc <c>If-Match</c> với giá trị đó
/// (thiếu ⇒ 400 <c>content.if_match_required</c>, khác ⇒ 409 <c>content.edit_conflict</c>).
/// </summary>
[ApiController]
[Authorize(Policy = Policies.Content)]
[Route("api/admin/content")]
public sealed class AdminContentController(AdminPostsHandler posts, AdminContentHandler content, AdminMediaHandler media) : ControllerBase
{
    private Guid UserId => CurrentUser.IdOf(User) ?? throw new InvalidOperationException("Policy content bảo đảm có người dùng.");

    // ---------- Bài ----------

    [HttpGet("posts")]
    [ProducesResponseType(typeof(AdminPostPage), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPosts([FromQuery] AdminListPostsQuery query, CancellationToken ct)
        => this.ToActionResult(await posts.ListAsync(query, ct));

    [HttpGet("posts/{id:guid}")]
    [ProducesResponseType(typeof(AdminPostView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPost(Guid id, CancellationToken ct) => WithETag(await posts.GetAsync(id, ct));

    [HttpPost("posts")]
    [ProducesResponseType(typeof(AdminPostView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePost([FromBody] CreatePostCommand command, CancellationToken ct)
    {
        Result<AdminPostView> r = await posts.CreateAsync(command, UserId, ct);
        if (r.IsFailure)
        {
            return this.ToProblem(r.Error);
        }

        Response.Headers.ETag = AdminPostsHandler.ETag(r.Value.Version);
        return Created($"/api/admin/content/posts/{r.Value.Id}", r.Value);
    }

    [HttpPut("posts/{id:guid}")]
    [ProducesResponseType(typeof(AdminPostView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdatePost(Guid id, [FromBody] PostInput input, CancellationToken ct)
        => Versioned(v => posts.UpdateAsync(id, v, input, UserId, ct));

    [HttpPost("posts/{id:guid}/publish")]
    public Task<IActionResult> Publish(Guid id, [FromBody] PublishCommand? command, CancellationToken ct)
        => Versioned(v => posts.PublishAsync(id, v, command ?? new PublishCommand(null), UserId, ct));

    [HttpPost("posts/{id:guid}/unpublish")]
    public Task<IActionResult> Unpublish(Guid id, CancellationToken ct)
        => Versioned(v => posts.UnpublishAsync(id, v, archive: false, UserId, ct));

    [HttpPost("posts/{id:guid}/archive")]
    public Task<IActionResult> Archive(Guid id, CancellationToken ct)
        => Versioned(v => posts.UnpublishAsync(id, v, archive: true, UserId, ct));

    [HttpPut("posts/{id:guid}/slug")]
    public Task<IActionResult> ChangeSlug(Guid id, [FromBody] ChangeSlugCommand command, CancellationToken ct)
        => Versioned(v => posts.ChangeSlugAsync(id, v, command, UserId, ct));

    [HttpGet("posts/{id:guid}/revisions")]
    [ProducesResponseType(typeof(IReadOnlyList<RevisionRow>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Revisions(Guid id, CancellationToken ct) => this.ToActionResult(await posts.RevisionsAsync(id, ct));

    [HttpPost("posts/{id:guid}/revisions/{revisionId:long}/restore")]
    public Task<IActionResult> Restore(Guid id, long revisionId, CancellationToken ct)
        => Versioned(v => posts.RestoreAsync(id, revisionId, v, UserId, ct));

    [HttpDelete("posts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePost(Guid id, CancellationToken ct)
    {
        if (!AdminPostsHandler.TryParseETag(Request.Headers.IfMatch.ToString(), out uint v))
        {
            return IfMatchRequired();
        }

        Result r = await posts.DeleteAsync(id, v, UserId, ct);
        return r.IsSuccess ? NoContent() : this.ToProblem(r.Error);
    }

    // ---------- Chủ đề ----------

    [HttpGet("topics")]
    public async Task<IActionResult> Topics([FromQuery] string? type, CancellationToken ct) => this.ToActionResult(await content.TopicsAsync(type, ct));

    [HttpPost("topics")]
    public async Task<IActionResult> CreateTopic([FromBody] TopicInput input, CancellationToken ct)
    {
        Result<AdminTopicView> r = await content.CreateTopicAsync(input, ct);
        return r.IsSuccess ? Created($"/api/admin/content/topics/{r.Value.Id}", r.Value) : this.ToProblem(r.Error);
    }

    [HttpPut("topics/{id:guid}")]
    public async Task<IActionResult> UpdateTopic(Guid id, [FromBody] TopicInput input, CancellationToken ct)
        => AdminPostsHandler.TryParseETag(Request.Headers.IfMatch.ToString(), out uint v)
            ? this.ToActionResult(await content.UpdateTopicAsync(id, v, input, ct))
            : IfMatchRequired();

    [HttpDelete("topics/{id:guid}")]
    public async Task<IActionResult> DeleteTopic(Guid id, CancellationToken ct)
    {
        Result r = await content.DeleteTopicAsync(id, ct);
        return r.IsSuccess ? NoContent() : this.ToProblem(r.Error);
    }

    // ---------- Lịch khai giảng ----------

    [HttpGet("courses/{postId:guid}/sessions")]
    public async Task<IActionResult> Sessions(Guid postId, CancellationToken ct) => this.ToActionResult(await content.SessionsAsync(postId, ct));

    [HttpPost("courses/{postId:guid}/sessions")]
    public async Task<IActionResult> CreateSession(Guid postId, [FromBody] SessionInput input, CancellationToken ct)
    {
        Result<AdminSessionView> r = await content.CreateSessionAsync(postId, input, ct);
        return r.IsSuccess ? Created($"/api/admin/content/sessions/{r.Value.Id}", r.Value) : this.ToProblem(r.Error);
    }

    [HttpPut("sessions/{id:guid}")]
    public async Task<IActionResult> UpdateSession(Guid id, [FromBody] SessionInput input, CancellationToken ct)
        => this.ToActionResult(await content.UpdateSessionAsync(id, input, ct));

    // ---------- Lead ----------

    [HttpGet("leads")]
    public async Task<IActionResult> Leads([FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int limit = 30, CancellationToken ct = default)
        => this.ToActionResult(await content.LeadsAsync(status, cursor, limit, ct));

    [HttpPut("leads/{id:guid}")]
    public async Task<IActionResult> UpdateLead(Guid id, [FromBody] LeadUpdate input, CancellationToken ct)
        => this.ToActionResult(await content.UpdateLeadAsync(id, input, UserId, ct));

    // ---------- Ảnh ----------

    /// <summary>Tải ảnh (multipart: <c>file</c>, <c>alt</c> tuỳ chọn). 201 ảnh mới · 200 ảnh đã có (trùng nội dung) · 400 sai loại/quá lớn.</summary>
    [HttpPost("media")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    [ProducesResponseType(typeof(MediaView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(MediaView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(IFormFile? file, [FromForm] string? alt, CancellationToken ct)
    {
        if (file is null)
        {
            return this.ToProblem(Error.Validation("content.invalid_media", "Thiếu file ảnh (trường file)."));
        }

        await using Stream stream = file.OpenReadStream();
        Result<MediaView> r = await media.UploadAsync(stream, file.Length, alt, UserId, ct);
        if (r.IsFailure)
        {
            return this.ToProblem(r.Error);
        }

        return r.Value.Duplicate ? Ok(r.Value) : Created(r.Value.Url, r.Value);
    }

    [HttpGet("media")]
    [ProducesResponseType(typeof(MediaPage), StatusCodes.Status200OK)]
    public async Task<IActionResult> Media([FromQuery] string? cursor, [FromQuery] int limit = 40, CancellationToken ct = default)
        => this.ToActionResult(await media.ListAsync(cursor, limit, ct));

    [HttpDelete("media/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMedia(Guid id, CancellationToken ct)
    {
        Result r = await media.DeleteAsync(id, ct);
        return r.IsSuccess ? NoContent() : this.ToProblem(r.Error);
    }

    // ---------- Hỗ trợ ----------

    private async Task<IActionResult> Versioned(Func<uint, Task<Result<AdminPostView>>> action)
        => AdminPostsHandler.TryParseETag(Request.Headers.IfMatch.ToString(), out uint v) ? WithETag(await action(v)) : IfMatchRequired();

    private IActionResult WithETag(Result<AdminPostView> r)
    {
        if (r.IsSuccess)
        {
            Response.Headers.ETag = AdminPostsHandler.ETag(r.Value.Version);
        }

        return this.ToActionResult(r);
    }

    private IActionResult IfMatchRequired() => this.ToProblem(Error.Validation("content.if_match_required",
        "Thiếu header If-Match (giá trị ETag/version của lần đọc gần nhất) — cần để không ghi đè sửa đổi của người khác."));
}
