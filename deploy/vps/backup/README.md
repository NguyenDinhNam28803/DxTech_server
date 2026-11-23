# Backup PostgreSQL + bản sao mã hoá ngoài VPS

Thay `~/backup-db.sh` (PR-0, chỉ lưu trên VPS) bằng script có trong repo, có test trong CI.
Đóng điều kiện nghiệm thu Phase 1 số 1: *“bản sao ngoài VPS, không làm tay”*.

```
02:15 cron (user deploy)
  pg_dump -Fc trong container ─► .tmp ─► pg_restore --list (đủ bảng?) ─► đổi tên ─► SHA-256
                                                                   │
                         giữ 7 ngày trên VPS ◄─────────────────────┤
                                                                   ▼
                              gpg --encrypt (chỉ PUBLIC key) ─► kho S3 ngoài VPS (khoá CHỈ GHI)
03:40 ngày 1 hằng tháng: --restore-test (khôi phục bản mới nhất vào DB tạm, đếm dữ liệu, xoá DB tạm)
```

| Quyết định | Lý do | Đánh đổi |
|---|---|---|
| Mã hoá GPG bằng **public key**; private key chỉ nằm ở máy Backend Lead | Kẻ chiếm VPS hoặc lộ khoá kho S3 vẫn không đọc được backup đã gửi đi | Mất private key = mất mọi bản ngoài VPS ⇒ phải cất private key ở **2 nơi** |
| Khoá S3 **chỉ được ghi** (PutObject), không đọc/xoá; xoá cũ bằng *lifecycle rule* của kho | Kẻ chiếm VPS không xoá được backup (kiểu ransomware xoá cả bản sao) | Script không tự dọn kho ngoài; tải về bằng tài khoản khác |
| Tự ký AWS SigV4 bằng Python chuẩn (`s3put.py`) thay vì cài awscli/rclone | Không thêm phần mềm lên VPS; kiểm thử với vector chính thức của AWS | Chỉ hỗ trợ PUT một object (đủ cho backup ≤ vài trăm MB) |
| Kiểm tra dump bằng `pg_restore --list` trước khi coi là backup | Chặn file bị cắt cụt mà `pg_dump` không báo lỗi | Tốn ~1 giây |
| Ping “dead man’s switch” (tuỳ chọn) | Biết khi backup **không chạy**, không chỉ khi chạy lỗi | Cần một dịch vụ giám sát bên ngoài |

**Nên chọn kho S3 đặt tại Việt Nam** (nhà cung cấp cloud trong nước có dịch vụ tương thích S3): backup chứa dữ liệu khách hàng từ Phase 2 — lưu ở nước ngoài là chuyển dữ liệu cá nhân ra nước ngoài (xem báo cáo AI, mục 18). Kho cần hỗ trợ path-style URL và chữ ký V4 (hầu hết đều hỗ trợ).

## Bước 1 — Tạo cặp khoá GPG (máy Windows, Git Bash) — làm một lần

```bash
gpg --quick-gen-key "DXTechs Backup <backup@dxtechs.com>" default default never   # đặt passphrase mạnh
gpg --armor --export backup@dxtechs.com > dxtechs-backup.pub.asc                   # PUBLIC — sẽ đưa lên VPS
gpg --armor --export-secret-keys backup@dxtechs.com > dxtechs-backup.SECRET.asc    # PRIVATE — KHÔNG lên VPS
gpg --fingerprint backup@dxtechs.com                                                # ghi lại fingerprint
```

Cất `dxtechs-backup.SECRET.asc` + passphrase ở **hai nơi tách biệt** (ví dụ: trình quản lý mật khẩu + USB cất riêng).
Không gửi qua chat, không commit, không để trong thư mục đồng bộ công khai.

## Bước 2 — Tạo bucket và khoá chỉ-ghi ở nhà cung cấp S3

1. Tạo bucket riêng, ví dụ `dxtechs-backup`, **không public**.
2. Bật *versioning* (hoặc *object lock* nếu có) và *lifecycle*: xoá object sau 90 ngày.
3. Tạo access key chỉ có quyền `PutObject` trên `dxtechs-backup/dxtechs/*`. Nếu nhà cung cấp không cho phân quyền
   chi tiết, dùng key riêng cho bucket này (không dùng key chính của tài khoản).
