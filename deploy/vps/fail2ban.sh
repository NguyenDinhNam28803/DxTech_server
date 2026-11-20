#!/usr/bin/env bash
# fail2ban cho SSH ở cổng thật (24700): 5 lần sai trong 10 phút ⇒ chặn IP 1 giờ.
# Giảm số kết nối rác ⇒ giảm lỗi "kex_exchange_identification: Connection reset" ở CI (MaxStartups).
source "$(dirname "$0")/lib.sh"
require_root

say "Cài đặt"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq fail2ban python3-systemd >/dev/null
ok "fail2ban $(fail2ban-client --version 2>/dev/null | head -1)"

port="$(sshd_value port)"
[ -n "$port" ] || die "Không đọc được cổng SSH từ sshd -T"

say "Cấu hình jail sshd (cổng $port)"
cat > /etc/fail2ban/jail.d/dxtechs.local <<CONF
# DXTechs — sổ tay v2 mục 7.4
[sshd]
enabled  = true
port     = $port
backend  = systemd
maxretry = 5
findtime = 10m
bantime  = 1h
CONF
fail2ban-client -t >/dev/null || die "Cấu hình fail2ban lỗi (fail2ban-client -t)"
systemctl enable fail2ban >/dev/null 2>&1
systemctl restart fail2ban
sleep 2
fail2ban-client status sshd | sed 's/^/  /'
ok "fail2ban đang theo dõi SSH cổng $port"
echo "  Lỡ tự chặn mình: vào console nhà cung cấp → fail2ban-client set sshd unbanip <IP>"
