#!/usr/bin/env bash
# Backup PostgreSQL production + bản sao MÃ HOÁ ra ngoài VPS. Chạy bằng user `deploy` (không phải root).
#
#   backup.sh                 Backup ngay (cron gọi lệnh này 02:15 hằng ngày)
#   backup.sh --check         Kiểm tra cấu hình, khoá GPG, quyền file; tải một file thử nhỏ ra kho ngoài
#   backup.sh --restore-test  Khôi phục bản mới nhất vào database tạm, đếm dữ liệu, xoá database tạm
#   backup.sh --install       Cài vào ~/bin/dxtechs-backup và thay dòng cron backup-db.sh cũ
#
# Luồng: pg_dump (trong container) → file .tmp → kiểm tra đọc được (pg_restore --list) → đổi tên
#        → SHA-256 → giữ KEEP_DAYS ngày trên VPS → mã hoá GPG bằng PUBLIC key → tải ra ngoài (S3 hoặc thư mục)
#        → thư mục ảnh (MEDIA_DIR): tar → mã hoá → tải ra ngoài (chỉ khi có kho ngoài)
# VPS chỉ giữ public key: kẻ chiếm VPS không giải mã được các bản đã gửi đi. Private key do Backend Lead giữ.
# Cấu hình: ~/.config/dxtechs-backup/backup.env (chmod 600) — mẫu: backup.env.example. Chi tiết: README.md.
set -Eeuo pipefail
umask 077   # dump, khoá, file tạm: chỉ chủ file đọc được

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG="${BACKUP_CONFIG:-$HOME/.config/dxtechs-backup/backup.env}"

log()  { printf '%s %s\n' "$(date -Is)" "$*"; }
die()  { log "FAIL $*" >&2; ping_fail; exit 1; }

ping_ok()   { [ -n "${BACKUP_PING_URL:-}" ] && curl -fsS -m 10 --retry 3 -o /dev/null "$BACKUP_PING_URL" || true; }
ping_fail() { [ -n "${BACKUP_PING_FAIL_URL:-}" ] && curl -fsS -m 10 --retry 3 -o /dev/null "$BACKUP_PING_FAIL_URL" || true; }

load_config() {
  [ "$(id -u)" -ne 0 ] || { echo "Chạy bằng user deploy, không phải root (file backup thuộc deploy)." >&2; exit 1; }
  if [ -f "$CONFIG" ]; then
    # Cấu hình có khoá S3 ⇒ chỉ chủ file được đọc.
    perm="$(stat -c %a "$CONFIG")"
    case "$perm" in 600|400) ;; *) echo "FAIL $CONFIG đang có quyền $perm — chạy: chmod 600 $CONFIG" >&2; exit 1 ;; esac
    set -a
    # shellcheck disable=SC1090
    . "$CONFIG"
    set +a
  fi
  BACKUP_DIR="${BACKUP_DIR:-$HOME/backups}"
  KEEP_DAYS="${KEEP_DAYS:-7}"
  OFFSITE="${OFFSITE:-none}"                       # none | s3 | dir
  OFFSITE_PREFIX="${OFFSITE_PREFIX:-dxtechs}"
  GPG_RECIPIENT_FILE="${GPG_RECIPIENT_FILE:-$(dirname "$CONFIG")/recipient.asc}"
  MIN_TABLES="${MIN_TABLES:-4}"
  COMPOSE_DIR="${COMPOSE_DIR:-$HOME/dxtechs-server}"
  MEDIA_DIR="${MEDIA_DIR:-/var/www/dxtechs/media}"   # ảnh của module Content (chỉ cần quyền đọc)
  if [ -n "${BACKUP_PG_EXEC:-}" ]; then
    read -r -a PG_EXEC <<<"$BACKUP_PG_EXEC"       # test thay bằng lệnh giả
  else
    PG_EXEC=(docker compose -p dxtechs-server --env-file "$COMPOSE_DIR/.env" -f "$COMPOSE_DIR/src/docker-compose.prod.yml" exec -T postgres)
  fi
  case "$OFFSITE" in none|s3|dir) ;; *) echo "FAIL OFFSITE='$OFFSITE' không hợp lệ (none|s3|dir)" >&2; exit 1 ;; esac
  mkdir -p "$BACKUP_DIR"; chmod 700 "$BACKUP_DIR"
}

