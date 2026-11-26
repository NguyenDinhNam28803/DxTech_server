using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using server.Infrastructure.Http;
using server.Infrastructure.Options;
using server.Infrastructure.Persistence;
using server.Infrastructure.Persistence.Interceptors;
using server.Modules.Catalog;
using server.Modules.Identity;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

// Production: log dạng JSON một dòng mỗi sự kiện → `docker logs` đọc/lọc được, sau này đẩy vào hệ thống log.
if (builder.Environment.IsProduction())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options =>
    {
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
}

// Nguồn thời gian duy nhất của ứng dụng (ADR-006): test thay bằng FakeTimeProvider/TimeProvider tự viết.
builder.Services.AddSingleton(TimeProvider.System);

// Thiếu chuỗi kết nối → API không khởi động được (fail fast lúc deploy, không phải lúc khách dùng).
// Chỉ kiểm tra khi host chạy; công cụ EF lúc design-time (migrations bundle) không bị ảnh hưởng.
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<AuditableInterceptor>();
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .AddInterceptors(serviceProvider.GetRequiredService<AuditableInterceptor>()));

// Module nghiệp vụ (ADR-001): mỗi module tự đăng ký service của mình.
builder.Services.AddCatalogModule(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);

// An toàn mặc định (ADR-012 §5): mọi action MVC yêu cầu đăng nhập, trừ khi ghi [AllowAnonymous] rõ ràng
// (test kiến trúc bắt buộc mỗi controller ghi rõ). Dùng filter thay cho FallbackPolicy để URL không tồn tại vẫn
// trả 404 thay vì 401.
builder.Services.AddControllers(options => options.Filters.Add(new AuthorizeFilter())).AddJsonOptions(options =>
    // Trả tiếng Việt dạng UTF-8 thay vì \uXXXX: payload nhỏ hơn ~2 lần với dữ liệu có dấu, đọc log dễ hơn.
    // Encoder này vẫn escape ký tự nhạy cảm với HTML (< > & ' ").
    options.JsonSerializerOptions.Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Lỗi không bắt được -> trả về RFC 7807 ProblemDetails thay vì trang lỗi/stack trace.
builder.Services.AddProblemDetails();
// 423 (tài khoản tạm khoá — ADR-012) không có sẵn tiêu đề/đường dẫn mặc định trong ASP.NET Core.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
    options.ClientErrorMapping[StatusCodes.Status423Locked] = new Microsoft.AspNetCore.Mvc.ClientErrorData
    {
        Title = "Locked",
        Link = "https://www.rfc-editor.org/rfc/rfc4918#section-11.3",
    });

// CORS (ADR-011): web https://dxtechs.com gọi API https://api.dxtechs.com — khác origin.
// Chỉ các origin liệt kê trong cấu hình "Cors:AllowedOrigins" được gọi, có gửi kèm cookie.
// Cấu hình sai (rỗng, "*", có đường dẫn, http ngoài localhost) ⇒ API không khởi động.
builder.Services.AddOptions<ApiCorsOptions>()
    .Bind(builder.Configuration.GetSection(ApiCorsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>()
    .Configure<Microsoft.Extensions.Options.IOptions<ApiCorsOptions>>((cors, api) =>
        cors.AddDefaultPolicy(policy => policy
            .WithOrigins(api.Value.AllowedOrigins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .AllowAnyHeader()
            .AllowCredentials()
            .WithExposedHeaders("Location")
            .SetPreflightMaxAge(ApiCorsOptions.PreflightMaxAge)));

// "ready" = API chạy VÀ kết nối được PostgreSQL. CI dùng endpoint này để quyết định deploy thành công.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

// Production: API nằm sau Nginx trên cùng VPS. Nginx gửi X-Forwarded-For/Proto.
// Container chỉ publish ở 127.0.0.1:8080 (docker-compose.prod.yml), nên mọi request tới
// đều đi qua Nginx; request lại đến từ gateway của mạng Docker (172.x) chứ không phải
// loopback, vì vậy phải bỏ danh sách proxy mặc định (chỉ tin 127.0.0.1) thì header mới
// được áp dụng. KHÔNG mở cổng 8080 ra Internet khi còn cấu hình này.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    if (builder.Environment.IsProduction())
    {
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

var app = builder.Build();

// ADR-010: lệnh nạp dữ liệu demo — chạy rồi thoát, KHÔNG khởi động web server.
//   dotnet server.dll --seed-demo [--dry-run]
//   docker compose ... run --rm --no-deps api --seed-demo
if (args.Contains("--seed-demo"))
{
    return await CatalogModule.SeedDemoAsync(app.Services, dryRun: args.Contains("--dry-run"), Console.Out);
}

// ADR-012: tạo tài khoản admin — chạy rồi thoát. Mật khẩu đọc từ stdin, không bao giờ qua tham số.
//   docker compose ... run --rm --no-deps api --create-admin admin@dxtechs.com --name "Nguyễn Văn A"
if (args.Contains("--create-admin"))
{
    return await IdentityModule.CreateAdminAsync(
        app.Services, args, Console.In, Console.Out, interactive: !Console.IsInputRedirected);
}

// ---------------------------------------------------------------------------
// HTTP pipeline
// ---------------------------------------------------------------------------

// Phải đứng đầu để các middleware sau (log, HTTPS, auth) thấy đúng IP/scheme của client.
app.UseForwardedHeaders();

// CORS đặt sớm, trước mọi middleware có thể trả response (health check, controller...).
// Header CORS được gắn qua callback OnStarting nên vẫn có mặt trên response lỗi (404/500) —
// trình duyệt đọc được ProblemDetails thay vì báo "lỗi CORS" che mất lỗi thật
// (test: CorsTests.Error_responses_keep_cors_headers...).
// Vary trước CORS: preflight OPTIONS do CORS trả lời ngay tại chỗ, đặt sau sẽ không bao giờ chạy tới.
app.UseVaryByOrigin();
app.UseCors();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Giới hạn tần suất cho /api/auth/* và /api/me/* theo IP thật (ADR-012 §3) — sau UseForwardedHeaders.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Chỉ máy dev mới redirect sang HTTPS. Ở Production, Nginx lo HTTPS (container chỉ nghe HTTP 8080);
// môi trường test chạy trên TestServer không có HTTPS.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// Liveness: process còn sống, không chạm DB (dùng cho uptime monitor).
app.MapHealthChecks("/api/health", new HealthCheckOptions { Predicate = _ => false });
// Readiness: kiểm tra cả PostgreSQL (dùng cho bước kiểm tra sau deploy).
app.MapHealthChecks("/api/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.MapControllers();

app.Run();
return 0;

// Cho phép project test dùng WebApplicationFactory<Program>.
public partial class Program;
