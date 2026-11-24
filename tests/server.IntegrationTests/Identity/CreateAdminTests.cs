using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Identity;
using server.Modules.Identity.Domain;
using server.Modules.Identity.Features.Admin;
using server.Shared.Results;

namespace server.IntegrationTests.Identity;

/// <summary>Lệnh <c>--create-admin</c> (ADR-012 §5): tạo admin đầu tiên mà không có mật khẩu mặc định.</summary>
public class CreateAdminTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string GoodPassword = "Ngựa-vằn chạy 42 vòng";
    private readonly ApiFactory _factory;

    public CreateAdminTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewEmail() => $"{TestDatabase.Unique("admin")}@dxtechs.test";

    private async Task<Result<CreateAdminResponse>> Create(string email, string password, string name = "Quản Trị")
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CreateAdminHandler>()
            .HandleAsync(new CreateAdminCommand(email, name, password));
    }

    [Fact]
    public async Task Creates_an_active_admin_with_a_verifiable_hash_and_normalized_email()
    {
        string email = NewEmail();
        Result<CreateAdminResponse> result = await Create("  " + email.ToUpperInvariant() + " ", GoodPassword);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(email, result.Value.Email);

        await using AppDbContext db = TestDatabase.CreateContext();
        User user = await db.Set<User>().SingleAsync(u => u.Id == result.Value.Id);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.NotEqual(Guid.Empty, user.SecurityStamp);
        Assert.DoesNotContain(GoodPassword, user.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, GoodPassword));
        Assert.Equal(PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, GoodPassword + "x"));
    }

    [Fact]
    public async Task Existing_email_in_any_case_is_a_conflict_and_never_promoted()
    {
        string email = NewEmail();
        Assert.True((await Create(email, GoodPassword)).IsSuccess);

        Result<CreateAdminResponse> again = await Create(email.ToUpperInvariant(), GoodPassword);

        Assert.True(again.IsFailure);
        Assert.Equal("identity.email_taken", again.Error.Code);
        Assert.Equal(ErrorType.Conflict, again.Error.Type);
    }

    [Theory]
    [InlineData("ngan", "password")]
    [InlineData("1234567890", "password")]
    [InlineData("{local}-rat-dai-12", "password")]
    [InlineData("", "password")]
    public async Task Weak_passwords_are_rejected_before_touching_the_database(string password, string field)
    {
        string email = NewEmail();
        password = password.Replace("{local}", email.Split('@')[0]);

        Result<CreateAdminResponse> result = await Create(email, password);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.True(result.Error.FieldErrors!.ContainsKey(field));
        await using AppDbContext db = TestDatabase.CreateContext();
        Assert.False(await db.Set<User>().AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Command_line_reads_password_from_stdin_and_never_prints_it()
    {
        string email = NewEmail();
        var output = new StringWriter();

        int code = await IdentityModule.CreateAdminAsync(_factory.Services,
            ["--create-admin", email, "--name", "Nguyễn Đình Nam"], new StringReader(GoodPassword + "\n"), output, interactive: false);

        Assert.Equal(0, code);
        Assert.Contains($"đã tạo admin {email}", output.ToString());
        Assert.DoesNotContain(GoodPassword, output.ToString());

        await using AppDbContext db = TestDatabase.CreateContext();
        Assert.Equal("Nguyễn Đình Nam", await db.Set<User>().Where(u => u.Email == email).Select(u => u.FullName).SingleAsync());
    }

    [Fact]
    public async Task Command_line_exit_codes_distinguish_usage_and_validation_errors()
    {
        var output = new StringWriter();
        Assert.Equal(2, await IdentityModule.CreateAdminAsync(_factory.Services,
            ["--create-admin"], new StringReader(""), output, interactive: false));

        Assert.Equal(1, await IdentityModule.CreateAdminAsync(_factory.Services,
            ["--create-admin", "khong-phai-email"], new StringReader(GoodPassword), output, interactive: false));
        Assert.Contains("email: Email không hợp lệ.", output.ToString());
    }
}
