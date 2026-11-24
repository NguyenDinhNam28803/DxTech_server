using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using server.IntegrationTests.Infrastructure;
using server.Infrastructure.Persistence;
using server.Modules.Identity.Domain;

namespace server.IntegrationTests.Identity;

/// <summary>
/// Migration Identity_Initial trên PostgreSQL thật (ADR-005, ADR-012): citext, ràng buộc, index, cascade, xmin.
/// Mỗi test dùng email/hash ngẫu nhiên để chạy song song và chạy lại được.
/// </summary>
public class IdentitySchemaTests : IAsyncLifetime
{
    private static readonly string FakeHash = "AQAAAAIAAYagAAAAE" + new string('x', 60);

    public Task InitializeAsync() => TestDatabase.EnsureMigratedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewEmail() => $"{TestDatabase.Unique("user")}@dxtechs.test";

    private static User NewUser(string? email = null, UserRole role = UserRole.Customer)
        => new(email ?? NewEmail(), "Người Dùng Thử", FakeHash, role);

    private static byte[] NewHash() => RandomNumberGenerator.GetBytes(RefreshToken.HashLength);

    // ---------- citext, index ----------

    [Fact]
    public async Task Email_is_unique_regardless_of_letter_case()
    {
        string email = NewEmail();
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.Add(NewUser(email));
            await db.SaveChangesAsync();
        }

        await using AppDbContext db2 = TestDatabase.CreateContext();
        db2.Add(NewUser(email.ToUpperInvariant()));
        DbUpdateException ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("ux_users_email", pg.ConstraintName);

