#!/usr/bin/env bash
# Kiểm thử ct3-nginx.py trên cấu hình mẫu giống production (sau D2). Chạy trong CI; có nginx thì kiểm cú pháp thật.
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp)"
python3 "$dir/../d2-nginx.py" <(cat "$dir/nginx-before-d2.conf") >"$tmp"   # cấu hình production hiện tại = sau D2

block() { awk -v n="$2" '/^server \{/{b=""} {b=b $0 "\n"} /^\}/{ if (b ~ "server_name " n ";") printf "%s", b }' "$1"; }
api_before="$(block "$tmp" 'api\.dxtechs\.com')"
python3 "$dir/../ct3-nginx.py" "$tmp" --apply >/dev/null
main="$(block "$tmp" 'dxtechs\.com')"

grep -q 'location ^~ /media/' <<< "$main"                      || { echo "FAIL: thiếu location /media/"; exit 1; }
grep -q '\[0-9a-f\]{32}\\.(jpg|png|webp)\$' <<< "$main"         || { echo "FAIL: /media/ không giới hạn tên file"; exit 1; }
grep -q 'nosniff' <<< "$main"                                  || { echo "FAIL: thiếu nosniff"; exit 1; }
grep -q 'return 404;' <<< "$(sed -n '/location \^~ \/media\//,/^    }/p' <<< "$main")" || { echo "FAIL: /media/ khác mẫu phải 404"; exit 1; }
grep -q 'proxy_pass http://127.0.0.1:8080/api/content/sitemap.xml;' <<< "$main" || { echo "FAIL: thiếu sitemap"; exit 1; }
grep -q 'try_files $uri $uri/ /index.html' <<< "$main"         || { echo "FAIL: mất location / của SPA"; exit 1; }
[ "$api_before" = "$(block "$tmp" 'api\.dxtechs\.com')" ]      || { echo "FAIL: block api.dxtechs.com bị thay đổi"; exit 1; }
# /media/ phải đứng TRƯỚC location / (đọc dễ, dù ^~ không phụ thuộc thứ tự).
[ "$(grep -n 'location ^~ /media/' <<< "$main" | cut -d: -f1)" -lt "$(grep -n 'location / {' <<< "$main" | cut -d: -f1)" ] \
  || { echo "FAIL: /media/ nằm sau location /"; exit 1; }
rc=0; python3 "$dir/../ct3-nginx.py" "$tmp" >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 3 ] || { echo "FAIL: chạy lần 2 phải trả mã 3 (đã làm), nhận $rc"; exit 1; }
odd="$(mktemp)"; cat "$tmp" "$tmp" > "$odd"; cp "$odd" "$odd.orig"
rc=0; python3 "$dir/../ct3-nginx.py" "$odd" --apply >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 1 ] && cmp -s "$odd" "$odd.orig" || { echo "FAIL: cấu hình lạ phải bị từ chối và giữ nguyên"; exit 1; }

if command -v nginx >/dev/null 2>&1; then
  conf="$(mktemp -d)"
  # Bản sao chỉ để kiểm cú pháp: bỏ chứng chỉ (không có trên máy CI), đổi cổng.
  sed -e '/listen \[::\]/d' -e 's/listen 443[^;]*;/listen 127.0.0.1:18443;/' -e 's/listen 80[^;]*;/listen 127.0.0.1:18080;/' -e '/ssl_/d' "$tmp" > "$conf/site.conf"
  printf 'pid %s/nginx.pid; error_log stderr; events {} http { access_log off; include %s/site.conf; }\n' "$conf" "$conf" > "$conf/nginx.conf"
  nginx -t -q -c "$conf/nginx.conf" -p "$conf" || { echo "FAIL: nginx -t từ chối cấu hình sau CT-3b"; exit 1; }
  echo "  nginx -t: OK"
fi
echo "ct3-nginx: tất cả kiểm tra đạt"
