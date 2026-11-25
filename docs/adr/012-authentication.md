# ADR-012: Xác thực & phân quyền (Phase 2)

- **Trạng thái:** Đã chấp nhận — Backend Lead duyệt khi merge PR #10 (23/11/2026). PR-7: schema `identity`, `citext`,
  `--create-admin` (đã áp dụng); PR-8: API xác thực (chưa)
- **Ngày:** 2026-11-23
- **Liên quan:** ADR-006 (id, thời gian), ADR-009 (role DB), ADR-011 (API trên `api.dxtechs.com`, cookie cùng site);
  thiết kế bảng: *Thiết kế toàn bộ Database v1.0*, mục 6 (`identity.users`, `identity.refresh_tokens`)

## Bối cảnh
Client hiện giữ người dùng giả trong `localStorage` và có `switchRole()` — ai cũng tự thành admin được. Phase 2 cần
đăng ký, đăng nhập, duy trì phiên, đăng xuất và phân quyền Customer/Admin **ở server**, đủ an toàn để Phase 3 gắn
đơn hàng và tiền vào tài khoản. Ràng buộc: .NET 8, chỉ thêm gói của Microsoft 8.0.x.

Đặc điểm cần tính tới: SPA (Vue) ở `https://dxtechs.com`, API ở `https://api.dxtechs.com` — **khác origin nhưng
cùng site**; API sau Nginx; dự kiến có app/đối tác gọi API sau này (lý do tách subdomain ở ADR-011); một instance API.

## Quyết định

### 1. Mô hình phiên: access token ngắn + refresh token xoay vòng
| Thành phần | Quyết định |
|---|---|
| Access token | JWT ký **HS256**, sống **15 phút**. Claim: `sub` (user id), `role`, `sst` (8 ký tự đầu security stamp), `jti`, `iat`, `exp`; `iss=https://api.dxtechs.com`, `aud=dxtechs-web`; lệch giờ cho phép 30 giây |
| Nơi giữ access token | **Bộ nhớ** của SPA (Pinia, không persist) — không `localStorage`/`sessionStorage`. Gửi bằng `Authorization: Bearer` |
| Refresh token | 32 byte ngẫu nhiên (`RandomNumberGenerator`), base64url; DB chỉ lưu **SHA-256** (`token_hash`, 32 byte) |
| Cookie refresh | `__Secure-dxt_rt`; `HttpOnly; Secure; SameSite=Strict; Path=/api/auth`; host-only trên `api.dxtechs.com`; `Max-Age` = hạn còn lại |
| Thời hạn | Trượt **14 ngày** kể từ lần refresh cuối; tuyệt đối **30 ngày** kể từ lần đăng nhập (cùng `family_id`) — hết thì đăng nhập lại |
| Xoay vòng | Mỗi lần `POST /api/auth/refresh`: thu hồi token cũ (`revoked_reason='rotated'`, `replaced_by_id`), cấp token mới cùng `family_id`, trong **một transaction** khoá dòng token cũ (`FOR UPDATE`) |
| Phát hiện đánh cắp | Token đã `rotated` bị dùng lại **sau thời gian ân hạn 10 giây** ⇒ thu hồi cả họ (`reuse_detected`), trả 401, ghi log bảo mật |
| Hai tab refresh cùng lúc | Client: chỉ một refresh tại một thời điểm (`navigator.locks`); server: token `rotated` trong ân hạn 10 giây ⇒ trả **409** (không thu hồi họ) để client đọc lại cookie mới và thử lại |
| Khoá ký | `Auth__SigningKeys__0` (khoá hiện tại), `Auth__SigningKeys__1` (khoá cũ khi xoay) — mỗi khoá ≥ 32 byte ngẫu nhiên, GitHub Secrets → `.env`. Ký bằng khoá 0, chấp nhận cả 0 và 1 (`kid`). Thiếu/ngắn ⇒ **API từ chối khởi động** (giống kiểm tra CORS) |

### 2. Mật khẩu
- `PasswordHasher<User>` (gói `Microsoft.Extensions.Identity.Core`) — PBKDF2-HMAC-SHA512, salt ngẫu nhiên, có số
  phiên bản trong chuỗi hash. Khi `VerifyHashedPassword` trả `SuccessRehashNeeded` ⇒ hash lại và lưu.
- Quy tắc theo NIST SP 800-63B: **10–128 ký tự**, không bắt buộc ký tự đặc biệt/hoa/số, chặn mật khẩu nằm trong
  danh sách 10.000 mật khẩu phổ biến (file nhúng) và mật khẩu chứa email. Không giới hạn ký tự Unicode.
- Đổi mật khẩu: cần mật khẩu cũ; đổi xong ⇒ đổi `security_stamp`, thu hồi **mọi** refresh token (`password_changed`).

### 3. Đăng nhập, khoá tài khoản, giới hạn tần suất
- Sai email hoặc mật khẩu đều trả **cùng một lỗi** `auth.invalid_credentials` (401). Email không tồn tại vẫn chạy
  `VerifyHashedPassword` với một hash giả ⇒ thời gian phản hồi như nhau (chống dò email qua thời gian).
