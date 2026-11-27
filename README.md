# DxTech_server

API ASP.NET Core 8 + EF Core 8 (PostgreSQL) cho DXTechs.

## Chạy local

```bash
docker compose up -d                       # PostgreSQL 15 tại localhost:5432
dotnet tool restore                        # cài dotnet-ef 8.0.8 theo .config/dotnet-tools.json
dotnet run --project server/server.csproj --launch-profile http
# Swagger: http://localhost:5043/swagger
```

> .NET 8 SDK không đọc được `server.slnx` trên dòng lệnh: luôn build/run qua `server/server.csproj`
> (Visual Studio 2022 17.13+ mở được `.slnx`).

## Cấu trúc (ADR-001)

```
server/
├─ Program.cs                     chỉ đăng ký hạ tầng + AddXxxModule(); không chứa logic
├─ Shared/                        kernel, không phụ thuộc module/hạ tầng (test kiến trúc kiểm tra)
│  ├─ Results/                    Result, Result<T>, Error, ErrorType           (ADR-004)
│  ├─ Paging/                     PageRequest, PagedResult<T>
│  ├─ Ids/UuidV7.cs               khoá chính UUID v7                            (ADR-006)
│  └─ Abstractions/IAuditable.cs  created_at / updated_at
├─ Infrastructure/
│  ├─ Persistence/                AppDbContext, SnakeCaseNamingConvention, AuditableInterceptor
│  ├─ Http/ResultHttpExtensions   Result → 200/201/204 hoặc ProblemDetails
│  └─ Options/                    cấu hình có kiểm tra khi khởi động (ValidateOnStart)
└─ Modules/<Module>/              Contracts/ (public) · Domain/ · Persistence/ · Features/ · Api/
tests/
├─ server.UnitTests/              Shared, convention, interceptor, quy tắc kiến trúc
└─ server.IntegrationTests/       API thật + PostgreSQL thật (WebApplicationFactory)
docs/adr/                         ADR-001 … ADR-010
```

Quy ước trong module:
- Entity, handler, cấu hình EF: `internal`. Chỉ `Contracts/`, `Api/` (controller) và lớp `XxxModule` là `public`.
- Module A không dùng type của module B ngoài `Modules/B/Contracts`.
- Bảng luôn đặt tên tường minh, số nhiều, có schema: `ToTable("products", CatalogModule.Schema)`.
  Cột, khoá, index tự sinh theo snake_case.
- Use case trả `Result<T>`; controller gọi `this.ToActionResult(result)`.

## Test

```bash
docker compose up -d                       # PostgreSQL cho integration test
dotnet test tests/server.UnitTests/server.UnitTests.csproj
dotnet test tests/server.IntegrationTests/server.IntegrationTests.csproj
```

Integration test dùng PostgreSQL thật (ADR-005). Mặc định trỏ tới database của `docker compose`
(`appdb` / `appuser`); đổi bằng biến môi trường `TEST_CONNECTION_STRING`. Trên CI, GitHub Actions
chạy một container `postgres:15` cho mỗi lần build.

## Địa chỉ production (ADR-011)

| Thành phần | URL |
|---|---|
| Web (client) | `https://dxtechs.com` |
| API | `https://api.dxtechs.com/api/...` |
| Kiểm tra sức khoẻ | `https://api.dxtechs.com/api/health`, `https://api.dxtechs.com/api/health/ready` |

API chỉ nghe `127.0.0.1:8080` trên VPS, Nginx chuyển tiếp từ `api.dxtechs.com`. Gọi trực tiếp để debug qua SSH tunnel:
`ssh -N -L 8080:127.0.0.1:8080 dxtechs` rồi `curl http://localhost:8080/api/health/ready`.

## CORS

