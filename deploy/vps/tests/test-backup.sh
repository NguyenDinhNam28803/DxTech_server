#!/usr/bin/env bash
# Kiểm thử deploy/vps/backup/backup.sh không cần Docker: PostgreSQL giả (fakepg), cặp khoá GPG tạo tạm,
# máy chủ S3 giả (http, ghi lại request). Chạy được trong CI và trên máy dev (không chạy bằng root).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../backup" && pwd)"
W="$(mktemp -d)"
SRV_PID=""
cleanup() { [ -n "$SRV_PID" ] && kill "$SRV_PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT
pass() { printf '  \033[32m✔\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✘ %s\033[0m\n' "$*"; exit 1; }

if [ "$(id -u)" -eq 0 ]; then
  echo "test-backup.sh: bỏ qua khi chạy bằng root (script từ chối root theo thiết kế)"; exit 0
fi

# ---------- PostgreSQL giả: nhận đúng dạng lệnh backup.sh gửi vào container ----------
cat >"$W/fakepg" <<'EOF'
#!/usr/bin/env bash
# Dạng gọi: fakepg sh -c "<lệnh>"  hoặc  fakepg pg_restore --list
echo "$*" >>"$FAKEPG_LOG"
if [ "$1" = pg_restore ]; then
  cat >/dev/null
  [ "${FAKEPG_TABLES:-4}" -gt 0 ] && for i in $(seq 1 "${FAKEPG_TABLES:-4}"); do echo "33$i; 0 1639$i TABLE DATA catalog t$i owner"; done
  exit 0
fi
cmd="$3"
case "$cmd" in
  *pg_dump*)    [ -n "${FAKEPG_DUMP_FAIL:-}" ] && { echo "pg_dump: error: connection refused" >&2; exit 1; }
                printf 'PGDMP'; head -c 4096 /dev/urandom ;;
  *pg_isready*) exit 0 ;;
  *createdb*|*dropdb*) exit 0 ;;
  *pg_restore*) cat >/dev/null ;;
  *psql*)       echo "1 22 16 16" ;;
  *)            echo "fakepg: lệnh lạ: $cmd" >&2; exit 9 ;;
esac
EOF
chmod +x "$W/fakepg"

# ---------- Máy chủ S3 giả ----------
cat >"$W/fakes3.py" <<'EOF'
import http.server, os, sys
out = sys.argv[1]
class H(http.server.BaseHTTPRequestHandler):
    def do_PUT(self):
        n = int(self.headers["Content-Length"]); body = self.rfile.read(n)
        name = self.path.strip("/").replace("/", "__")
        open(os.path.join(out, name), "wb").write(body)
        open(os.path.join(out, "requests.log"), "a").write(f"PUT {self.path} {self.headers.get('Authorization','')[:40]}\n")
        self.send_response(200); self.end_headers()
    def do_GET(self):
        open(os.path.join(out, "requests.log"), "a").write(f"GET {self.path}\n")
        self.send_response(200); self.end_headers()
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", 0), H)
open(os.path.join(out, "port"), "w").write(str(s.server_port))
s.serve_forever()
EOF
mkdir -p "$W/s3"
python3 "$W/fakes3.py" "$W/s3" & SRV_PID=$!
for _ in $(seq 50); do [ -f "$W/s3/port" ] && break; sleep 0.1; done
PORT="$(cat "$W/s3/port")"

# ---------- Cặp khoá GPG thử (private key chỉ nằm trong thư mục test) ----------
export GNUPGHOME="$W/gnupg-private"; mkdir -m 700 "$GNUPGHOME"
gpg --batch --quiet --passphrase '' --quick-gen-key 'DXTechs Backup Test <backup-test@example.invalid>' default default never 2>/dev/null
mkdir -p "$W/cfg"
gpg --batch --armor --export 'backup-test@example.invalid' >"$W/cfg/recipient.asc"
decrypt() { gpg --batch --quiet --decrypt "$1" 2>/dev/null; }

