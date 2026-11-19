# ADR-004: Result<T> + ProblemDetails cho lỗi nghiệp vụ

- **Trạng thái:** Đã áp dụng — PR-1 (`Shared/Results`, `Infrastructure/Http/ResultHttpExtensions`)
- **Ngày:** 2026-11-18

## Bối cảnh
Client (`src/api` của DXTechs) đọc lỗi theo chuẩn RFC 7807; cần một cách trả lỗi thống nhất cho mọi module.

## Quyết định
Lỗi dự đoán được trả về bằng `Result`/`Result<T>` chứa `Error(Code, Message, Type, FieldErrors)`. `ErrorType` → HTTP: Validation 400, Unauthorized 401, Forbidden 403, NotFound 404, Conflict 409, BusinessRule 422. Một chỗ duy nhất (`ToActionResult`/`ToProblem`) đổi sang ProblemDetails, thêm phần mở rộng `code`. Exception chỉ cho lỗi bất ngờ → 500 qua `UseExceptionHandler`, không lộ chi tiết.

## Phương án đã cân nhắc và loại
Ném exception cho mọi lỗi; envelope tự chế `{success, data}`.

## Hệ quả
Phải kiểm tra `IsFailure`. Mã lỗi (`code`) là hợp đồng với client: không đổi tên sau khi đã phát hành.