# Lệnh chạy TRONG container postgres; biến $POSTGRES_USER/$POSTGRES_DB là của container (không đọc .env ở đây).
pg() { "${PG_EXEC[@]}" sh -c "$1"; }

check_recipient() {
  [ -f "$GPG_RECIPIENT_FILE" ] || die "Không có public key GPG: $GPG_RECIPIENT_FILE (xem README, bước 1)"
  if grep -q 'PRIVATE KEY' "$GPG_RECIPIENT_FILE"; then
    die "$GPG_RECIPIENT_FILE chứa PRIVATE key — chỉ được đặt PUBLIC key trên VPS. Xoá file này ngay."
  fi
}

encrypt() { # encrypt <in> <out>
  local home rc=0; home="$(mktemp -d)"
  # Keyring tạm, không đụng keyring của user. Dump -Fc đã nén ⇒ tắt nén của GPG.
  GNUPGHOME="$home" gpg --batch --yes --quiet --no-tty --trust-model always \
    --recipient-file "$GPG_RECIPIENT_FILE" --compress-algo none --output "$2" --encrypt "$1" || rc=$?
  rm -rf "$home"
  [ "$rc" -eq 0 ] && [ -s "$2" ]
}

upload() { # upload <file> <object-key>
  case "$OFFSITE" in
    s3)
      for v in S3_ENDPOINT S3_REGION S3_BUCKET S3_ACCESS_KEY S3_SECRET_KEY; do
        [ -n "${!v:-}" ] || die "Thiếu $v trong $CONFIG"
      done
      python3 "$SELF_DIR/s3put.py" "$1" "$2" >/dev/null ;;
    dir)
      [ -n "${OFFSITE_DIR:-}" ] || die "Thiếu OFFSITE_DIR trong $CONFIG"
      mkdir -p "$OFFSITE_DIR/$(dirname "$2")"
      cp "$1" "$OFFSITE_DIR/$2.part" && mv "$OFFSITE_DIR/$2.part" "$OFFSITE_DIR/$2"
      cmp -s "$1" "$OFFSITE_DIR/$2" || die "Bản sao ở $OFFSITE_DIR/$2 khác bản gốc" ;;
  esac
}

run_backup() {
  exec 9>"$BACKUP_DIR/.lock"
  flock -n 9 || die "Một lần backup khác đang chạy"
  local ts size tables key
  # out/enc là biến toàn cục: trap EXIT chạy sau khi hàm đã trả về.
  ts="$(date +%Y%m%d-%H%M%S)"
  out="$BACKUP_DIR/dxtechs-$ts.dump"
  enc=""
  trap 'rm -f "$out.tmp" ${enc:+"$enc"}' EXIT

  pg 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' >"$out.tmp" || die "pg_dump lỗi"
  size="$(stat -c %s "$out.tmp")"
  [ "$size" -gt 0 ] || die "pg_dump tạo file rỗng"
  # File đọc được và có đủ bảng — chặn trường hợp dump bị cắt cụt mà không báo lỗi.
  tables="$("${PG_EXEC[@]}" pg_restore --list <"$out.tmp" | grep -c ' TABLE DATA ' || true)"
  [ "$tables" -ge "$MIN_TABLES" ] || die "Dump chỉ có $tables bảng dữ liệu (cần ≥ $MIN_TABLES) — có thể hỏng"
  mv "$out.tmp" "$out"
  (cd "$BACKUP_DIR" && sha256sum "$(basename "$out")" >"$(basename "$out").sha256")
  find "$BACKUP_DIR" -maxdepth 1 -name 'dxtechs-*.dump*' -mtime +"$KEEP_DAYS" -delete
  log "LOCAL OK $out $size bytes, $tables bảng"

  if [ "$OFFSITE" != none ]; then
    check_recipient
    enc="$(mktemp "$BACKUP_DIR/.enc.XXXXXX")"
    encrypt "$out" "$enc" || die "Mã hoá GPG lỗi"
    key="$OFFSITE_PREFIX/${ts:0:4}/${ts:4:2}/$(basename "$out").gpg"
    upload "$enc" "$key" || die "Tải ra ngoài lỗi ($OFFSITE)"
    log "OFFSITE OK $OFFSITE:$key $(stat -c %s "$enc") bytes"
    backup_media "$ts"
  else
    log "OFFSITE SKIP (OFFSITE=none) — backup mới chỉ nằm trên VPS"
  fi
  ping_ok
}

