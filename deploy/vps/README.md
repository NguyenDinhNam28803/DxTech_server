# Script vận hành VPS (chạy bằng root)

Mỗi lần deploy, CI giải nén repo vào `/home/deploy/dxtechs-server/src/`, nên các script này có sẵn trên VPS:

```bash
ssh dxtechs-root
cd /home/deploy/dxtechs-server/src/deploy/vps
bash audit.sh            # CHỈ ĐỌC — chụp hiện trạng, chạy trước và sau
```

| Thứ tự | Script | Việc | Ảnh hưởng | Quay lại |
|---|---|---|---|---|
| 0 | `audit.sh` | Chụp hiện trạng (SSH, ufw, fail2ban, chứng chỉ, Nginx, log Docker) | Không | — |
| 1 | `d2-nginx.sh` | `https://dxtechs.com/api/*` trả 404; API chỉ còn ở `api.dxtechs.com` | Reload Nginx (không gián đoạn) | Lệnh `cp` in ra cuối script |
| 2 | `certbot renew --dry-run` | Xác nhận chứng chỉ tự gia hạn được | Không | — |
| 3 | `ssh-hardening.sh` | Tắt đăng nhập SSH bằng mật khẩu | Reload sshd, phiên đang mở giữ nguyên | `ssh-hardening.sh --rollback` |
| 4 | `fail2ban.sh` | Chặn IP dò SSH (5 lần sai/10 phút ⇒ 1 giờ) | Không | `systemctl disable --now fail2ban` |
| 5 | `auto-updates.sh` | Tự cài bản vá bảo mật; không tự reboot/restart Docker | Không | `rm /etc/apt/apt.conf.d/52dxtechs-unattended` + sửa `20auto-upgrades` về `"0"` |
| 6 | `ufw-cleanup.sh` | Xoá rule mở cổng 22 (chỉ khi ufw đang bật) | Không | `ufw allow 22/tcp` |
| 6b | `ufw-enable.sh` | Bật ufw: chỉ mở 24700, 80, 443; **tự tắt sau 5 phút** nếu không `--confirm` | Chặn mọi cổng vào khác | `ufw-enable.sh --rollback` |
| 7 | `webdeploy.sh '<public key>'` | User riêng cho CI client, không có Docker | Cần đổi secret repo client | `webdeploy.sh --rollback` |

**Backup** chạy bằng user `deploy` (không phải root) — xem [`backup/README.md`](backup/README.md):

| Script | Việc | Quay lại |
|---|---|---|
| `backup/backup.sh --install` | Cài backup 02:15 + bản sao mã hoá GPG ra kho S3 ngoài VPS + thử khôi phục hằng tháng; thay cron `backup-db.sh` cũ | `crontab -e` khôi phục dòng cũ (file `~/backup-db.sh` được giữ) |

Giới hạn log Docker **không** làm ở đây: nằm trong `docker-compose.prod.yml` (`x-logging`), áp dụng ở lần deploy kế tiếp.

Nguyên tắc chung của các script:
- Kiểm tra điều kiện trước, sai thì dừng mà **không sửa gì** (ví dụ `ssh-hardening.sh` từ chối chạy nếu root chưa đăng nhập bằng key trong 3 giờ qua).
- Sao lưu file cấu hình vào `/root/*.bak` trước khi sửa; chạy lại nhiều lần an toàn.
- Lưu ý: thư mục này thuộc user `deploy`. `deploy` ở group `docker` nên vốn đã tương đương root;
  khi chạy bằng root vẫn nên đọc lại script (`less`) nếu không chắc nội dung.

Chi tiết từng bước, kết quả mong đợi và xử lý sự cố: *Sổ tay lệnh v2.0*, mục 7–9.