- 5 lần sai liên tiếp ⇒ khoá **15 phút** (`lockout_until`); đăng nhập đúng ⇒ đặt lại bộ đếm. Thời gian khoá ngắn có
  chủ đích: khoá dài biến tính năng này thành công cụ khoá tài khoản người khác.
- Rate limit có sẵn của ASP.NET Core 8 (`Microsoft.AspNetCore.RateLimiting`), phân theo IP thật của client:

| Endpoint | Giới hạn mỗi IP | Lý do |
|---|---|---|
| `POST /api/auth/login` | 10/phút và 50/giờ | Lớp phòng thủ chính chống dò mật khẩu |
| `POST /api/auth/register` | 5/giờ | Chống tạo tài khoản hàng loạt |
| `POST /api/auth/refresh` | 60/phút | Đủ cho nhiều tab |
| Còn lại `/api/auth/*` | 30/phút | |

  Vượt ⇒ 429 + `Retry-After` + ProblemDetails `auth.rate_limited`. IP lấy sau `UseForwardedHeaders` với
  `ForwardLimit=1`: chỉ tin giá trị cuối cùng do Nginx thêm vào `X-Forwarded-For`, nên client không giả IP được bằng
  cách tự gửi header này. Bộ đếm nằm trong bộ nhớ — đủ cho một instance; nhiều instance sẽ cần kho chung (ghi nhận ở Hệ quả).
- Đăng ký: email đã dùng ⇒ 409 `auth.email_taken`. Chấp nhận lộ “email này đã có tài khoản” vì chưa có dịch vụ email
  để làm luồng xác nhận im lặng; giảm thiểu bằng rate limit. Xem lại khi có email (Phase 3).

### 4. CSRF
- Endpoint dùng **Bearer** (mọi API trừ refresh/logout) không bị CSRF: trình duyệt không tự gửi header `Authorization`.
- `/api/auth/refresh`, `/api/auth/logout` dùng **cookie** ⇒ hai lớp: `SameSite=Strict` (trình duyệt không gửi cookie
  từ site khác) và server **kiểm tra header `Origin`** phải nằm trong `Cors:AllowedOrigins`, thiếu/sai ⇒ 403.

### 5. Phân quyền
- **An toàn mặc định:** `FallbackPolicy = RequireAuthenticatedUser`. Endpoint công khai (catalog, health, đăng
  ký/đăng nhập) phải ghi `[AllowAnonymous]` rõ ràng. Test kiến trúc: mọi action có `[Authorize]` hoặc `[AllowAnonymous]`.
- Policy `Customer`: đã xác thực. Policy `Admin`: `role=admin` **và** kiểm tra lại DB mỗi request (`status='active'`,
  `security_stamp` khớp `sst`) — tra theo khoá chính, cache 30 giây. Nhờ vậy hạ quyền/khoá một admin có hiệu lực
  gần như ngay, không phải chờ access token hết hạn. Customer không kiểm tra DB mỗi request (cửa sổ tối đa 15 phút).
- Tài khoản tự đăng ký luôn là `customer`. Admin đầu tiên tạo bằng lệnh `--create-admin <email>` (mật khẩu đọc từ
  stdin, không qua tham số dòng lệnh, không có mật khẩu mặc định). Đổi role chỉ admin làm được và ghi audit (Phase 4).

### 6. API (PR-8)
| Method · Path | Xác thực | Kết quả |
|---|---|---|
| `POST /api/auth/register` | — | 201 + `{accessToken, expiresAt, user}` + cookie refresh |
| `POST /api/auth/login` | — | 200 như trên; 401 `auth.invalid_credentials`; 423 `auth.locked` (kèm thời điểm mở) |
| `POST /api/auth/refresh` | cookie + Origin | 200 token mới + cookie mới; 401 khi hết hạn/thu hồi; 409 trong ân hạn |
| `POST /api/auth/logout` | cookie + Origin | 204; thu hồi token hiện tại, xoá cookie |
| `POST /api/auth/logout-all` | Bearer | 204; đổi security stamp, thu hồi mọi token |
| `GET /api/me` | Bearer | Hồ sơ (không có hash, không có stamp) |
| `PUT /api/me/password` | Bearer | 204; thu hồi mọi phiên khác |

### 7. Ghi log
Không bao giờ log mật khẩu, token, cookie, header `Authorization`. Log sự kiện: đăng nhập thành công/thất bại
(user id nếu có, IP), khoá tài khoản, phát hiện dùng lại token, đổi mật khẩu — đầu vào cho `audit.audit_logs` và
bản tin bất thường sau này.

