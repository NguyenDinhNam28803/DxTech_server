#!/usr/bin/env bash
# Tạo user webdeploy cho CI client: chỉ ghi /var/www/dxtechs, KHÔNG có Docker, KHÔNG có mật khẩu.
#   bash webdeploy.sh 'ssh-ed25519 AAAA… github-actions-dxtechs-web'
#   bash webdeploy.sh --rollback      trả /var/www/dxtechs về cho deploy (giữ user webdeploy)
source "$(dirname "$0")/lib.sh"
require_root
web=/var/www/dxtechs

if [ "${1:-}" = "--rollback" ]; then
  chown -R deploy:deploy "$web"
  ok "Đã trả $web cho deploy — nhớ đổi lại secret VPS_USER/VPS_SSH_KEY của repo client"
  exit 0
fi

key="${1:-}"
[[ "$key" =~ ^ssh-ed25519\ [A-Za-z0-9+/=]+(\ .*)?$ ]] || die "Truyền vào DÒNG public key (bắt đầu bằng ssh-ed25519), không phải private key."

say "User"
if id webdeploy >/dev/null 2>&1; then ok "webdeploy đã có"; else
  adduser --disabled-password --gecos "" webdeploy >/dev/null
  ok "Đã tạo webdeploy (không mật khẩu)"
fi
id -nG webdeploy | grep -qw docker && die "webdeploy đang ở group docker — gỡ ra: gpasswd -d webdeploy docker"
id -nG webdeploy | grep -qwE 'sudo|adm' && die "webdeploy có quyền sudo/adm — gỡ ra trước."

say "Key"
install -d -m 700 -o webdeploy -g webdeploy /home/webdeploy/.ssh
ak=/home/webdeploy/.ssh/authorized_keys
touch "$ak"
grep -qxF "$key" "$ak" || echo "$key" >> "$ak"
chown webdeploy:webdeploy "$ak" && chmod 600 "$ak"
ok "$(key_count webdeploy) key trong $ak"

say "Quyền thư mục web"
chown -R webdeploy:webdeploy "$web"
stat -c '  %U:%G %a %n' "$web" "$web/releases"
sudo -u www-data test -r "$web/current/index.html" && ok "Nginx (www-data) vẫn đọc được index.html" \
  || die "www-data không đọc được $web/current/index.html — kiểm tra quyền"
echo "  Tiếp theo: chủ repo client đổi secret VPS_USER=webdeploy và VPS_SSH_KEY (sổ tay v2 mục 8)."
