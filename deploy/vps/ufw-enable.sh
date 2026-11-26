#!/usr/bin/env bash
# Bật ufw an toàn: chỉ mở 24700 (SSH), 80, 443; mọi cổng vào khác bị chặn.
#   bash ufw-enable.sh             áp dụng + hẹn giờ TỰ TẮT ufw sau 5 phút (công tắc an toàn)
#   bash ufw-enable.sh --confirm   đã thử SSH ở cửa sổ mới thành công ⇒ huỷ hẹn giờ, giữ ufw
#   bash ufw-enable.sh --rollback  tắt ufw ngay
# Container: API chỉ publish 127.0.0.1:5000, postgres không publish ⇒ không phụ thuộc ufw.
source "$(dirname "$0")/lib.sh"
require_root
unit=dxtechs-ufw-deadman

case "${1:-}" in
  --confirm)
    systemctl stop "$unit.timer" 2>/dev/null || true
    systemctl reset-failed "$unit.service" "$unit.timer" 2>/dev/null || true
    ok "Đã huỷ hẹn giờ tự tắt — ufw giữ nguyên"; ufw status verbose | sed 's/^/  /'; exit 0 ;;
  --rollback)
    systemctl stop "$unit.timer" 2>/dev/null || true
    ufw --force disable; ok "Đã tắt ufw"; exit 0 ;;
  "") ;;
  *) die "Tham số không hợp lệ: $1" ;;
esac

say "Kiểm tra trước"
command -v ufw >/dev/null || die "Chưa cài ufw (apt-get install -y ufw)."
port="$(sshd_value port)"
[ "$port" = 24700 ] || die "sshd đang dùng cổng '$port' (mong đợi 24700) — dừng."
[ -n "${SSH_CONNECTION:-}" ] && [ "$(awk '{print $4}' <<<"$SSH_CONNECTION")" != 24700 ] \
  && die "Phiên SSH hiện tại không đi qua cổng 24700 — dừng."
ok "sshd nghe 24700"
echo "  Cổng đang nghe trên mọi địa chỉ (ngoài 127.0.0.x / ::1):"
ss -tlnH | awk '{print $4}' | grep -vE '^(127\.|\[::1\])' | sort -u | sed 's/^/    /'

say "Công tắc an toàn: tự tắt ufw sau 5 phút nếu chưa --confirm"
systemctl stop "$unit.timer" 2>/dev/null || true
systemctl reset-failed "$unit.service" "$unit.timer" 2>/dev/null || true
systemd-run --quiet --unit="$unit" --on-active=5min "$(command -v ufw)" --force disable
ok "Đã hẹn giờ ($unit.timer)"

say "Áp dụng"
backup_file /etc/ufw/user.rules
backup_file /etc/ufw/user6.rules
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw allow 24700/tcp comment 'ssh' >/dev/null
ufw allow 80/tcp comment 'http' >/dev/null
ufw allow 443/tcp comment 'https' >/dev/null
ufw --force enable >/dev/null
ufw status verbose | sed 's/^/  /'
ufw status | grep -qE '^24700/tcp[[:space:]]+ALLOW' || { ufw --force disable; die "Không thấy rule 24700 sau khi bật — đã tắt lại ufw."; }
ok "ufw đã bật"

cat <<'NEXT'

  GIỮ CỬA SỔ NÀY MỞ. Trong 5 phút, mở cửa sổ Git Bash MỚI và thử:
    ssh dxtechs-root "echo ROOT_OK"
    ssh dxtechs "echo DEPLOY_OK"
    curl -s https://api.dxtechs.com/api/health/ready ; echo      # Healthy
  Cả 3 đúng ⇒ quay lại đây:  bash ufw-enable.sh --confirm
  Sai / không làm gì      ⇒ sau 5 phút ufw tự tắt, mọi thứ trở về như trước.
NEXT