# Ảnh của module Content (CT-3b): tar → mã hoá → kho ngoài. Không giữ bản trên VPS (cùng ổ đĩa với ảnh gốc thì vô ích).
# Ảnh đã nén sẵn ⇒ tar không nén. Thư mục chưa có/rỗng ⇒ bỏ qua (chưa ai tải ảnh).
backup_media() { # backup_media <ts>
  local tar_file media_enc key count
  [ -d "$MEDIA_DIR" ] || { log "MEDIA SKIP ($MEDIA_DIR chưa có)"; return 0; }
  count="$(find "$MEDIA_DIR" -type f ! -name '.*' | wc -l)"
  [ "$count" -gt 0 ] || { log "MEDIA SKIP ($MEDIA_DIR rỗng)"; return 0; }
  tar_file="$(mktemp "$BACKUP_DIR/.media.XXXXXX")"; media_enc="$(mktemp "$BACKUP_DIR/.media-enc.XXXXXX")"
  # File tạm của API (.upload-*.tmp) không phải ảnh hoàn chỉnh ⇒ bỏ.
  tar -C "$(dirname "$MEDIA_DIR")" --exclude='.*' -cf "$tar_file" "$(basename "$MEDIA_DIR")" || { rm -f "$tar_file" "$media_enc"; die "tar ảnh lỗi"; }
  encrypt "$tar_file" "$media_enc" || { rm -f "$tar_file" "$media_enc"; die "Mã hoá GPG ảnh lỗi"; }
  key="$OFFSITE_PREFIX/${1:0:4}/${1:4:2}/dxtechs-media-$1.tar.gpg"
  upload "$media_enc" "$key" || { rm -f "$tar_file" "$media_enc"; die "Tải ảnh ra ngoài lỗi ($OFFSITE)"; }
  log "MEDIA OK $OFFSITE:$key $count file, $(stat -c %s "$media_enc") bytes"
  rm -f "$tar_file" "$media_enc"
}

run_check() {
  log "Cấu hình: $CONFIG · BACKUP_DIR=$BACKUP_DIR · KEEP_DAYS=$KEEP_DAYS · OFFSITE=$OFFSITE"
  pg 'pg_isready -q -U "$POSTGRES_USER" -d "$POSTGRES_DB"' || die "Không kết nối được PostgreSQL trong container"
  log "PostgreSQL: OK"
  [ "$OFFSITE" = none ] && { log "OFFSITE=none — không kiểm tra kho ngoài"; return; }
  check_recipient
  GNUPGHOME="$(mktemp -d)" gpg --batch --quiet --show-keys --with-fingerprint "$GPG_RECIPIENT_FILE" | sed 's/^/  /'
  local f enc key
  f="$(mktemp)"; enc="$(mktemp)"
  echo "dxtechs backup check $(date -Is) $(hostname)" >"$f"
  encrypt "$f" "$enc" || die "Mã hoá GPG lỗi"
  key="$OFFSITE_PREFIX/_check/check-$(date +%Y%m%d-%H%M%S).txt.gpg"
  upload "$enc" "$key"
  rm -f "$f" "$enc"
  log "CHECK OK — đã tải $OFFSITE:$key. Tải file này về máy có private key và chạy: gpg --decrypt <file>"
}