4. Ghi lại: endpoint (`https://…`), region, tên bucket, access key, secret key.

## Bước 3 — Cài trên VPS (user `deploy`)

```bash
ssh dxtechs                                          # user deploy, KHÔNG phải root
bash ~/dxtechs-server/src/deploy/vps/backup/backup.sh --install
```

`--install` chép script vào `~/bin/dxtechs-backup/`, tạo `~/.config/dxtechs-backup/backup.env` (600) từ mẫu,
và thay dòng cron `backup-db.sh` cũ bằng 2 dòng mới. Dòng cron khác giữ nguyên; chạy lại không bị nhân đôi.

Đưa public key lên (từ Git Bash trên Windows):
```bash
scp -P 24700 -i ~/.ssh/dxtechs_deploy dxtechs-backup.pub.asc deploy@103.72.98.140:.config/dxtechs-backup/recipient.asc
```

Sửa cấu hình **trên VPS** (gõ trực tiếp, không dán secret vào chat):
```bash
nano ~/.config/dxtechs-backup/backup.env     # OFFSITE=s3, S3_ENDPOINT, S3_REGION, S3_BUCKET, S3_ACCESS_KEY, S3_SECRET_KEY
chmod 600 ~/.config/dxtechs-backup/backup.env
~/bin/dxtechs-backup/backup.sh --check        # in fingerprint public key + tải file thử lên kho
```
So fingerprint in ra với fingerprint ở bước 1 — phải trùng.

## Bước 4 — Chạy thật và xác nhận

```bash
~/bin/dxtechs-backup/backup.sh                # LOCAL OK … / OFFSITE OK s3:dxtechs/2026/11/…dump.gpg
~/bin/dxtechs-backup/backup.sh --restore-test # RESTORE-TEST OK … migrations=1 categories=22 products=16 variants=16
crontab -l
tail -n 5 ~/backups/backup.log                # sáng hôm sau: phải có dòng 02:15
```

**Bằng chứng nghiệm thu** (gửi Backend Lead, không có secret): kết quả `--check`, một dòng `OFFSITE OK`,
một dòng `RESTORE-TEST OK`, `crontab -l`, và `tail backup.log` có dòng 02:15 tự chạy.

## Bước 5 — Diễn tập khôi phục từ bản NGOÀI VPS (mỗi quý, trên máy Windows)

```bash
# Tải object …dump.gpg về (bằng tài khoản/console của nhà cung cấp — khoá trên VPS không đọc được)
gpg --decrypt dxtechs-YYYYMMDD-HHMMSS.dump.gpg > restore.dump      # hỏi passphrase
pg_restore --list restore.dump | grep -c 'TABLE DATA'               # phải ≥ 4
```
Khôi phục thật vào một PostgreSQL 15 sạch: `createdb` rồi `pg_restore --no-owner --no-acl -d <db> restore.dump`,
sau đó chạy `deploy/postgres/roles.sql` để tạo lại role và quyền (dump không chứa role).

## Chưa có kho S3?

- `OFFSITE=dir` + `OFFSITE_DIR=/mnt/...` nếu có ổ/máy khác mount được vào VPS.
- Tạm thời: kéo về Windows bằng Task Scheduler + `scp` (file `.dump` trên VPS **chưa mã hoá** — chỉ chấp nhận
  trước khi có dữ liệu khách hàng). Không tính là “tự động” cho nghiệm thu vì phụ thuộc máy Windows bật.

## Lỗi thường gặp

| Thông báo | Nguyên nhân · cách xử lý |
|---|---|
| `… đang có quyền 644 — chạy: chmod 600 …` | File cấu hình chứa khoá S3 nhưng người khác đọc được |
| `… chứa PRIVATE key` | Đặt nhầm private key lên VPS — xoá ngay, coi như khoá đã lộ: tạo cặp khoá mới |
| `kho trả HTTP 403` | Sai key/secret/region, hoặc key không có quyền PutObject vào đường dẫn `dxtechs/…` |
| `kho trả HTTP 400 … AuthorizationHeaderMalformed` | Sai `S3_REGION` |
| `Dump chỉ có N bảng` | Dump bị cắt cụt hoặc DB sai — kiểm tra `docker compose ps`, dung lượng đĩa |
| `Một lần backup khác đang chạy` | Lần trước chưa xong (khoá `~/backups/.lock`) |
