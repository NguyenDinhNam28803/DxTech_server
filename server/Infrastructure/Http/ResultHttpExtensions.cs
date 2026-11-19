using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using server.Shared.Results;

namespace server.Infrastructure.Http;

/// <summary>
/// Một chỗ duy nhất đổi <see cref="Result"/> của use case thành HTTP response (ADR-004).
/// Lỗi trả về dạng ProblemDetails (RFC 7807) — khớp với <c>ApiError</c> của client:
/// <c>detail</c> là thông báo cho người dùng, phần mở rộng <c>code</c> là mã lỗi ổn định.
/// </summary>
public static class ResultHttpExtensions
{
    public const string ProblemJsonContentType = "application/problem+json";

    public static int ToStatusCode(this ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "ErrorType chưa được ánh xạ sang HTTP."),
    };

    /// <summary>Thành công → 204 No Content.</summary>
    public static IActionResult ToActionResult(this ControllerBase controller, Result result)
        => result.IsSuccess ? controller.NoContent() : controller.ToProblem(result.Error);

    /// <summary>Thành công → 200 kèm dữ liệu.</summary>
    public static IActionResult ToActionResult<T>(this ControllerBase controller, Result<T> result)
        => result.IsSuccess ? controller.Ok(result.Value) : controller.ToProblem(result.Error);

    /// <summary>Thành công → 201 Created, trỏ tới action đọc lại tài nguyên.</summary>
    public static IActionResult ToCreatedResult<T>(
        this ControllerBase controller,
        Result<T> result,
        string actionName,
        Func<T, object> routeValues)
        => result.IsSuccess
            ? controller.CreatedAtAction(actionName, routeValues(result.Value), result.Value)
            : controller.ToProblem(result.Error);

    public static IActionResult ToProblem(this ControllerBase controller, Error error)
    {
        int status = error.Type.ToStatusCode();
        HttpContext httpContext = controller.HttpContext;

        ProblemDetails problem;
        if (error.Type == ErrorType.Validation && error.FieldErrors is { Count: > 0 })
        {
            var modelState = new ModelStateDictionary();
            foreach (var (field, messages) in error.FieldErrors)
            {
                foreach (string message in messages)
                {
                    modelState.AddModelError(field, message);
                }
            }

            problem = controller.ProblemDetailsFactory.CreateValidationProblemDetails(
                httpContext, modelState, statusCode: status, detail: error.Message);
        }
        else
        {
            // title để factory tự điền theo mã HTTP (ví dụ "Not Found"); thông báo nằm ở detail.
            problem = controller.ProblemDetailsFactory.CreateProblemDetails(
                httpContext, statusCode: status, detail: error.Message);
        }

        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { ProblemJsonContentType },
        };
    }
}