run_restore_test() {
  local f="${1:-}" counts
  [ -n "$f" ] || f="$(ls -t "$BACKUP_DIR"/dxtechs-*.dump 2>/dev/null | head -1 || true)"
  [ -f "$f" ] || die "Không có file dump để thử"
  if [ -f "$f.sha256" ]; then (cd "$(dirname "$f")" && sha256sum -c --quiet "$(basename "$f").sha256") || die "SHA-256 không khớp: $f"; fi
  db="restore_test_$(date +%s)"
  trap 'pg "dropdb -U \"\$POSTGRES_USER\" --if-exists $db" >/dev/null 2>&1 || true' EXIT   # db: biến toàn cục
  pg "createdb -U \"\$POSTGRES_USER\" $db" || die "createdb lỗi"
  "${PG_EXEC[@]}" sh -c "pg_restore -U \"\$POSTGRES_USER\" -d $db --no-owner --no-acl --exit-on-error" <"$f" || die "pg_restore lỗi"
  counts="$(pg "psql -U \"\$POSTGRES_USER\" -d $db -tA -F' ' -c 'select (select count(*) from public.\"__EFMigrationsHistory\"), (select count(*) from catalog.categories), (select count(*) from catalog.products), (select count(*) from catalog.product_variants)'")" \
    || die "Truy vấn kiểm tra lỗi"
  read -r migrations categories products variants <<<"$counts"
  [ "${migrations:-0}" -ge 1 ] || die "Bản khôi phục không có lịch sử migration"
  log "RESTORE-TEST OK $(basename "$f") → migrations=$migrations categories=$categories products=$products variants=$variants (đã xoá $db)"
}

run_install() {
  local dest="$HOME/bin/dxtechs-backup" cron_cmd="${BACKUP_CRONTAB:-crontab}" tmp
  mkdir -p "$dest" "$(dirname "$CONFIG")"
  chmod 700 "$dest" "$(dirname "$CONFIG")"
  install -m 700 "$SELF_DIR/backup.sh" "$dest/backup.sh"
  install -m 600 "$SELF_DIR/s3put.py" "$dest/s3put.py"
  if [ ! -f "$CONFIG" ]; then
    install -m 600 "$SELF_DIR/backup.env.example" "$CONFIG"
    log "Đã tạo $CONFIG từ mẫu — sửa OFFSITE/S3_* rồi chạy: $dest/backup.sh --check"
  fi
  tmp="$(mktemp)"
  # Giữ mọi dòng khác; bỏ dòng backup-db.sh cũ và dòng cũ của chính script này rồi thêm lại.
  { $cron_cmd -l 2>/dev/null || true; } | grep -v -e 'backup-db\.sh' -e 'dxtechs-backup/backup\.sh' >"$tmp" || true
  {
    echo "15 2 * * * $dest/backup.sh >> $BACKUP_DIR/backup.log 2>&1"
    echo "40 3 1 * * $dest/backup.sh --restore-test >> $BACKUP_DIR/backup.log 2>&1"
  } >>"$tmp"
  $cron_cmd "$tmp"
  rm -f "$tmp"
  log "Cron đã cài (backup 02:15 hằng ngày, thử khôi phục 03:40 ngày 1 mỗi tháng):"
  $cron_cmd -l | sed 's/^/  /'
  log "File ~/backup-db.sh cũ được giữ nguyên (không còn được cron gọi) — xoá tay khi đã yên tâm."
}

load_config
case "${1:-}" in
  "")              run_backup ;;
  --check)         run_check ;;
  --restore-test)  run_restore_test "${2:-}" ;;
  --install)       run_install ;;
  -h|--help)       sed -n '2,13p' "$0" ;;
  *)               echo "Tham số không hợp lệ: $1 (xem --help)" >&2; exit 2 ;;
esac
