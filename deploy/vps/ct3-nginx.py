#!/usr/bin/env python3
"""CT-3b: trong server block 443 của dxtechs.com, thêm
  - `location ^~ /media/`  : phục vụ ảnh từ /var/www/dxtechs/media, CHỈ tên file đúng mẫu API sinh (yyyy/mm/<32 hex>.jpg|png|webp),
                             Content-Type cố định theo đuôi, nosniff, CSP chặn mọi thứ, cache 1 năm (tên file không bao giờ đổi).
  - `location = /sitemap.xml`: proxy tới API (/api/content/sitemap.xml).
Không đụng block api.dxtechs.com. In bản mới để so sánh; chỉ ghi khi chạy với --apply.
Mã thoát: 0 = có thay đổi · 3 = đã làm rồi · 1 = cấu hình khác dự kiến, KHÔNG sửa gì."""
import datetime, re, sys

path, apply = sys.argv[1], "--apply" in sys.argv
src = open(path, encoding="utf-8").read()


def match_brace(s, i):
    depth, q = 0, None
    while i < len(s):
        c = s[i]
        if q:
            if c == "\\": i += 1
            elif c == q: q = None
        elif c == "#":
            i = s.find("\n", i)
            if i < 0: break
            continue
        elif c in "\"'": q = c
        elif c == "{": depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0: return i + 1
        i += 1
    sys.exit("LỖI: dấu ngoặc không khớp trong " + path)


def strip_comments(t):
    return re.sub(r"#[^\n]*", "", t)


targets = []
for m in re.finditer(r"(?m)^\s*server\s*\{", src):
    start = m.end() - 1
    end = match_brace(src, start)
    body = strip_comments(src[start:end])
    names = set(" ".join(re.findall(r"server_name\s+([^;]+);", body)).split())
    listens = " ".join(re.findall(r"listen\s+([^;]+);", body))
    if "dxtechs.com" in names and "443" in listens:
        targets.append((start, end, names))

if len(targets) != 1:
    sys.exit(f"DỪNG: cần đúng 1 block 443 có server_name dxtechs.com, tìm thấy {len(targets)}. Không sửa gì.")
start, end, names = targets[0]
if "api.dxtechs.com" in names:
    sys.exit("DỪNG: block dxtechs.com cũng chứa api.dxtechs.com — cấu hình khác dự kiến. Không sửa gì.")

block = src[start:end]
code = strip_comments(block)
has_media = re.search(r"location\s+(\^~\s+)?/media/", code)
has_sitemap = re.search(r"location\s+=?\s*/sitemap\.xml", code)
if has_media and has_sitemap:
    print("ĐÃ LÀM: block dxtechs.com đã có /media/ và /sitemap.xml. Không sửa gì.", file=sys.stderr)
    sys.exit(3)
if has_media or has_sitemap:
    sys.exit("DỪNG: chỉ có một trong hai location /media/, /sitemap.xml — cấu hình khác dự kiến, sửa tay. Không sửa gì.")

loc = re.search(r"(?m)^([ \t]*)location\s+/\s*\{", block)
if not loc:
    sys.exit("DỪNG: không thấy `location / {` (SPA) trong block dxtechs.com. Không sửa gì.")
ind = loc.group(1)
today = f"{datetime.date.today():%d/%m/%Y}"
new = f"""{ind}# CT-3b ({today}): ảnh của module Content — chỉ tên file API sinh ra; loại file cố định theo đuôi, không đoán nội dung.
{ind}location ^~ /media/ {{
{ind}    location ~ "^/media/[0-9]{{4}}/[0-9]{{2}}/[0-9a-f]{{32}}\\.(jpg|png|webp)$" {{
{ind}        root /var/www/dxtechs;
{ind}        types {{ image/jpeg jpg; image/png png; image/webp webp; }}
{ind}        default_type application/octet-stream;
{ind}        add_header X-Content-Type-Options "nosniff" always;
{ind}        add_header Content-Security-Policy "default-src 'none'; sandbox" always;
{ind}        add_header Cache-Control "public, max-age=31536000, immutable" always;
{ind}        try_files $uri =404;
{ind}    }}
{ind}    return 404;
{ind}}}
{ind}# CT-3b ({today}): sitemap do API sinh (bài đã đăng + sản phẩm).
{ind}location = /sitemap.xml {{
{ind}    proxy_pass http://127.0.0.1:8080/api/content/sitemap.xml;
{ind}    proxy_set_header Host api.dxtechs.com;
{ind}    proxy_set_header X-Forwarded-For $remote_addr;
{ind}    proxy_set_header X-Forwarded-Proto https;
{ind}}}
"""
out = src[:start] + block[:loc.start()] + new + block[loc.start():] + src[end:]

if apply:
    open(path, "w", encoding="utf-8").write(out)
    print("ĐÃ GHI:", path)
else:
    sys.stdout.write(out)
