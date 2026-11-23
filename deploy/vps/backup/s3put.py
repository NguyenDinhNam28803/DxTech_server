#!/usr/bin/env python3
"""Tải một file lên kho lưu trữ tương thích S3 (PUT Object, chữ ký AWS Signature V4).

Chỉ dùng thư viện chuẩn của Python 3 (có sẵn trên Ubuntu 22.04) — không cài awscli/rclone.
Ký cả nội dung (x-amz-content-sha256 = SHA-256 thật của file) để kho từ chối file bị sửa trên đường truyền.

Thông tin đăng nhập đọc từ biến môi trường (không qua tham số dòng lệnh ⇒ không lộ trong `ps`):
  S3_ENDPOINT    https://s3.example.com            (path-style: <endpoint>/<bucket>/<key>)
  S3_REGION      ví dụ ap-southeast-1 (kho không có region: dùng "us-east-1" hoặc theo tài liệu nhà cung cấp)
  S3_BUCKET
  S3_ACCESS_KEY
  S3_SECRET_KEY

Dùng:  s3put.py <file> <object-key>     → in URL đích, exit 0 khi kho trả 2xx.
"""
import datetime
import hashlib
import hmac
import http.client
import os
import sys
import urllib.parse


def _hmac(key: bytes, msg: str) -> bytes:
    return hmac.new(key, msg.encode("utf-8"), hashlib.sha256).digest()


def signing_key(secret: str, date: str, region: str, service: str = "s3") -> bytes:
    k = _hmac(("AWS4" + secret).encode("utf-8"), date)
    k = _hmac(k, region)
    k = _hmac(k, service)
    return _hmac(k, "aws4_request")


def uri_encode_path(path: str) -> str:
    # S3: mã hoá từng ký tự trừ unreserved và '/', không mã hoá hai lần.
    return urllib.parse.quote(path, safe="/-_.~")


def sign(method: str, host: str, path: str, headers: dict, payload_hash: str,
         access_key: str, secret: str, region: str, amz_date: str, query: str = "") -> str:
    """Trả về giá trị header Authorization. `headers` phải gồm host, x-amz-date, x-amz-content-sha256."""
    date = amz_date[:8]
    canon = {k.lower().strip(): " ".join(str(v).split()) for k, v in headers.items()}
    signed = ";".join(sorted(canon))
    canonical_request = "\n".join([
        method,
        uri_encode_path(path),
        query,
        "".join(f"{k}:{canon[k]}\n" for k in sorted(canon)),
        signed,
        payload_hash,
    ])
    scope = f"{date}/{region}/s3/aws4_request"
    string_to_sign = "\n".join([
        "AWS4-HMAC-SHA256", amz_date, scope,
        hashlib.sha256(canonical_request.encode("utf-8")).hexdigest(),
    ])
    signature = hmac.new(signing_key(secret, date, region), string_to_sign.encode("utf-8"), hashlib.sha256).hexdigest()
    return f"AWS4-HMAC-SHA256 Credential={access_key}/{scope},SignedHeaders={signed},Signature={signature}"


def put(file_path: str, object_key: str, env=os.environ, now=None) -> str:
    for name in ("S3_ENDPOINT", "S3_REGION", "S3_BUCKET", "S3_ACCESS_KEY", "S3_SECRET_KEY"):
        if not env.get(name):
            raise SystemExit(f"s3put: thiếu biến môi trường {name}")
    endpoint = urllib.parse.urlsplit(env["S3_ENDPOINT"])
    if endpoint.scheme not in ("https", "http"):
        raise SystemExit("s3put: S3_ENDPOINT phải bắt đầu bằng https://")
    if endpoint.scheme == "http" and env.get("S3_ALLOW_HTTP") != "1":
        raise SystemExit("s3put: từ chối http:// (chỉ dùng trong test với S3_ALLOW_HTTP=1)")

    with open(file_path, "rb") as f:
        body = f.read()
    payload_hash = hashlib.sha256(body).hexdigest()
    now = now or datetime.datetime.now(datetime.timezone.utc)
    amz_date = now.strftime("%Y%m%dT%H%M%SZ")
    base = endpoint.path.rstrip("/")
    path = f"{base}/{env['S3_BUCKET']}/{object_key.lstrip('/')}"
    host = endpoint.netloc
    headers = {
        "host": host,
        "x-amz-content-sha256": payload_hash,
        "x-amz-date": amz_date,
        "content-length": str(len(body)),
        "content-type": "application/octet-stream",
    }
    headers["authorization"] = sign("PUT", host, path, {k: v for k, v in headers.items()},
                                    payload_hash, env["S3_ACCESS_KEY"], env["S3_SECRET_KEY"],
                                    env["S3_REGION"], amz_date)

    conn_cls = http.client.HTTPSConnection if endpoint.scheme == "https" else http.client.HTTPConnection
    conn = conn_cls(host, timeout=120)
    try:
        conn.request("PUT", uri_encode_path(path), body=body, headers=headers)
        resp = conn.getresponse()
        text = resp.read(2000).decode("utf-8", "replace")
    finally:
        conn.close()
    if not 200 <= resp.status < 300:
        # Không in header/khoá; chỉ mã lỗi và đoạn đầu thông báo XML của kho.
        raise SystemExit(f"s3put: kho trả HTTP {resp.status}: {text[:500]}")
    return f"{endpoint.scheme}://{host}{path}"


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Dùng: s3put.py <file> <object-key>")
    print(put(sys.argv[1], sys.argv[2]))
