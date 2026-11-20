# shellcheck shell=bash
# Hàm dùng chung cho các script vận hành VPS (chạy bằng root).
set -euo pipefail

say()  { printf '\n\033[1;34m== %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✔\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✘ %s\033[0m\n' "$*" >&2; exit 1; }

require_root() { [ "$(id -u)" -eq 0 ] || die "Phải chạy bằng root: ssh dxtechs-root rồi chạy lại."; }

# Số key hợp lệ trong authorized_keys của một user (0 nếu không có file).
key_count() {
  local home
  home="$(getent passwd "$1" | cut -d: -f6)"
  [ -n "$home" ] && [ -f "$home/.ssh/authorized_keys" ] || { echo 0; return; }
  grep -cE '^[[:space:]]*(ssh-(ed25519|rsa)|ecdsa-sha2-|sk-)' "$home/.ssh/authorized_keys" || true
}

# Giá trị sshd đang áp dụng (sau khi đọc mọi file Include), ví dụ: sshd_value passwordauthentication
sshd_value() { sshd -T 2>/dev/null | awk -v k="$1" '$1 == k { print $2 }' | paste -sd, -; }

backup_file() { [ -e "$1" ] && cp -a "$1" "/root/$(basename "$1").$(date +%F-%H%M%S).bak" && ok "Đã sao lưu $1 vào /root/"; return 0; }
