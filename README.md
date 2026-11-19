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

## Health check

| Endpoint | Ý nghĩa | Dùng cho |
|---|---|---|
| `GET /api/health` | Process còn sống (không chạm DB) | Uptime monitor |
| `GET /api/health/ready` | API chạy **và** kết nối được PostgreSQL (503 nếu DB lỗi) | Bước kiểm tra sau deploy của CI |

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

Quy tắc: migration phải tương thích ngược với code đang chạy (thêm cột nullable/bảng mới trước,
xoá/đổi tên cột ở một lần deploy sau), vì API cũ vẫn phục vụ cho tới khi API mới sẵn sàng.

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
