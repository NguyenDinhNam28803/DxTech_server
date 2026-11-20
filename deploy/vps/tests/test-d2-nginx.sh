#!/usr/bin/env bash
# Kiểm thử d2-nginx.py trên cấu hình mẫu giống production (chạy trong CI, không cần nginx).
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp)"
cp "$dir/nginx-before-d2.conf" "$tmp"

python3 "$dir/../d2-nginx.py" "$tmp" --apply >/dev/null

block() { awk -v n="$2" '/^server \{/{b=""} {b=b $0 "\n"} /^\}/{ if (b ~ "server_name " n ";") printf "%s", b }' "$1"; }
main="$(block "$tmp" 'dxtechs\.com')"
api_before="$(block "$dir/nginx-before-d2.conf" 'api\.dxtechs\.com')"
api_after="$(block "$tmp" 'api\.dxtechs\.com')"

grep -q 'return 404;' <<< "$main"            || { echo "FAIL: dxtechs.com chưa trả 404 cho /api/"; exit 1; }
! grep -q 'proxy_pass' <<< "$main"           || { echo "FAIL: dxtechs.com còn proxy_pass"; exit 1; }
grep -q 'try_files' <<< "$main"              || { echo "FAIL: mất location / của SPA"; exit 1; }
[ "$api_before" = "$api_after" ]             || { echo "FAIL: block api.dxtechs.com bị thay đổi"; exit 1; }
rc=0; python3 "$dir/../d2-nginx.py" "$tmp" >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 3 ]                              || { echo "FAIL: chạy lần 2 phải trả mã 3 (đã làm), nhận $rc"; exit 1; }
# Cấu hình lạ (2 block dxtechs.com) ⇒ mã 1, không ghi gì.
odd="$(mktemp)"; cat "$dir/nginx-before-d2.conf" "$dir/nginx-before-d2.conf" > "$odd"; cp "$odd" "$odd.orig"
rc=0; python3 "$dir/../d2-nginx.py" "$odd" --apply >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 1 ] && cmp -s "$odd" "$odd.orig" || { echo "FAIL: cấu hình lạ phải bị từ chối và giữ nguyên"; exit 1; }
echo "d2-nginx: tất cả kiểm tra đạt"