Web và API khác origin nên trình duyệt cần CORS. Danh sách origin được phép nằm ở `Cors:AllowedOrigins`
(`appsettings.json`: `https://dxtechs.com`; `appsettings.Development.json`: `http://localhost:5173`, `http://localhost:4173`).
Ghi đè trên VPS bằng biến môi trường `Cors__AllowedOrigins__0=...`. Cấm `*`, cấm đường dẫn hoặc `/` cuối,
cấm `http://` ngoài localhost — sai cấu hình thì API không khởi động.

## Health check

| Endpoint | Ý nghĩa | Dùng cho |
|---|---|---|
| `GET /api/health` | Process còn sống (không chạm DB) | Uptime monitor |
| `GET /api/health/ready` | API chạy **và** kết nối được PostgreSQL (503 nếu DB lỗi) | Bước kiểm tra sau deploy của CI |

## API Catalog (PR-4)

| Endpoint | Mô tả |
|---|---|
| `GET /api/categories` | Cây danh mục đang hiển thị |
| `GET /api/products?category=&platform=&licenseType=&minPrice=&maxPrice=&q=&sort=&page=&pageSize=` | Danh sách sản phẩm đang bán (giá, giá gốc, tên gói đều của gói rẻ nhất); `category` gồm danh mục con; `sort` = `newest` (mặc định) \| `price_asc` \| `price_desc` \| `relevance` (khi có `q`); `pageSize` mặc định 12, tối đa 100 |
| `GET /api/products/search?q=` | Tìm không dấu, khớp tiền tố, xếp theo độ liên quan; nhận cùng bộ lọc |
| `GET /api/products/{slug}` | Chi tiết + gói đang bán + breadcrumb; sản phẩm ẩn/đã xoá ⇒ 404 `catalog.product_not_found` |

Danh sách trả `{ items, page, pageSize, total, totalPages }`. Lỗi là ProblemDetails có `code`
(`catalog.invalid_query` kèm `errors` theo từng tham số, `catalog.category_not_found`, `catalog.search_query_required`).
Thiết kế và phân tích schema: [`docs/database/catalog-schema.md`](docs/database/catalog-schema.md).

## Dữ liệu demo (PR-5, ADR-010)

```bash
# Máy dev
dotnet run --project server/server.csproj -- --seed-demo --dry-run   # xem trước, không ghi
dotnet run --project server/server.csproj -- --seed-demo

# VPS (user deploy, trong ~/dxtechs-server) — chạy bằng image API đang chạy, role dxtechs_app
C="docker compose -p dxtechs-server --env-file .env -f src/docker-compose.prod.yml"
export API_TAG=$(docker inspect -f '{{.Config.Image}}' dxtechs-server-api-1 | cut -d: -f2)
$C run --rm --no-deps api --seed-demo --dry-run
$C run --rm --no-deps api --seed-demo
```

Chỉ thêm danh mục/sản phẩm còn thiếu (theo slug); không ghi đè chỉnh sửa của admin, không khôi phục sản phẩm đã xoá.
Không bao giờ tự chạy khi API khởi động.

## Xác thực (ADR-012, PR-8)

| Method · Path | Xác thực | Kết quả |
|---|---|---|
| `POST /api/auth/register` `{email, password, fullName}` | — | 201 `{accessToken, tokenType, expiresAt, user}` + cookie `__Secure-dxt_rt` |
| `POST /api/auth/login` `{email, password}` | — | 200 như trên · 401 `auth.invalid_credentials` · 423 `auth.locked` |
| `POST /api/auth/refresh` | cookie + header `Origin` | 200 token mới + cookie mới · 401 · 409 `auth.refresh_conflict` (thử lại) |
| `POST /api/auth/logout` | cookie + `Origin` | 204, xoá cookie |
| `GET /api/me` | Bearer | hồ sơ |
| `PUT /api/me/password` `{currentPassword, newPassword}` | Bearer | 200 phiên mới; mọi phiên khác bị thu hồi |
| `POST /api/me/logout-all` | Bearer | 204 |
| `GET /api/admin/ping` | Bearer, policy Admin | 200 / 403 |

