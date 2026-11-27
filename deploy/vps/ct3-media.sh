#!/usr/bin/env bash
# CT-3b (ADR-015 §5): thư mục ảnh + Nginx phục vụ https://dxtechs.com/media/… và /sitemap.xml.
# 1) Tạo /var/www/dxtechs/media thuộc uid 1654 (user "app" trong container API) — API ghi, Nginx chỉ đọc.
# 2) Sửa Nginx: sao lưu → xem trước → ghi → nginx -t → reload. Lỗi ở bất kỳ bước nào ⇒ khôi phục bản sao lưu.
# Chạy lại an toàn (đã làm ⇒ không sửa gì). Chạy bằng root, sau khi deploy bản có CT-3b.
source "$(dirname "$0")/lib.sh"
require_root
here="$(cd "$(dirname "$0")" && pwd)"
f=/etc/nginx/sites-available/dxtechs
media=/var/www/dxtechs/media
app_uid=1654

say "Thư mục ảnh"
install -d -m 755 -o "$app_uid" -g "$app_uid" "$media"
# Thư mục đã có từ trước (docker tạo khi mount, thuộc root) ⇒ sửa chủ sở hữu cho cả cây con.
chown -R "$app_uid:$app_uid" "$media"
ok "$media thuộc uid $app_uid, quyền $(stat -c %a "$media")"

say "Kiểm tra trước"
[ "$(readlink -f /etc/nginx/sites-enabled/dxtechs)" = "$f" ] || die "sites-enabled/dxtechs không trỏ tới $f"
nginx -t >/dev/null 2>&1 || die "nginx -t đang lỗi TỪ TRƯỚC — không sửa thêm."

say "Thay đổi sẽ áp dụng"
set +e
preview="$(python3 "$here/ct3-nginx.py" "$f")"; rc=$?
set -e
if [ "$rc" -eq 3 ]; then
  ok "Nginx đã có /media/ và /sitemap.xml từ trước — không sửa gì"
else
  [ "$rc" -eq 0 ] || die "Cấu hình Nginx khác dự kiến (xem thông báo ở trên) — không sửa gì."
  diff -u "$f" - <<< "$preview" | sed 's/^/  /' || true
  bak="/root/nginx-dxtechs.$(date +%F-%H%M%S).bak"
  cp -a "$f" "$bak" && ok "Sao lưu: $bak"
  python3 "$here/ct3-nginx.py" "$f" --apply >/dev/null
  if nginx -t 2>/dev/null && systemctl reload nginx; then
    ok "Đã reload Nginx"
  else
    cp -a "$bak" "$f"; nginx -t && systemctl reload nginx
    die "nginx -t lỗi — đã khôi phục $bak"
  fi
  sleep 2   # reload bất đồng bộ: đợi worker mới (sự cố 21/11/2026)
fi

say "Kiểm tra sau"
probe="$media/2000/01"; name="00000000000000000000000000000000.png"
install -d -m 755 -o "$app_uid" -g "$app_uid" "$media/2000" "$probe"
printf '\x89PNG\r\n\x1a\nprobe' >"$probe/$name"; chmod 644 "$probe/$name"
hdr() { curl -s -o /dev/null -D - --resolve dxtechs.com:443:127.0.0.1 "https://dxtechs.com$1"; }
code() { curl -s -o /dev/null -w '%{http_code}' --resolve dxtechs.com:443:127.0.0.1 "https://dxtechs.com$1"; }
h="$(hdr "/media/2000/01/$name")"
printf '  %-52s %s (mong đợi 200)\n' "/media/2000/01/<ảnh thử>.png" "$(head -1 <<<"$h" | awk '{print $2}')"
grep -qi '^content-type: image/png' <<<"$h" && ok "Content-Type image/png" || warn "Content-Type sai: $(grep -i '^content-type' <<<"$h")"
grep -qi '^x-content-type-options: nosniff' <<<"$h" && ok "nosniff" || warn "thiếu X-Content-Type-Options"
printf '  %-52s %s (mong đợi 404)\n' "/media/2000/01/ten-la.svg" "$(code /media/2000/01/ten-la.svg)"
printf '  %-52s %s (mong đợi 404)\n' "/media/" "$(code /media/)"
rm -rf "$media/2000"
printf '  %-52s %s (mong đợi 200)\n' "/sitemap.xml" "$(code /sitemap.xml)"
printf '  %-52s %s (mong đợi 200)\n' "/" "$(code /)"
[ -n "${bak:-}" ] && echo "  Quay lại: cp -a $bak $f && nginx -t && systemctl reload nginx"
true
