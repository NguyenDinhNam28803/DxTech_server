#!/usr/bin/env bash
# Tắt đăng nhập SSH bằng mật khẩu; root chỉ vào bằng key.
#   bash ssh-hardening.sh            áp dụng
#   bash ssh-hardening.sh --rollback gỡ bỏ (dùng từ cửa sổ root còn mở, hoặc console của nhà cung cấp)
#
# File tên 00-…: sshd lấy giá trị ĐẦU TIÊN gặp, và Ubuntu cloud thường có 50-cloud-init.conf với
# "PasswordAuthentication yes". Đặt tên 99-… như bản v1 của sổ tay sẽ KHÔNG có tác dụng.
source "$(dirname "$0")/lib.sh"
require_root
conf=/etc/ssh/sshd_config.d/00-dxtechs-hardening.conf

reload_sshd() { sshd -t && { systemctl reload ssh 2>/dev/null || systemctl restart ssh; }; }

if [ "${1:-}" = "--rollback" ]; then
  rm -f "$conf"
  reload_sshd
  ok "Đã gỡ $conf — passwordauthentication = $(sshd_value passwordauthentication)"
  exit 0
fi

say "Kiểm tra trước — tránh tự khoá mình ngoài VPS"
grep -qiE '^\s*Include\s+/etc/ssh/sshd_config\.d/' /etc/ssh/sshd_config \
  || die "/etc/ssh/sshd_config không Include sshd_config.d — dừng, báo lại để xử lý riêng."
[ "$(key_count root)" -ge 1 ] || die "root chưa có SSH key nào (mục 7.3 bước 1 sổ tay v2)."
[ "$(key_count deploy)" -ge 1 ] || die "deploy chưa có SSH key — CI server sẽ hỏng."
journalctl -t sshd --since "3 hours ago" 2>/dev/null | grep -q 'Accepted publickey for root' \
  || die "Chưa thấy root đăng nhập BẰNG KEY trong 3 giờ qua. Mở cửa sổ mới: ssh dxtechs-root (dùng key dxtechs_admin), rồi chạy lại."
ok "root: $(key_count root) key, đã đăng nhập bằng key gần đây · deploy: $(key_count deploy) key"

say "Áp dụng"
cat > "$conf" <<'CONF'
# DXTechs — sổ tay v2 mục 7.3. Tên 00- để được đọc trước 50-cloud-init.conf.
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
PubkeyAuthentication yes
CONF
chmod 644 "$conf"
if ! reload_sshd; then
  rm -f "$conf"; reload_sshd || true
  die "sshd -t báo lỗi — đã gỡ file, cấu hình SSH giữ nguyên như cũ."
fi

pa="$(sshd_value passwordauthentication)"; ki="$(sshd_value kbdinteractiveauthentication)"; pr="$(sshd_value permitrootlogin)"
printf '  passwordauthentication=%s kbdinteractiveauthentication=%s permitrootlogin=%s\n' "$pa" "$ki" "$pr"
case "$pr" in without-password|prohibit-password) pr_ok=yes ;; *) pr_ok=no ;; esac
if [ "$pa" != no ] || [ "$ki" != no ] || [ "$pr_ok" != yes ]; then
  rm -f "$conf"; reload_sshd
  die "Giá trị không như mong đợi (có file khác ghi đè) — đã gỡ, giữ cấu hình cũ."
fi
ok "Đã tắt đăng nhập bằng mật khẩu"

cat <<'NEXT'

  GIỮ CỬA SỔ NÀY MỞ. Mở cửa sổ Git Bash MỚI và thử:
    ssh dxtechs-root "echo ROOT_OK"        # phải in ROOT_OK
    ssh dxtechs "echo DEPLOY_OK"           # phải in DEPLOY_OK
    ssh -o PubkeyAuthentication=no -p 24700 root@103.72.98.140   # phải: Permission denied (publickey)
  Nếu 1 trong 2 lệnh đầu lỗi: quay lại cửa sổ này chạy  bash ssh-hardening.sh --rollback
NEXT
