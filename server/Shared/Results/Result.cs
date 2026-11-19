using System.Diagnostics.CodeAnalysis;

namespace server.Shared.Results;

/// <summary>
/// Kết quả của một use case không trả dữ liệu (ADR-004).
/// Lỗi nghiệp vụ dự đoán được đi qua <see cref="Error"/>; exception chỉ dành cho lỗi bất ngờ.
/// </summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Kết quả của một use case có trả dữ liệu.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error) : base(error) => _value = value;

    /// <summary>Giá trị khi thành công. Đọc khi thất bại là lỗi lập trình nên ném exception.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Không đọc được Value của kết quả lỗi '{Error.Code}'.");

    public static Result<T> Success(T value) => new(value, null);

    public static new Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