### 8. Gói thêm vào `server.csproj` (đều của Microsoft, trong ràng buộc)
- `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.34 — xác thực JWT
- `Microsoft.Extensions.Identity.Core` 8.0.34 — chỉ dùng `PasswordHasher<T>`
Ghim đúng phiên bản; bản 8.0.x mới nhất tại 23/11/2026 (gồm các bản vá của `Microsoft.IdentityModel.*`).

## Phương án đã cân nhắc và loại
| Phương án | Vì sao không chọn |
|---|---|
| **Chỉ dùng cookie phiên** (cookie auth của ASP.NET Core, bảng session ở server) | Đơn giản hơn, JavaScript không bao giờ chạm vào token. Không chọn vì: API đã được tách subdomain để phục vụ cả app/đối tác (ADR-011), những client này dùng Bearer; mọi endpoint dùng cookie đều phải chống CSRF; một kiểu xác thực cho mọi client dễ kiểm thử hơn. **Đánh đổi chấp nhận:** XSS có thể đọc access token trong bộ nhớ — giới hạn thiệt hại bằng thời hạn 15 phút, refresh token vẫn nằm trong cookie HttpOnly |
| Lưu access token trong `localStorage` | XSS đọc được và token sống qua lần đóng trình duyệt; không có lợi ích gì so với bộ nhớ + refresh cookie |
| Refresh token dạng JWT tự chứa | Không thu hồi được nếu không có bảng; đằng nào cũng phải tra DB ⇒ chuỗi ngẫu nhiên + hash đơn giản và an toàn hơn |
| Nguyên bộ ASP.NET Core Identity (`AddIdentity`, EF stores) | Tạo ~7 bảng `AspNet*` ngoài quy ước snake_case/schema, kèm UI và cookie auth không dùng; chỉ cần `PasswordHasher` |
| Ký RS256/ES256 | Chỉ cần khi bên khác tự kiểm tra token bằng public key. Hiện chỉ API tự ký và tự kiểm tra ⇒ HS256 đủ, ít khâu quản lý khoá hơn. Đổi sang bất đối xứng khi có dịch vụ thứ hai cần xác minh |
| Dịch vụ đăng nhập bên ngoài (Auth0, Keycloak, Google…) | Thêm nhà cung cấp/hạ tầng, dữ liệu người dùng ra ngoài; vượt nhu cầu hiện tại. Đăng nhập Google có thể thêm sau như một cách đăng nhập bổ sung |
| Kiểm tra DB ở mọi request (kể cả Customer) | Thu hồi tức thì nhưng thêm một truy vấn cho mọi request; chỉ đáng với Admin |

## Hệ quả
- Thêm 2 gói Microsoft; 1 secret mới `AUTH_SIGNING_KEY` (GitHub Secrets → `.env` → `Auth__SigningKeys__0`).
- CORS giữ `AllowCredentials` với đúng một origin (đã có); client gọi `/api/auth/*` với `credentials: 'include'`.
- Rate limit và cache policy Admin nằm trong bộ nhớ ⇒ khi chạy nhiều instance API phải chuyển sang kho chung.
- Xoay khoá ký: thêm khoá mới vào vị trí 0, chuyển khoá cũ xuống 1, sau 15 phút xoá khoá cũ — không ai bị đăng xuất.
- Đổi `security_stamp` vô hiệu mọi refresh token ngay; access token Customer còn hiệu lực tối đa 15 phút.
- **Bổ sung thiết kế DB v1.0** (`identity.refresh_tokens`): thêm cột `session_expires_at timestamptz NOT NULL`
  (chép từ token đầu tiên của họ, = thời điểm đăng nhập + 30 ngày) để kiểm tra hạn tuyệt đối không cần truy vấn cả họ.
  Access token mang `sst` ⇒ `identity.users.security_stamp` được đọc ở policy Admin.
- Job nền hằng ngày dọn `refresh_tokens` hết hạn quá 30 ngày (theo lô 1.000 dòng).

## Kiểm thử bắt buộc (PR-7, PR-8)
1. Email trùng khác hoa thường bị UNIQUE (citext) từ chối; CHECK role/status/độ dài hash token.
2. Đăng nhập sai 5 lần ⇒ 423 trong 15 phút; đúng ⇒ bộ đếm về 0; email lạ và mật khẩu sai trả cùng lỗi.
3. Refresh xoay vòng: token cũ dùng lại sau 10 giây ⇒ 401 và **cả họ** bị thu hồi; trong 10 giây ⇒ 409, họ còn nguyên.
4. Refresh/logout không có `Origin` hợp lệ ⇒ 403; cookie có đủ `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api/auth`.
5. Customer gọi endpoint Admin ⇒ 403; khách ⇒ 401; admin bị khoá ⇒ 403 ở request kế tiếp (không chờ 15 phút).
6. Endpoint mới không ghi `[AllowAnonymous]` ⇒ mặc định 401 (test kiến trúc).
7. DB không có mật khẩu/refresh token dạng rõ; log không chứa token (test bắt log).
8. Rate limit: request thứ 11 trong một phút tới `/login` ⇒ 429 có `Retry-After`; IP lấy đúng sau Nginx.
9. API không khởi động khi thiếu khoá ký hoặc khoá < 32 byte.
