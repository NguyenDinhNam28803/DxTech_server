using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using server.Infrastructure.Options;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Auth;
using server.Modules.Identity.Domain;
using server.Modules.Identity.Features.Admin;
using server.Modules.Identity.Features.Auth;
using server.Modules.Identity.Jobs;
using server.Shared.Results;

namespace server.Modules.Identity;

/// <summary>
/// Module Identity: tài khoản, mật khẩu, phiên đăng nhập — schema PostgreSQL <c>identity</c> (ADR-012).
/// Bố cục giống Catalog (xem <c>CatalogModule</c>), thêm <c>Auth/</c> (JWT, refresh token, policy, rate limit — internal)
/// và <c>Jobs/</c> (dọn token). PR-7: bảng + <c>--create-admin</c>. PR-8: API xác thực.
/// </summary>
public static class IdentityModule
{
    public const string Schema = "identity";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        // Chỉ dùng PasswordHasher của ASP.NET Core Identity (PBKDF2-HMAC-SHA512, salt, có phiên bản) —
        // không dùng AddIdentity/EF stores (ADR-012, phương án đã loại).
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        AuthSetup.AddAuth(services, configuration);

        // Handler có constructor internal (nhận kiểu internal của module) ⇒ đăng ký tường minh.
        services.AddScoped(sp => new CreateAdminHandler(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IPasswordHasher<User>>()));
        services.AddScoped(sp => new RegisterHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IPasswordHasher<User>>(), sp.GetRequiredService<SessionService>()));
        services.AddScoped(sp => new LoginHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IPasswordHasher<User>>(), sp.GetRequiredService<SessionService>(),
            sp.GetRequiredService<IOptions<AuthOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new SessionHandler(sp.GetRequiredService<SessionService>()));
        services.AddScoped(sp => new AccountHandler(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IPasswordHasher<User>>(), sp.GetRequiredService<SessionService>()));

        services.AddScoped<Contracts.IUserProfiles, UserProfiles>();

        services.AddSingleton<RefreshTokenCleanup>();
        services.AddHostedService(sp => sp.GetRequiredService<RefreshTokenCleanup>());
        return services;
    }

    /// <summary>
    /// Lệnh <c>--create-admin &lt;email&gt; [--name "Họ tên"]</c>: tạo admin rồi thoát, không khởi động web server.
    /// Mật khẩu đọc từ stdin (không qua tham số dòng lệnh ⇒ không nằm trong shell history hay <c>ps</c>):
    /// có TTY thì hỏi ẩn ký tự 2 lần; stdin là pipe thì đọc dòng đầu tiên.
    /// Mã thoát: 0 thành công, 1 lỗi, 2 sai cú pháp.
    /// </summary>
    public static async Task<int> CreateAdminAsync(
        IServiceProvider services, string[] args, TextReader input, TextWriter output, bool interactive, CancellationToken ct = default)
    {
        int i = Array.IndexOf(args, "--create-admin");
        string? email = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        int n = Array.IndexOf(args, "--name");
        string? name = n >= 0 && n + 1 < args.Length ? args[n + 1] : null;
        if (email is null)
        {
            await output.WriteLineAsync("Dùng: --create-admin <email> [--name \"Họ tên\"]   (mật khẩu nhập từ bàn phím / stdin)");
            return 2;
        }

        string? password;
        if (interactive)
        {
            password = ReadHidden("Mật khẩu admin (≥ 10 ký tự): ", output);
            string? again = ReadHidden("Nhập lại mật khẩu: ", output);
            if (password != again)
            {
                await output.WriteLineAsync("create-admin THẤT BẠI: hai lần nhập mật khẩu không khớp.");
                return 1;
            }
        }
        else
        {
            password = await input.ReadLineAsync(ct);
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Result<CreateAdminResponse> result = await scope.ServiceProvider.GetRequiredService<CreateAdminHandler>()
            .HandleAsync(new CreateAdminCommand(email, name ?? "Quản trị viên", password ?? string.Empty), ct);

        if (result.IsFailure)
        {
            await output.WriteLineAsync($"create-admin THẤT BẠI: {result.Error.Message}");
            foreach ((string field, string[] errors) in result.Error.FieldErrors ?? new Dictionary<string, string[]>())
            {
                await output.WriteLineAsync($"  - {field}: {string.Join(" ", errors)}");
            }

            return 1;
        }

        await output.WriteLineAsync($"create-admin: đã tạo admin {result.Value.Email} (id {result.Value.Id}).");
        return 0;
    }

    private static string? ReadHidden(string prompt, TextWriter output)
    {
        output.Write(prompt);
        var chars = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return chars.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Length > 0)
                {
                    chars.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Append(key.KeyChar);
            }
        }
    }
}