export FAKEPG_LOG="$W/fakepg.log" BACKUP_PG_EXEC="$W/fakepg"
newcfg() { # newcfg <OFFSITE> [extra lines...]
  # MEDIA_DIR mặc định trỏ vào thư mục không tồn tại ⇒ các kiểm thử cũ không phụ thuộc máy chạy (mục 10 kiểm thử ảnh).
  { echo "BACKUP_DIR=$W/backups"; echo "OFFSITE=$1"; echo "GPG_RECIPIENT_FILE=$W/cfg/recipient.asc"; echo "MEDIA_DIR=$W/no-media"; shift; printf '%s\n' "$@"; } >"$W/cfg/backup.env"
  chmod 600 "$W/cfg/backup.env"
}
export BACKUP_CONFIG="$W/cfg/backup.env"
B="$ROOT/backup.sh"

echo "1. Backup + bản sao thư mục (OFFSITE=dir)"
newcfg dir "OFFSITE_DIR=$W/offsite"
out="$("$B" 2>&1)" || fail "backup lỗi: $out"
dump="$(ls "$W"/backups/dxtechs-*.dump)"
[ "$(head -c 5 "$dump")" = PGDMP ] || fail "dump không đúng định dạng"
(cd "$W/backups" && sha256sum -c --quiet "$(basename "$dump").sha256") || fail "sha256 sai"
enc="$(find "$W/offsite" -name '*.dump.gpg')"
[ -n "$enc" ] || fail "không có bản .gpg ở thư mục ngoài"
case "$enc" in */dxtechs/20[0-9][0-9]/[01][0-9]/dxtechs-*.dump.gpg) ;; *) fail "đường dẫn sai: $enc" ;; esac
cmp -s <(decrypt "$enc") "$dump" || fail "giải mã không ra đúng dump"
! grep -q PGDMP "$enc" || fail "bản ngoài VPS không được mã hoá"
compgen -G "$W/backups/.enc.*" >/dev/null && fail "còn file mã hoá tạm"
grep -q 'LOCAL OK' <<<"$out" && grep -q 'OFFSITE OK dir:' <<<"$out" || fail "log thiếu dòng OK: $out"
pass "dump hợp lệ, SHA-256 đúng, bản ngoài mã hoá và giải mã khớp, không còn file tạm"

echo "2. Bản sao lên kho S3 (máy chủ giả)"
rm -rf "$W/backups"
newcfg s3 "S3_ENDPOINT=http://127.0.0.1:$PORT" "S3_ALLOW_HTTP=1" "S3_REGION=ap-southeast-1" "S3_BUCKET=bk" \
  "S3_ACCESS_KEY=AKTEST" "S3_SECRET_KEY=secret-test-value" "BACKUP_PING_URL=http://127.0.0.1:$PORT/ping/ok"
out="$("$B" 2>&1)" || fail "backup lỗi: $out"
obj="$(ls "$W"/s3/bk__dxtechs__*)"
cmp -s <(decrypt "$obj") "$(ls "$W"/backups/dxtechs-*.dump)" || fail "object trên S3 giải mã không khớp"
grep -q 'PUT /bk/dxtechs/.*\.dump\.gpg AWS4-HMAC-SHA256 Credential=AKTEST' "$W/s3/requests.log" || fail "request PUT sai"
grep -q 'GET /ping/ok' "$W/s3/requests.log" || fail "không gọi ping thành công"
grep -q 'secret-test-value' <<<"$out" && fail "lộ secret trong log"
pass "PUT đúng đường dẫn + chữ ký, giải mã khớp, có ping OK, log không lộ secret"