Client gọi `/api/auth/*` với `credentials: 'include'`. Access token sống 15 phút, giữ trong bộ nhớ (không `localStorage`).
Production cần `AUTH_SIGNING_KEY` trong `~/dxtechs-server/.env` (tạo: `openssl rand -base64 48`) — thiếu thì deploy dừng
trước khi thay container. Xoay khoá: chuyển khoá cũ sang `AUTH_SIGNING_KEY_PREVIOUS`, đặt khoá mới, deploy; sau 15 phút xoá khoá cũ.

## Tạo tài khoản admin (`--create-admin`, ADR-012)

Không có mật khẩu mặc định. Mật khẩu **nhập từ bàn phím** (ẩn ký tự, hỏi 2 lần) — không bao giờ đưa vào tham số
dòng lệnh (lộ trong shell history và `ps`). Email đã có tài khoản ⇒ lỗi, không tự nâng quyền.

```bash
# VPS (user deploy, trong ~/dxtechs-server) — biến C và API_TAG như mục trên
$C run --rm --no-deps api --create-admin admin@dxtechs.com --name "Nguyễn Đình Nam"
# Mật khẩu admin (≥ 10 ký tự): ********
# Nhập lại mật khẩu: ********
# create-admin: đã tạo admin admin@dxtechs.com (id 01a2…).
```

Quy tắc mật khẩu (NIST SP 800-63B): 10–128 ký tự, cho phép dấu tiếng Việt và khoảng trắng, không bắt buộc ký tự
đặc biệt; từ chối mật khẩu phổ biến và mật khẩu chứa phần trước `@` của email. Mã thoát: 0 thành công, 1 lỗi, 2 sai cú pháp.

## Đơn hàng & giỏ hàng (Phase 3: PR-10, PR-11)

| Method · Path | Xác thực | Kết quả |
|---|---|---|
| `POST /api/cart/quote` | — (60/phút/IP) | Báo giá: giá, tồn, tổng do server tính; trạng thái từng dòng |
| `POST /api/orders` + header `Idempotency-Key` | Bearer (10/giờ/IP) | 201 đơn mới + hướng dẫn chuyển khoản VietQR (hạn 30 phút) · 200 nếu gửi lại cùng khoá · 409 hết hàng / quá 3 đơn chờ · 422 chưa cấu hình tài khoản nhận tiền |
| `GET /api/orders/{code}` | Bearer, chủ đơn | Chi tiết + dòng thời gian; key rõ khi đơn `fulfilled`; đơn người khác ⇒ 404 |
| `POST /api/orders/{code}/cancel` | Bearer, chủ đơn | Huỷ đơn chờ thanh toán, key về kho |
| `GET /api/me/orders?cursor=` | Bearer | “Đơn của tôi”, mới nhất trước |

Job nền `OrderExpiry` chạy mỗi phút: huỷ đơn quá hạn (lý do `expired`), trả key về kho.

## License key (ADR-014, PR-11)

**Khoá mã hoá** — bắt buộc trong `.env` trước khi deploy PR-11 (thiếu ⇒ `docker compose` dừng trước khi thay container):
```bash
cd ~/dxtechs-server && cp .env .env.bak-$(date +%F)
printf '\nLICENSE_ENC_KEY_V1=%s\nLICENSE_HMAC_KEY=%s\n' "$(openssl rand -base64 32)" "$(openssl rand -base64 32)" >> .env
grep -c '^LICENSE_' .env      # 2 — KHÔNG cat .env
```
Khoá **không** nằm trong backup DB: chép hai giá trị sang **2 nơi tách biệt** (trình quản lý mật khẩu + nơi công ty chỉ định)
trước khi nhập key thật. Mất khoá = mọi key trong kho không giải mã được.

