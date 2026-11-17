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
dotnet tool run dotnet-ef migrations add <TenMigration> --project server/server.csproj
dotnet tool run dotnet-ef database update --project server/server.csproj   # áp vào DB local
```

Commit cả thư mục `server/Migrations/`. Khi deploy, service `migrate` trong `docker-compose.prod.yml`
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
