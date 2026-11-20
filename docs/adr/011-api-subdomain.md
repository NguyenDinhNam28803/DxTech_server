# ADR-011: API trên subdomain riêng `api.dxtechs.com`

- **Trạng thái:** Đã áp dụng — hạ tầng (DNS, chứng chỉ, Nginx) 20/11/2026; CORS ở PR-C1
- **Ngày:** 2026-11-20

## Bối cảnh
Website chuyển về tên miền gốc `https://dxtechs.com`. Cần chốt địa chỉ API mà trình duyệt gọi. Ba phương án:
(A) cùng tên miền `https://dxtechs.com/api`, (B) gọi thẳng qua IP `http://103.72.98.140`, (C) subdomain riêng
`https://api.dxtechs.com`.

## Quyết định
Chọn (C). Web ở `https://dxtechs.com`, API ở `https://api.dxtechs.com/api/...` (giữ tiền tố `/api` để không phải
đổi route, health check, CI và test). IP chỉ dùng cho SSH, CI/CD và debug qua SSH tunnel.

- CORS cấu hình trong ASP.NET Core (không ở Nginx): chỉ các origin trong `Cors:AllowedOrigins`
  (production: `https://dxtechs.com`; dev: `http://localhost:5173`, `http://localhost:4173`), `AllowCredentials`,
  phương thức GET/POST/PUT/PATCH/DELETE, mọi header, expose `Location`, cache preflight 10 phút.
- Cấu hình được kiểm tra khi khởi động: cấm `*`, cấm đường dẫn/`/` cuối, cấm `http://` ngoài localhost.
- Luôn gửi `Vary: Origin` (middleware `UseVaryByOrigin`) vì với một origin duy nhất ASP.NET Core không tự thêm.
- Cookie phiên đăng nhập (Phase 2): host-only trên `api.dxtechs.com`, `Secure; HttpOnly; SameSite=Strict`.
  `dxtechs.com` và `api.dxtechs.com` cùng site nên trình duyệt vẫn gửi cookie khi client gọi với `credentials: 'include'`.
- Dev giữ Vite proxy `/api` (cùng origin, không cần CORS); chỉ bản build production dùng `https://api.dxtechs.com/api`.
- Chuyển đổi theo expand → contract: mở `api.` song song `dxtechs.com/api`, chuyển client, rồi tắt đường cũ.

## Phương án đã cân nhắc và loại
- (B) IP: trang HTTPS gọi API HTTP bị trình duyệt chặn (mixed content); không có chứng chỉ cho IP ⇒ mất HTTPS cho
  đăng nhập/thanh toán; cookie `Secure` không dùng được; phải mở cổng 8080 ra Internet, phá giả định tin
  `X-Forwarded-*` của Phase 0.
- (A) cùng tên miền: đơn giản nhất (không CORS) nhưng không tách được API cho app mobile/đối tác sau này — lựa chọn
  của Backend Lead là tách ngay.

## Hệ quả
- Thêm một kết nối TLS và request preflight `OPTIONS` cho request JSON có body; giảm bằng `preconnect` ở client và
  `Access-Control-Max-Age: 600`.
- Mọi origin mới (ví dụ trang admin riêng) phải được thêm vào cấu hình — không bao giờ dùng `*`.
- Nginx: không thêm header `Access-Control-*` (tránh header trùng).
