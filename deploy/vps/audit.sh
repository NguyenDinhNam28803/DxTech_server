#!/usr/bin/env bash
# CHỈ ĐỌC — không thay đổi gì. Chụp hiện trạng bảo mật/vận hành của VPS trước và sau khi làm.
# Chạy: ssh dxtechs-root  →  bash /home/deploy/dxtechs-server/src/deploy/vps/audit.sh
source "$(dirname "$0")/lib.sh"
set +e
require_root

say "Hệ điều hành"
. /etc/os-release && echo "  $PRETTY_NAME · kernel $(uname -r)"
[ -f /var/run/reboot-required ] && warn "Cần reboot để áp dụng bản vá kernel/thư viện"

say "SSH — giá trị đang áp dụng"
for k in port passwordauthentication kbdinteractiveauthentication permitrootlogin pubkeyauthentication maxstartups; do
  printf '  %-30s %s\n' "$k" "$(sshd_value "$k")"
done
echo "  File trong /etc/ssh/sshd_config.d/: $(ls /etc/ssh/sshd_config.d/ 2>/dev/null | tr '\n' ' ')"

say "Key được phép đăng nhập"
for u in root deploy webdeploy; do
  getent passwd "$u" >/dev/null && printf '  %-10s %s key · mật khẩu: %s\n' "$u" "$(key_count "$u")" "$(passwd -S "$u" | awk '{print $2}')"
done
echo "  (mật khẩu: P = có, L = đã khoá, NP = không có)"

say "Cổng đang nghe"
ss -tlnp | awk 'NR == 1 || /sshd|systemd|nginx|docker-proxy/ { print "  " $0 }'

say "Firewall (ufw)"
ufw status | sed 's/^/  /'

say "fail2ban"
echo "  service: $(systemctl is-active fail2ban 2>/dev/null)"
fail2ban-client status sshd 2>/dev/null | sed 's/^/  /'

say "Tự cài bản vá bảo mật"
echo "  unattended-upgrades: $(systemctl is-enabled unattended-upgrades 2>/dev/null)"
sed 's/^/  /' /etc/apt/apt.conf.d/20auto-upgrades 2>/dev/null

say "Chứng chỉ HTTPS"
certbot certificates 2>/dev/null | grep -E 'Certificate Name|Domains|Expiry' | sed 's/^ */  /'
systemctl list-timers --all 2>/dev/null | grep -i certbot | sed 's/^/  /'

say "Nginx"
nginx -t 2>&1 | tail -1 | sed 's/^/  /'
grep -nE '^\s*(listen|server_name|location|proxy_pass|return|root)\b' /etc/nginx/sites-enabled/* | sed 's/^/  /'

say "Thư mục web"
stat -c '  %U:%G %a %n' /var/www/dxtechs /var/www/dxtechs/releases 2>/dev/null
echo "  current → $(readlink /var/www/dxtechs/current)"

say "Docker — giới hạn log từng container"
for c in $(docker ps --format '{{.Names}}'); do
  printf '  %-34s %s\n' "$c" "$(docker inspect -f '{{.HostConfig.LogConfig.Type}} {{json .HostConfig.LogConfig.Config}}' "$c")"
done
echo "  Tổng log container: $(du -sh /var/lib/docker/containers 2>/dev/null | cut -f1)"

say "SSH 24 giờ qua"
since="24 hours ago"
echo "  Đăng nhập sai / user không tồn tại: $(journalctl -t sshd --since "$since" 2>/dev/null | grep -cE 'Failed password|Invalid user')"
echo "  Bị ngắt do quá tải (MaxStartups):  $(journalctl -t sshd --since "$since" 2>/dev/null | grep -ciE 'MaxStartups|drop connection')"
echo "  Đăng nhập bằng key thành công:     $(journalctl -t sshd --since "$since" 2>/dev/null | grep -c 'Accepted publickey')"

say "Đĩa · RAM"
df -h / | sed 's/^/  /'
free -h | sed 's/^/  /'
