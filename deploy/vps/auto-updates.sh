#!/usr/bin/env bash
# Tự cài bản vá bảo mật Ubuntu mỗi ngày. KHÔNG tự reboot, KHÔNG tự restart Docker.
source "$(dirname "$0")/lib.sh"
require_root

say "Cài đặt"
export DEBIAN_FRONTEND=noninteractive
apt-get install -y -qq unattended-upgrades >/dev/null

cat > /etc/apt/apt.conf.d/20auto-upgrades <<'CONF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
CONF
cat > /etc/apt/apt.conf.d/52dxtechs-unattended <<'CONF'
// DXTechs — sổ tay v2 mục 7.5. Reboot làm tay vào giờ thấp điểm khi có /var/run/reboot-required.
Unattended-Upgrade::Automatic-Reboot "false";
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
CONF
ok "Bật cập nhật hằng ngày, tắt tự reboot"

# needrestart (Ubuntu 22.04+) có thể tự restart dịch vụ sau khi vá thư viện. Restart dockerd/containerd
# = dừng cả API lẫn PostgreSQL ⇒ loại trừ, để restart tay.
if [ -d /etc/needrestart ]; then
  mkdir -p /etc/needrestart/conf.d
  cat > /etc/needrestart/conf.d/50-dxtechs.conf <<'CONF'
# DXTechs: không tự restart Docker/containerd (sẽ dừng API + PostgreSQL).
$nrconf{override_rc}{qr(^docker)} = 0;
$nrconf{override_rc}{qr(^containerd)} = 0;
CONF
  perl -c /etc/needrestart/conf.d/50-dxtechs.conf >/dev/null 2>&1 || die "Cú pháp needrestart lỗi"
  ok "needrestart sẽ không tự restart Docker/containerd"
fi

systemctl enable --now unattended-upgrades >/dev/null 2>&1
say "Chạy thử (không cài thật)"
unattended-upgrade --dry-run 2>&1 | tail -n 3 | sed 's/^/  /'
ok "unattended-upgrades: $(systemctl is-enabled unattended-upgrades)"
