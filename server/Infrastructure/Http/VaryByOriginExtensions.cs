using Microsoft.Net.Http.Headers;

namespace server.Infrastructure.Http;

/// <summary>
/// Luôn gửi <c>Vary: Origin</c> cho mọi response của API.
///
/// Lý do: khi chỉ có MỘT origin được phép, middleware CORS của ASP.NET Core không thêm <c>Vary: Origin</c>.
/// Response có/không có header <c>Access-Control-Allow-Origin</c> lại phụ thuộc vào header <c>Origin</c>
/// của request, nên mọi cache (trình duyệt, proxy) phải biết tách bản lưu theo Origin — nếu không,
/// một response không có header CORS (ví dụ do mở thẳng URL API) có thể bị dùng lại cho lần gọi từ
/// https://dxtechs.com và trình duyệt báo lỗi CORS.
/// </summary>
public static class VaryByOriginExtensions
{
    public static IApplicationBuilder UseVaryByOrigin(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            // OnStarting chạy ngay trước khi gửi header — kể cả sau khi ExceptionHandler xoá header cũ.
            context.Response.OnStarting(() =>
            {
                bool alreadyVaries = context.Response.Headers.Vary
                    .Any(value => value?.Contains(HeaderNames.Origin, StringComparison.OrdinalIgnoreCase) == true);
                if (!alreadyVaries)
                {
                    context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Origin);
                }

                return Task.CompletedTask;
            });

            await next(context);
        });
}
