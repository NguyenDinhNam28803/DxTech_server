using server.Shared.Results;

namespace server.UnitTests.Shared;

public class ResultTests
{
    [Fact]
    public void Success_carries_value()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_carries_error_and_value_access_throws()
    {
        Result<string> result = Error.NotFound("catalog.product_not_found", "Không tìm thấy sản phẩm.");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
        Assert.Equal("catalog.product_not_found", result.Error.Code);
        var ex = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("catalog.product_not_found", ex.Message);
    }

    [Fact]
    public void Non_generic_result_converts_from_error()
    {
        Result ok = Result.Success();
        Result failed = Error.Conflict("catalog.slug_taken", "Slug đã tồn tại.");

        Assert.True(ok.IsSuccess);
        Assert.True(failed.IsFailure);
        Assert.Equal(ErrorType.Conflict, failed.Error!.Type);
    }

    [Fact]
    public void Failure_requires_an_error()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
    }

    [Fact]
    public void Validation_error_keeps_field_errors()
    {
        var fields = new Dictionary<string, string[]> { ["email"] = ["Email không hợp lệ."] };

        Error error = Error.Validation("identity.invalid_email", "Dữ liệu không hợp lệ.", fields);

        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Equal("Email không hợp lệ.", error.FieldErrors!["email"][0]);
    }
}
