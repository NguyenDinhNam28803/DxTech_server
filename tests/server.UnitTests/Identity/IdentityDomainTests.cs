using server.Infrastructure.Persistence.Conversions;
using server.Modules.Identity.Domain;

namespace server.UnitTests.Identity;

public class EmailAddressTests
{
    [Theory]
    [InlineData("Admin@DXTechs.com", "admin@dxtechs.com")]
    [InlineData("  nam.nguyen+shop@gmail.com ", "nam.nguyen+shop@gmail.com")]
    [InlineData("a@b.vn", "a@b.vn")]
    public void Valid_addresses_are_trimmed_and_lowercased(string input, string expected)
        => Assert.Equal(expected, EmailAddress.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("khong-co-a-cong")]
    [InlineData("@dxtechs.com")]
    [InlineData("a@localhost")]
    [InlineData("a b@dxtechs.com")]
    [InlineData("Nam <nam@dxtechs.com>")]
    [InlineData("a@dxtechs.")]
    public void Invalid_addresses_are_rejected(string? input) => Assert.Null(EmailAddress.Normalize(input));

    [Fact]
    public void Too_long_addresses_are_rejected()
        => Assert.Null(EmailAddress.Normalize(new string('a', 250) + "@x.vn"));
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("ngựa vằn chạy")]                   // có dấu, có khoảng trắng: hợp lệ
    [InlineData("correct horse battery staple")]
    [InlineData("khongcokytudacbiet")]              // không bắt buộc hoa/số/ký tự đặc biệt (NIST)
    public void Long_unusual_passwords_are_accepted(string password)
        => Assert.Empty(PasswordPolicy.Validate(password, "nam@dxtechs.com"));

    [Theory]
    [InlineData("ngắn quá", "ít nhất 10")]
    [InlineData("1234567890", "phổ biến")]
    [InlineData("aaaaaaaaaaaa", "phổ biến")]
    [InlineData("nguyendinhnam2026", "chứa email")]
    public void Weak_passwords_are_rejected_with_a_reason(string password, string reason)
        => Assert.Contains(PasswordPolicy.Validate(password, "nguyendinhnam@dxtechs.com"), e => e.Contains(reason));

    [Fact]
    public void Length_counts_visible_characters_not_utf16_units()
    {
        // 10 ký tự hiển thị có dấu tổ hợp (NFD) vẫn là 10, không bị tính thành 20.
        string tenChars = string.Concat(Enumerable.Repeat("á", 5)) + "bcdef";
        Assert.DoesNotContain(PasswordPolicy.Validate(tenChars, null), e => e.Contains("ít nhất"));
        Assert.Contains(PasswordPolicy.Validate(new string('x', 129) + "y", null), e => e.Contains("tối đa"));
    }
}

public class RefreshTokenTests
{
    [Fact]
    public void Hash_must_be_exactly_32_bytes()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new RefreshToken(Guid.NewGuid(), new byte[31], Guid.NewGuid(), now, now.AddDays(1), now.AddDays(2)));
    }

    [Fact]
    public void Revoke_keeps_the_first_reason()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var token = new RefreshToken(Guid.NewGuid(), new byte[32], Guid.NewGuid(), now, now.AddDays(1), now.AddDays(2));

        token.Revoke(RefreshTokenRevokedReason.Rotated, now);
        token.Revoke(RefreshTokenRevokedReason.Logout, now.AddMinutes(1));

        Assert.Equal(RefreshTokenRevokedReason.Rotated, token.RevokedReason);
        Assert.Equal(now, token.RevokedAt);
    }

    [Fact]
    public void Security_stamp_is_random_v4_and_rotates()
    {
        var user = new User("a@b.vn", "A", "hash-hash-hash-hash-hash", UserRole.Customer);
        Guid first = user.SecurityStamp;
        user.RotateSecurityStamp();

        Assert.NotEqual(first, user.SecurityStamp);
        Assert.Equal(4, (user.SecurityStamp.ToByteArray()[7] >> 4) & 0xF); // version nibble = 4 (ngẫu nhiên)
    }

    [Fact]
    public void Enum_check_sql_matches_adr_values()
    {
        Assert.Equal("role IN ('customer', 'editor', 'admin')", SnakeCaseEnumConverter<UserRole>.CheckSql("role")); // editor: ADR-015 §4
        Assert.Equal("r IN ('rotated', 'logout', 'reuse_detected', 'password_changed', 'admin')",
            SnakeCaseEnumConverter<RefreshTokenRevokedReason>.CheckSql("r"));
    }
}