echo "3. pg_dump lỗi ⇒ thoát ≠ 0, không để lại file, gọi ping lỗi"
rm -rf "$W/backups"
newcfg dir "OFFSITE_DIR=$W/offsite" "BACKUP_PING_FAIL_URL=http://127.0.0.1:$PORT/ping/fail"
if FAKEPG_DUMP_FAIL=1 "$B" >"$W/o3" 2>&1; then fail "phải thất bại"; fi
grep -q 'FAIL pg_dump lỗi' "$W/o3" || fail "thiếu thông báo lỗi: $(cat "$W/o3")"
compgen -G "$W/backups/*dump*" >/dev/null && fail "còn file dump/tmp sau lỗi"
grep -q 'GET /ping/fail' "$W/s3/requests.log" || fail "không gọi ping lỗi"
pass "lỗi được báo, không có backup giả, có cảnh báo"

echo "4. Dump thiếu bảng (bị cắt cụt) ⇒ từ chối"
if FAKEPG_TABLES=1 "$B" >"$W/o4" 2>&1; then fail "phải thất bại"; fi
grep -q 'Dump chỉ có 1 bảng' "$W/o4" || fail "$(cat "$W/o4")"
pass "dump < MIN_TABLES bị loại"

echo "5. Cấu hình đọc được bởi người khác ⇒ từ chối"
chmod 644 "$W/cfg/backup.env"
if "$B" >"$W/o5" 2>&1; then fail "phải từ chối"; fi
grep -q 'chmod 600' "$W/o5" || fail "$(cat "$W/o5")"
chmod 600 "$W/cfg/backup.env"
pass "file cấu hình 644 bị từ chối"

echo "6. Private key đặt nhầm trên VPS ⇒ từ chối"
cp "$W/cfg/recipient.asc" "$W/cfg/recipient.bak"
gpg --batch --armor --pinentry-mode loopback --passphrase '' --export-secret-keys 'backup-test@example.invalid' >"$W/cfg/recipient.asc"
if "$B" >"$W/o6" 2>&1; then fail "phải từ chối"; fi
grep -q 'chứa PRIVATE key' "$W/o6" || fail "$(cat "$W/o6")"
mv "$W/cfg/recipient.bak" "$W/cfg/recipient.asc"
pass "phát hiện private key"

echo "7. Giữ KEEP_DAYS ngày"
touch -d '10 days ago' "$W/backups/dxtechs-20000101-000000.dump" "$W/backups/dxtechs-20000101-000000.dump.sha256"
"$B" >/dev/null 2>&1 || fail "backup lỗi"
[ ! -e "$W/backups/dxtechs-20000101-000000.dump" ] && [ ! -e "$W/backups/dxtechs-20000101-000000.dump.sha256" ] || fail "không xoá bản cũ"
[ "$(ls "$W"/backups/dxtechs-*.dump | wc -l)" -ge 1 ] || fail "xoá nhầm bản mới"
pass "bản > 7 ngày bị xoá, bản mới giữ lại"

echo "8. Thử khôi phục"
out="$("$B" --restore-test 2>&1)" || fail "$out"
grep -q 'RESTORE-TEST OK .* migrations=1 categories=22 products=16 variants=16' <<<"$out" || fail "$out"
grep -q 'dropdb' "$FAKEPG_LOG" || fail "không xoá database tạm"
echo "tampered" >>"$(ls -t "$W"/backups/dxtechs-*.dump | head -1)"
if "$B" --restore-test >"$W/o8" 2>&1; then fail "file bị sửa phải bị từ chối"; fi
grep -q 'SHA-256 không khớp' "$W/o8" || fail "$(cat "$W/o8")"
pass "khôi phục + đếm dữ liệu + xoá DB tạm; file bị sửa bị phát hiện"

