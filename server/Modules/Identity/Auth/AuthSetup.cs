using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using server.Infrastructure.Options;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Domain;

namespace server.Modules.Identity.Auth;

/// <summary>Tên policy dùng trong <c>[Authorize(Policy = ...)]</c>.</summary>
internal static class AuthPolicies
{
    /// <summary>Admin đang hoạt động — kiểm tra lại DB (ADR-012 §5).</summary>
    public const string Admin = Contracts.Policies.Admin;

    /// <summary>Editor hoặc admin đang hoạt động (ADR-015 §4).</summary>
    public const string Content = Contracts.Policies.Content;
}

/// <summary>Đăng ký xác thực, phân quyền, giới hạn tần suất cho module Identity (ADR-012).</summary>
internal static class AuthSetup
{
    public static void AddAuth(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMemoryCache();
        services.AddSingleton<AccessTokenService>();
        services.AddSingleton<OriginGuard>();
        services.AddScoped<SessionService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = AccessTokenService.ValidationParameters(auth.Value);
                jwt.Events = new JwtBearerEvents
                {
                    // Trả ProblemDetails có mã lỗi thay vì 401 rỗng — client biết khi nào cần gọi /refresh.
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        bool expired = ctx.AuthenticateFailure is SecurityTokenExpiredException;
                        await WriteProblem(ctx.HttpContext, StatusCodes.Status401Unauthorized,
                            expired ? "auth.token_expired" : "auth.unauthorized",
                            expired ? "Phiên truy cập đã hết hạn." : "Bạn cần đăng nhập để tiếp tục.");
                    },
                    OnForbidden = ctx => WriteProblem(ctx.HttpContext, StatusCodes.Status403Forbidden,
                        "auth.forbidden", "Bạn không có quyền thực hiện thao tác này."),
                };
            });

        services.AddScoped<IAuthorizationHandler, ActiveRoleHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Admin, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(AuthClaims.Role, UserRoles.Name(UserRole.Admin))
                .AddRequirements(new ActiveRoleRequirement(UserRole.Admin)))
            .AddPolicy(AuthPolicies.Content, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(AuthClaims.Role, UserRoles.Name(UserRole.Editor), UserRoles.Name(UserRole.Admin))
                .AddRequirements(new ActiveRoleRequirement(UserRole.Editor, UserRole.Admin)));

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<AuthOptions>>((limiter, auth) => ConfigureRateLimits(limiter, auth.Value.RateLimits));
    }

    public static Task WriteProblem(HttpContext http, int status, string code, string detail)
        => Results.Problem(
                detail: detail,
                statusCode: status,
                title: status switch
                {
                    StatusCodes.Status401Unauthorized => "Unauthorized",
                    StatusCodes.Status403Forbidden => "Forbidden",
                    StatusCodes.Status429TooManyRequests => "Too Many Requests",
                    _ => null,
                },
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(http);

    // ---------- Rate limit (ADR-012 §3) ----------

    /// <summary>Nhóm endpoint cần giới hạn; null = không giới hạn.</summary>
    internal static string? LimitGroup(PathString path)
    {
        if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)) return "login";
        if (path.Equals("/api/auth/register", StringComparison.OrdinalIgnoreCase)) return "register";
        if (path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase)) return "refresh";
        if (path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/me")) return "auth";
        if (path.Equals("/api/cart/quote", StringComparison.OrdinalIgnoreCase)) return "cart";
        if (path.Equals("/api/orders", StringComparison.OrdinalIgnoreCase)) return "orders";
        return null;
    }

    private static void ConfigureRateLimits(RateLimiterOptions limiter, AuthRateLimits limits)
    {
        // IP sau UseForwardedHeaders (ForwardLimit = 1): giá trị Nginx thêm vào, client không tự giả được.
        static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        PartitionedRateLimiter<HttpContext> perMinute = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        {
            string? group = LimitGroup(c.Request.Path);
            int permits = group switch
            {
                "login" => limits.LoginPerMinute,
                "refresh" => limits.RefreshPerMinute,
                "auth" => limits.OtherPerMinute,
                "cart" => limits.CartQuotePerMinute,
                _ => 0,
            };
            return permits == 0
                ? RateLimitPartition.GetNoLimiter("none")
                : RateLimitPartition.GetFixedWindowLimiter($"m:{group}:{Ip(c)}", _ => Window(permits, TimeSpan.FromMinutes(1)));
        });

        PartitionedRateLimiter<HttpContext> perHour = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        {
            string? group = LimitGroup(c.Request.Path);
            int permits = group switch
            {
                "login" => limits.LoginPerHour,
                "register" => limits.RegisterPerHour,
                "orders" => limits.OrdersPerHour,
                _ => 0,
            };
            return permits == 0
                ? RateLimitPartition.GetNoLimiter("none")
                : RateLimitPartition.GetFixedWindowLimiter($"h:{group}:{Ip(c)}", _ => Window(permits, TimeSpan.FromHours(1)));
        });

        limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(perMinute, perHour);
        limiter.OnRejected = async (ctx, ct) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            }

            await WriteProblem(ctx.HttpContext, StatusCodes.Status429TooManyRequests,
                "auth.rate_limited", "Bạn thao tác quá nhanh. Vui lòng thử lại sau ít phút.");
        };
    }

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}

/// <summary>
/// Tài khoản phải còn hoạt động, role trong DB thuộc <see cref="Roles"/> và security stamp trong token khớp DB
/// (bị hạ quyền/khoá ⇒ mất quyền gần như ngay, không chờ access token hết hạn). Dùng cho policy admin và content.
/// </summary>
internal sealed class ActiveRoleRequirement(params UserRole[] roles) : IAuthorizationRequirement
{
    public IReadOnlyCollection<UserRole> Roles { get; } = roles;
}

internal sealed class ActiveRoleHandler(AppDbContext db, IMemoryCache cache, IOptions<AuthOptions> options)
    : AuthorizationHandler<ActiveRoleRequirement>
{
    private sealed record AdminState(UserRole Role, UserStatus Status, string Stamp);

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveRoleRequirement requirement)
    {
        string? sub = context.User.FindFirst(AuthClaims.Subject)?.Value;
        string? stamp = context.User.FindFirst(AuthClaims.StampPrefix)?.Value;
        if (!Guid.TryParse(sub, out Guid userId) || stamp is null)
        {
            return;
        }

        AdminState? state = await Load(userId);
        if (state is { Status: UserStatus.Active } && requirement.Roles.Contains(state.Role) && state.Stamp == stamp)
        {
            context.Succeed(requirement);
        }
    }

    private async Task<AdminState?> Load(Guid userId)
    {
        int seconds = options.Value.AdminCheckCacheSeconds;
        string key = $"identity:admin-state:{userId}";
        if (seconds > 0 && cache.TryGetValue(key, out AdminState? cached))
        {
            return cached;
        }

        AdminState? state = await db.Set<User>().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new AdminState(u.Role, u.Status, u.SecurityStamp.ToString()))
            .SingleOrDefaultAsync();
        if (state is not null)
        {
            state = state with { Stamp = Guid.Parse(state.Stamp).ToString("N")[..8] };
        }

        if (seconds > 0)
        {
            cache.Set(key, state, TimeSpan.FromSeconds(seconds));
        }

        return state;
    }
}
