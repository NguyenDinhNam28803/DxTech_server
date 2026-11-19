# ADR-001: Modular monolith dạng gọn trong một project

- **Trạng thái:** Đã áp dụng — PR-1 (khung `Modules/`, test kiến trúc)
- **Ngày:** 2026-11-18

## Bối cảnh
Team 2–3 người bán thời gian, một VPS, một container API. Luồng thanh toán → cấp license key → hoàn tất đơn phải nằm trong một transaction. Đã so sánh 5 phương án: Layered, Clean Architecture 4 project, Vertical Slice, Modular monolith nhiều project, Microservices.

## Quyết định
Một project `server`, chia `Modules/<Nghiệp vụ>/` (Catalog, Identity, Sales, Inventory, Payments, Admin). Bên trong mỗi module: `Contracts/` (public), `Domain/`, `Persistence/`, `Features/` (internal, mỗi file một use case), `Api/` (controller). Module khác chỉ được dùng `Contracts/`.

## Phương án đã cân nhắc và loại
Layered: ranh giới yếu. Clean 4 project: nhiều file trung gian, kéo theo MediatR/AutoMapper. Nhiều project/module: quá nặng cho team hiện tại. Microservices: giao dịch phân tán, cần broker — loại.

## Hệ quả
Ranh giới chỉ là quy ước nên được bảo vệ bằng: `internal` cho entity/handler, `InternalsVisibleTo` cho project test, và test kiến trúc `tests/server.UnitTests/Architecture` (kiểm tra chữ ký type; thân method do review). Khi một module vượt ~3.000 dòng hoặc có team riêng, tách `Modules/X` thành project `X`.
