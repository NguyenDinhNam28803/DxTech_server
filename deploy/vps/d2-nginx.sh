#!/usr/bin/env bash
# D2: https://dxtechs.com/api/* trả 404 — API chỉ còn ở https://api.dxtechs.com.
# Sao lưu → xem trước thay đổi → ghi → nginx -t → reload. Lỗi ở bất kỳ bước nào ⇒ khôi phục bản sao lưu.
source "$(dirname "$0")/lib.sh"
require_root
here="$(cd "$(dirname "$0")" && pwd)"
f=/etc/nginx/sites-available/dxtechs

say "Kiểm tra trước"
[ "$(readlink -f /etc/nginx/sites-enabled/dxtechs)" = "$f" ] || die "sites-enabled/dxtechs không trỏ tới $f"
# Bản client từ PR-C2 trở đi có <link rel="preconnect" href="https://api.dxtechs.com"> trong index.html.
# Không tìm trong assets/: khi chưa trang nào gọi API, Vite loại bỏ lớp gọi API khỏi bundle (tree-shaking)
# nên chuỗi base URL không xuất hiện (sự cố 21/11/2026).
grep -qs 'api\.dxtechs\.com' /var/www/dxtechs/current/index.html \
  || die "Bản client đang chạy cũ hơn PR-C2 (index.html chưa có api.dxtechs.com) — chưa được tắt /api/ trên dxtechs.com."
if grep -rqsE '["'"'"'`]/api/' /var/www/dxtechs/current/assets/; then
  die "Bundle client còn gọi đường dẫn tương đối /api/ — tắt /api/ trên dxtechs.com sẽ làm hỏng client."
fi
ok "Client đang chạy là bản PR-C2+ và không gọi /api/ tương đối"
nginx -t >/dev/null 2>&1 || die "nginx -t đang lỗi TỪ TRƯỚC — không sửa thêm."

say "Thay đổi sẽ áp dụng"
set +e
preview="$(python3 "$here/d2-nginx.py" "$f")"; rc=$?
set -e
[ "$rc" -eq 3 ] && { ok "D2 đã được áp dụng từ trước — không sửa gì"; exit 0; }
[ "$rc" -eq 0 ] || die "Cấu hình Nginx khác dự kiến (xem thông báo ở trên) — không sửa gì."
diff -u "$f" - <<< "$preview" | sed 's/^/  /' || true

bak="/root/nginx-dxtechs.$(date +%F-%H%M%S).bak"
cp -a "$f" "$bak" && ok "Sao lưu: $bak"
python3 "$here/d2-nginx.py" "$f" --apply >/dev/null
if nginx -t 2>/dev/null && systemctl reload nginx; then
  ok "Đã reload Nginx"
else
  cp -a "$bak" "$f"; nginx -t && systemctl reload nginx
  die "nginx -t lỗi — đã khôi phục $bak"
fi

say "Kiểm tra sau"
code() { curl -s -o /dev/null -w '%{http_code}' --resolve "$1:443:127.0.0.1" "https://$1$2"; }
printf '  %-46s %s (mong đợi 404)\n' "https://dxtechs.com/api/health" "$(code dxtechs.com /api/health)"
printf '  %-46s %s (mong đợi 200)\n' "https://api.dxtechs.com/api/health/ready" "$(code api.dxtechs.com /api/health/ready)"
printf '  %-46s %s (mong đợi 200)\n' "https://dxtechs.com/" "$(code dxtechs.com /)"
echo "  Quay lại: cp -a $bak $f && nginx -t && systemctl reload nginx"
