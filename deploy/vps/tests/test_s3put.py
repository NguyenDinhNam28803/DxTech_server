#!/usr/bin/env python3
"""Kiểm tra s3put.py: (1) chữ ký khớp ví dụ chính thức trong tài liệu AWS Signature V4 cho S3,
(2) tải lên một máy chủ giả: đúng đường dẫn, nội dung, header, và chữ ký tính lại được ở phía nhận."""
import datetime
import hashlib
import http.server
import os
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "backup"))
import s3put  # noqa: E402

EMPTY_SHA = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
AK, SK = "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"


class AwsExamples(unittest.TestCase):
    """Ví dụ “GET Object” và “PUT Object” trong tài liệu S3: Signature Calculations for the Authorization Header."""

    def test_get_object_example(self):
        auth = s3put.sign("GET", "examplebucket.s3.amazonaws.com", "/test.txt", {
            "host": "examplebucket.s3.amazonaws.com",
            "range": "bytes=0-9",
            "x-amz-content-sha256": EMPTY_SHA,
            "x-amz-date": "20130524T000000Z",
        }, EMPTY_SHA, AK, SK, "us-east-1", "20130524T000000Z")
        self.assertIn("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", auth)
        self.assertTrue(auth.endswith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41"), auth)

    def test_put_object_example(self):
        body = b"Welcome to Amazon S3."
        h = hashlib.sha256(body).hexdigest()
        auth = s3put.sign("PUT", "examplebucket.s3.amazonaws.com", "/test$file.text", {
            "host": "examplebucket.s3.amazonaws.com",
            "date": "Fri, 24 May 2013 00:00:00 GMT",
            "x-amz-date": "20130524T000000Z",
            "x-amz-storage-class": "REDUCED_REDUNDANCY",
            "x-amz-content-sha256": h,
        }, h, AK, SK, "us-east-1", "20130524T000000Z")
        self.assertTrue(auth.endswith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd"), auth)


class FakeS3(http.server.BaseHTTPRequestHandler):
    received = {}

    def do_PUT(self):  # noqa: N802
        n = int(self.headers["Content-Length"])
        FakeS3.received = {"path": self.path, "headers": dict(self.headers), "body": self.rfile.read(n)}
        self.send_response(int(os.environ.get("FAKE_S3_STATUS", "200")))
        self.end_headers()
        self.wfile.write(b"<Error><Code>AccessDenied</Code></Error>" if os.environ.get("FAKE_S3_STATUS") else b"")

    def log_message(self, *a):
        pass


class Upload(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = http.server.HTTPServer(("127.0.0.1", 0), FakeS3)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def env(self):
        return {"S3_ENDPOINT": f"http://127.0.0.1:{self.srv.server_port}", "S3_ALLOW_HTTP": "1",
                "S3_REGION": "ap-southeast-1", "S3_BUCKET": "dxtechs-backup",
                "S3_ACCESS_KEY": AK, "S3_SECRET_KEY": SK}

    def test_put_signs_body_and_path(self):
        os.environ.pop("FAKE_S3_STATUS", None)
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(b"\x00encrypted-dump\xff" * 100)
        now = datetime.datetime(2026, 11, 24, 2, 15, tzinfo=datetime.timezone.utc)
        url = s3put.put(f.name, "dxtechs/2026/11/dxtechs-20261124-021500.dump.gpg", self.env(), now)
        r = FakeS3.received
        self.assertEqual(r["path"], "/dxtechs-backup/dxtechs/2026/11/dxtechs-20261124-021500.dump.gpg")
        self.assertTrue(url.endswith(r["path"]))
        self.assertEqual(r["body"], open(f.name, "rb").read())
        lower = {k.lower(): v for k, v in r["headers"].items()}
        self.assertEqual(lower["x-amz-content-sha256"], hashlib.sha256(r["body"]).hexdigest())
        self.assertEqual(lower["x-amz-date"], "20261124T021500Z")
        # Phía nhận tính lại chữ ký từ chính các header đã ký ⇒ phải trùng.
        auth = lower["authorization"]
        signed = auth.split("SignedHeaders=")[1].split(",")[0].split(";")
        self.assertEqual(signed, ["content-length", "content-type", "host", "x-amz-content-sha256", "x-amz-date"])
        again = s3put.sign("PUT", lower["host"], r["path"], {k: lower[k] for k in signed},
                           lower["x-amz-content-sha256"], AK, SK, "ap-southeast-1", "20261124T021500Z")
        self.assertEqual(auth, again)
        self.assertIn("Credential=AKIAIOSFODNN7EXAMPLE/20261124/ap-southeast-1/s3/aws4_request", auth)
        os.unlink(f.name)

    def test_error_status_fails_without_leaking_secret(self):
        os.environ["FAKE_S3_STATUS"] = "403"
        with tempfile.NamedTemporaryFile(delete=False) as f:
            f.write(b"x")
        with self.assertRaises(SystemExit) as ctx:
            s3put.put(f.name, "k", self.env())
        self.assertIn("HTTP 403", str(ctx.exception))
        self.assertNotIn(SK, str(ctx.exception))
        os.environ.pop("FAKE_S3_STATUS")
        os.unlink(f.name)

    def test_refuses_plain_http_and_missing_vars(self):
        e = self.env()
        e.pop("S3_ALLOW_HTTP")
        with self.assertRaises(SystemExit):
            s3put.put(__file__, "k", e)
        e = self.env()
        e.pop("S3_SECRET_KEY")
        with self.assertRaises(SystemExit) as ctx:
            s3put.put(__file__, "k", e)
        self.assertIn("S3_SECRET_KEY", str(ctx.exception))


if __name__ == "__main__":
    unittest.main(verbosity=2)
