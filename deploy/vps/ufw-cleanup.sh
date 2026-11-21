#!/usr/bin/env bash
# Xoá rule ufw mở cổng 22 (SSH thật chạy ở 24700). Chỉ chạy SAU ssh-hardening.sh.
source "$(dirname "$0")/lib.sh"
require_root

say "Kiểm tra"
port="$(sshd_value port)"
[ "$port" = 24700 ] || die "sshd đang dùng cổng '$port' (mong đợi đúng 24700) — dừng."
ss -tln | awk '{print $4}' | grep -qE '(^|:)22$' && die "Vẫn có tiến trình nghe cổng 22 — dừng."
ufw status | grep -qE '^24700(/tcp)?[[:space:]]+(ALLOW|LIMIT)' || die "ufw chưa mở 24700 — dừng, xoá 22 lúc này sẽ tự khoá."
ok "SSH chỉ nghe 24700, ufw đã mở 24700"

say "Xoá rule cổng 22"
backup_file /etc/ufw/user.rules
backup_file /etc/ufw/user6.rules
removed=0
for rule in OpenSSH 22/tcp 22; do
  if ufw status | grep -qE "^${rule}[[:space:]]+ALLOW"; then
    ufw --force delete allow "$rule" >/dev/null
    ok "Đã xoá: allow $rule (cả IPv4 và IPv6)"
    removed=1
  fi
done
[ "$removed" = 1 ] || ok "Không còn rule nào mở cổng 22"
ufw status verbose | sed 's/^/  /'