**Nhập key** (mỗi dòng một key; dòng trống và dòng `#` bị bỏ qua; trùng được đếm và bỏ qua; không in key):
```bash
$C run --rm --no-deps -T api --import-keys <SKU> --note "Hoá đơn NPP số …" --dry-run < keys.txt   # thử
$C run --rm --no-deps -T api --import-keys <SKU> --note "Hoá đơn NPP số …" < keys.txt             # thật
shred -u keys.txt
```

**Tài khoản nhận tiền** (công khai, tuỳ chọn tới khi có tài khoản): `PAYMENTS_BANK_BIN`, `PAYMENTS_BANK_NAME`,
`PAYMENTS_ACCOUNT_NUMBER`, `PAYMENTS_ACCOUNT_NAME` trong `.env`. Để trống ⇒ `POST /api/orders` trả 422 `sales.ordering_unavailable`.

## Quy ước route

Mọi controller dùng tiền tố `api/`, ví dụ `[Route("api/[controller]")]`. Nginx trên VPS chỉ chuyển
`/api/*` vào container, nên route thiếu tiền tố sẽ không gọi được từ trình duyệt.

## Migration (EF Core)

```bash
dotnet tool run dotnet-ef migrations add <Module>_<TenMigration> --project server/server.csproj \
  --output-dir Infrastructure/Persistence/Migrations
dotnet tool run dotnet-ef database update --project server/server.csproj   # áp vào DB local
```

Commit cả thư mục `server/Infrastructure/Persistence/Migrations/` và đính kèm SQL
(`dotnet tool run dotnet-ef migrations script --project server/server.csproj`) vào PR để review. Khi deploy, service `migrate` trong `docker-compose.prod.yml`
chạy migrations bundle **trước** khi khởi động API; migration lỗi thì API cũ vẫn chạy và job deploy báo đỏ.

File SQL để review đặt ở `docs/migrations/<tên migration>.sql`
(`dotnet tool run dotnet-ef migrations script <trước> <sau> --project server/server.csproj -o docs/migrations/...`).

Quy tắc: migration phải tương thích ngược với code đang chạy (thêm cột nullable/bảng mới trước,
xoá/đổi tên cột ở một lần deploy sau), vì API cũ vẫn phục vụ cho tới khi API mới sẵn sàng.

## Role database (ADR-009)

| Role | Dùng bởi | Quyền |
|---|---|---|
| `POSTGRES_USER` | postgres, `db-roles`, backup/admin | superuser |
| `dxtechs_owner` | `migrate` | chủ database, tạo/sửa schema và bảng |
| `dxtechs_app` | `api` | chỉ SELECT/INSERT/UPDATE/DELETE |

Service `db-roles` chạy `deploy/postgres/roles.sql` (idempotent) trước `migrate` ở mọi lần deploy.
`.env` trên VPS cần thêm `DB_OWNER_PASSWORD` và `DB_APP_PASSWORD` (`openssl rand -hex 24`).
Migration **không** được chứa lệnh cần superuser (ví dụ `CREATE EXTENSION`) — thêm vào `roles.sql`.
Kiểm thử: `PGHOST=... PGUSER=<superuser> PGPASSWORD=... .github/scripts/test-db-roles.sh`.

## Deploy & rollback

Merge vào `main` → GitHub Actions build, tải mã nguồn lên VPS và chạy
`docker compose -f docker-compose.prod.yml up -d --build` với image gắn tag = 7 ký tự đầu của commit.
VPS giữ 5 tag gần nhất của mỗi image.

Rollback API về một bản trước (trên VPS, user `deploy`):

```bash
cd ~/dxtechs-server
docker image ls dxtechs-api                     # xem các tag còn giữ
API_TAG=<tag> docker compose -p dxtechs-server --env-file .env \
  -f src/docker-compose.prod.yml up -d --no-build api
```

Rollback chỉ đổi code API, **không** đảo ngược migration đã chạy.
