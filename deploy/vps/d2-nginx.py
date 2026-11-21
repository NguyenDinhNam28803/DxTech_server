#!/usr/bin/env python3
"""D2: trong server block của dxtechs.com (cổng 443), thay `location /api/ {...}` bằng `return 404`.
Không đụng block api.dxtechs.com. In ra bản mới để so sánh; chỉ ghi khi chạy với --apply.
Mã thoát: 0 = có thay đổi · 3 = đã làm rồi, không cần sửa · 1 = cấu hình khác dự kiến, KHÔNG sửa gì."""
import datetime, re, sys

path, apply = sys.argv[1], "--apply" in sys.argv
src = open(path, encoding="utf-8").read()

def match_brace(s, i):
    """i trỏ vào '{'; trả về vị trí ngay sau '}' tương ứng (bỏ qua comment và chuỗi)."""
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
    names = re.findall(r"server_name\s+([^;]+);", body)
    names = set(" ".join(names).split())
    listens = " ".join(re.findall(r"listen\s+([^;]+);", body))
    if "dxtechs.com" in names and "443" in listens:
        targets.append((start, end, names))

if len(targets) != 1:
    sys.exit(f"DỪNG: cần đúng 1 block 443 có server_name dxtechs.com, tìm thấy {len(targets)}. Không sửa gì.")
start, end, names = targets[0]
if "api.dxtechs.com" in names:
    sys.exit("DỪNG: block dxtechs.com cũng chứa api.dxtechs.com — cấu hình khác dự kiến. Không sửa gì.")

block = src[start:end]
locs = [m for m in re.finditer(r"(?m)^([ \t]*)location\s+(?:\^~\s+)?/api/\s*\{", block)]
if not locs:
    print("ĐÃ LÀM (hoặc chưa từng có): block dxtechs.com không có `location /api/`. Không sửa gì.", file=sys.stderr)
    sys.exit(3)
if len(locs) > 1:
    sys.exit("DỪNG: có nhiều hơn 1 `location /api/`. Không sửa gì.")
lm = locs[0]
lend = match_brace(block, lm.end() - 1)
old = block[lm.start():lend]
if re.search(r"return\s+404\s*;", strip_comments(old)) and "proxy_pass" not in strip_comments(old):
    print("ĐÃ LÀM: `location /api/` đã trả 404. Không sửa gì.", file=sys.stderr)
    sys.exit(3)
ind = lm.group(1)
new = (f"{ind}# D2 ({datetime.date.today():%d/%m/%Y}): API chỉ phục vụ ở https://api.dxtechs.com — không proxy qua dxtechs.com nữa.\n"
       f"{ind}location /api/ {{\n{ind}    return 404;\n{ind}}}")
out = src[:start] + block[:lm.start()] + new + block[lend:] + src[end:]

if apply:
    open(path, "w", encoding="utf-8").write(out)
    print("ĐÃ GHI:", path)
else:
    sys.stdout.write(out)