echo "9. --check tải file thử và --install thay cron cũ"
newcfg dir "OFFSITE_DIR=$W/offsite"
out="$("$B" --check 2>&1)" || fail "$out"
chk="$(find "$W/offsite/dxtechs/_check" -name '*.txt.gpg')"
decrypt "$chk" | grep -q 'dxtechs backup check' || fail "file kiểm tra không giải mã được"
cat >"$W/crontab" <<'EOF'
#!/usr/bin/env bash
f="$CRONTAB_FILE"
if [ "$1" = -l ]; then cat "$f"; else cp "$1" "$f"; fi
EOF
chmod +x "$W/crontab"
export CRONTAB_FILE="$W/crontab.txt"
printf '%s\n' '0 4 * * 0 /usr/bin/other-job' "15 2 * * * $HOME/backup-db.sh >> $HOME/backups/backup.log 2>&1" >"$CRONTAB_FILE"
HOME="$W/home" BACKUP_CRONTAB="$W/crontab" BACKUP_CONFIG="$W/home/.config/dxtechs-backup/backup.env" "$B" --install >/dev/null 2>&1 \
  || HOME="$W/home" BACKUP_CRONTAB="$W/crontab" BACKUP_CONFIG="$W/home/.config/dxtechs-backup/backup.env" "$B" --install
grep -q 'other-job' "$CRONTAB_FILE" || fail "mất cron khác"
grep -q 'backup-db.sh' "$CRONTAB_FILE" && fail "còn dòng backup-db.sh cũ"
[ "$(grep -c 'dxtechs-backup/backup.sh' "$CRONTAB_FILE")" -eq 2 ] || fail "cron mới sai: $(cat "$CRONTAB_FILE")"
[ -x "$W/home/bin/dxtechs-backup/backup.sh" ] && [ "$(stat -c %a "$W/home/.config/dxtechs-backup/backup.env")" = 600 ] || fail "cài đặt thiếu"
HOME="$W/home" BACKUP_CRONTAB="$W/crontab" BACKUP_CONFIG="$W/home/.config/dxtechs-backup/backup.env" "$B" --install >/dev/null 2>&1
[ "$(grep -c 'dxtechs-backup/backup.sh' "$CRONTAB_FILE")" -eq 2 ] || fail "chạy lại --install bị nhân đôi cron"
pass "--check OK; --install giữ cron khác, bỏ dòng cũ, chạy lại không nhân đôi"

echo "10. Ảnh (CT-3b): tar mã hoá ra kho ngoài, bỏ file tạm, không để lại bản trên VPS"
rm -rf "$W/backups" "$W/offsite"
mkdir -p "$W/media/2026/11"
printf 'png-1' >"$W/media/2026/11/0123456789abcdef0123456789abcdef.png"
printf 'jpg-2' >"$W/media/2026/11/fedcba9876543210fedcba9876543210.jpg"
printf 'dang-ghi' >"$W/media/.upload-123.tmp"
newcfg dir "OFFSITE_DIR=$W/offsite" "MEDIA_DIR=$W/media"
out="$("$B" 2>&1)" || fail "backup lỗi: $out"
menc="$(find "$W/offsite" -name 'dxtechs-media-*.tar.gpg')"
[ -n "$menc" ] || fail "không có bản ảnh ở kho ngoài: $out"
listing="$(decrypt "$menc" | tar -tf -)"
grep -q 'media/2026/11/0123456789abcdef0123456789abcdef.png' <<<"$listing" || fail "thiếu ảnh trong tar: $listing"
grep -q 'upload-123' <<<"$listing" && fail "tar chứa file tạm đang ghi"
! grep -q 'png-1' "$menc" || fail "bản ảnh ngoài VPS không được mã hoá"
compgen -G "$W/backups/.media*" >/dev/null && fail "còn file ảnh tạm trên VPS"
grep -q 'MEDIA OK dir:.* 2 file' <<<"$out" || fail "log thiếu MEDIA OK: $out"
rm -rf "$W/media"/*; newcfg dir "OFFSITE_DIR=$W/offsite" "MEDIA_DIR=$W/media"
out="$("$B" 2>&1)" || fail "backup lỗi khi thư mục ảnh rỗng: $out"
grep -q 'MEDIA SKIP' <<<"$out" || fail "thư mục rỗng phải bỏ qua: $out"
pass "ảnh được tar + mã hoá + giải mã khớp; file tạm bị loại; thư mục rỗng bỏ qua"

echo "Tất cả kiểm thử backup: OK"