        // Tra cứu cũng không phân biệt hoa thường — không cần lower() trong truy vấn.
        Assert.True(await db2.Set<User>().AnyAsync(u => u.Email == email.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("users", "ux_users_email", true, null)]
    [InlineData("users", "ix_users_role_admin", false, "(role = 'admin'::text)")]
    [InlineData("refresh_tokens", "ux_refresh_tokens_token_hash", true, null)]
    [InlineData("refresh_tokens", "ix_refresh_tokens_user_active", false, "(revoked_at IS NULL)")]
    [InlineData("refresh_tokens", "ix_refresh_tokens_family_id", false, null)]
    [InlineData("refresh_tokens", "ix_refresh_tokens_expires_at", false, null)]
    public async Task Indexes_exist_with_expected_uniqueness_and_filter(string table, string index, bool unique, string? predicate)
    {
        await using AppDbContext db = TestDatabase.CreateContext();
        await using var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            SELECT x.indisunique, pg_get_expr(x.indpred, x.indrelid)
              FROM pg_index x
              JOIN pg_class i ON i.oid = x.indexrelid
              JOIN pg_class t ON t.oid = x.indrelid
              JOIN pg_namespace n ON n.oid = t.relnamespace
             WHERE n.nspname = 'identity' AND t.relname = '{table}' AND i.relname = '{index}'
            """, conn);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"thiếu index {index}");
        Assert.Equal(unique, r.GetBoolean(0));
        Assert.Equal(predicate, r.IsDBNull(1) ? null : r.GetString(1));
    }

    [Fact]
    public async Task Admin_listing_index_is_descending_for_keyset_paging()
    {
        string def = await Scalar("SELECT pg_get_indexdef('identity.ix_users_created_at'::regclass)");
        Assert.Contains("(created_at DESC, id DESC)", def);
    }

    // ---------- CHECK constraints ----------

    [Theory]
    [InlineData("role", "'superadmin'", "ck_users_role")]
    [InlineData("status", "'banned'", "ck_users_status")]
    [InlineData("phone", "'09-123'", "ck_users_phone")]
    [InlineData("tax_code", "'12345'", "ck_users_tax_code")]
    [InlineData("failed_login_count", "-1", "ck_users_failed_login_count")]
    [InlineData("full_name", "''", "ck_users_full_name")]
    [InlineData("password_hash", "'plain'", "ck_users_password_hash")]
    [InlineData("email", "'khong-co-a-cong'", "ck_users_email")]
    [InlineData("email", "'a b@x.vn'", "ck_users_email")]
    public async Task Invalid_user_values_are_rejected_by_the_database(string column, string value, string constraint)
    {
        User user = NewUser();
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.Add(user);
            await db.SaveChangesAsync();
        }

        PostgresException ex = await Assert.ThrowsAsync<PostgresException>(
            () => Execute($"UPDATE identity.users SET {column} = {value} WHERE id = '{user.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
    }

    [Theory]
    [InlineData("0912345678")]
    [InlineData("+84912345678")]
    public async Task Valid_vietnamese_phone_and_tax_codes_are_accepted(string phone)
    {
        User user = NewUser();
        user.Phone = phone;
        user.TaxCode = "0312345678-001";
        user.CompanyName = "Công ty TNHH Thử Nghiệm";
        await using AppDbContext db = TestDatabase.CreateContext();
        db.Add(user);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Enums_are_stored_as_snake_case_text()
    {
        User user = NewUser(role: UserRole.Admin);
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.Add(user);
            await db.SaveChangesAsync();
        }

        Assert.Equal("admin|active", await Scalar($"SELECT role || '|' || status FROM identity.users WHERE id = '{user.Id}'"));
    }

    // ---------- refresh_tokens ----------

    [Fact]
    public async Task Refresh_token_round_trips_and_is_deleted_with_its_user()
    {
        User user = NewUser();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var token = new RefreshToken(user.Id, NewHash(), Guid.NewGuid(), now, now.AddDays(14), now.AddDays(30))
        {
            CreatedIp = IPAddress.Parse("203.0.113.7"),
            UserAgent = "Mozilla/5.0 (test)",
        };

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.AddRange(user, token);
            await db.SaveChangesAsync();
        }

        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            RefreshToken loaded = await db.Set<RefreshToken>().SingleAsync(t => t.Id == token.Id);
            Assert.Equal(token.TokenHash, loaded.TokenHash);
            Assert.Equal(IPAddress.Parse("203.0.113.7"), loaded.CreatedIp);
            Assert.Null(loaded.RevokedReason);

            loaded.Revoke(RefreshTokenRevokedReason.ReuseDetected, now.AddMinutes(1));
            await db.SaveChangesAsync();
        }

        Assert.Equal("reuse_detected", await Scalar($"SELECT revoked_reason FROM identity.refresh_tokens WHERE id = '{token.Id}'"));

        await Execute($"DELETE FROM identity.users WHERE id = '{user.Id}'");
        Assert.Equal("0", await Scalar($"SELECT count(*)::text FROM identity.refresh_tokens WHERE id = '{token.Id}'"));
    }

    [Theory]
    [InlineData("token_hash = '\\x00ff'::bytea", "ck_refresh_tokens_hash_len")]
    [InlineData("expires_at = created_at", "ck_refresh_tokens_expiry")]
    [InlineData("expires_at = session_expires_at + interval '1 second'", "ck_refresh_tokens_expiry")]
    [InlineData("revoked_at = now()", "ck_refresh_tokens_revoked")]
    [InlineData("revoked_at = now(), revoked_reason = 'stolen'", "ck_refresh_tokens_revoked_reason")]
    [InlineData("replaced_by_id = id", "ck_refresh_tokens_not_own_replacement")]
    [InlineData("user_agent = repeat('a', 513)", "ck_refresh_tokens_user_agent")]
    public async Task Invalid_refresh_token_values_are_rejected(string set, string constraint)
    {
        User user = NewUser();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var token = new RefreshToken(user.Id, NewHash(), Guid.NewGuid(), now, now.AddDays(14), now.AddDays(30));
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.AddRange(user, token);
            await db.SaveChangesAsync();
        }

        PostgresException ex = await Assert.ThrowsAsync<PostgresException>(
            () => Execute($"UPDATE identity.refresh_tokens SET {set} WHERE id = '{token.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
    }

    [Fact]
    public async Task Same_token_hash_cannot_be_stored_twice()
    {
        User user = NewUser();
        byte[] hash = NewHash();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using AppDbContext db = TestDatabase.CreateContext();
        db.AddRange(user,
            new RefreshToken(user.Id, hash, Guid.NewGuid(), now, now.AddDays(1), now.AddDays(30)),
            new RefreshToken(user.Id, (byte[])hash.Clone(), Guid.NewGuid(), now, now.AddDays(1), now.AddDays(30)));

        DbUpdateException ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("ux_refresh_tokens_token_hash", Assert.IsType<PostgresException>(ex.InnerException).ConstraintName);
    }

    // ---------- concurrency ----------

    [Fact]
    public async Task Concurrent_role_changes_are_detected_with_xmin()
    {
        User user = NewUser();
        await using (AppDbContext db = TestDatabase.CreateContext())
        {
            db.Add(user);
            await db.SaveChangesAsync();
        }

        await using AppDbContext first = TestDatabase.CreateContext();
        await using AppDbContext second = TestDatabase.CreateContext();
        User a = await first.Set<User>().SingleAsync(u => u.Id == user.Id);
        User b = await second.Set<User>().SingleAsync(u => u.Id == user.Id);

        a.Role = UserRole.Admin;
        await first.SaveChangesAsync();

        b.Status = UserStatus.Locked;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    // ---------- helpers ----------

    private static async Task Execute(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> Scalar(string sql)
    {
        await using var conn = new NpgsqlConnection(ApiFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
