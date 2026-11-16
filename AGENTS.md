# AGENTS.md

Quy tắc cho agent AI trong repo `Comic_Management_Online`. Đọc file này và `PLAN.md` trước khi sửa.

## Mục tiêu

Chuyển backend .NET 8 từ 9 project chạy riêng (8 API + ApiGateway) sang **một ứng dụng ASP.NET Core 8 (modular monolith)** tên `ComicMonolith.Api`, mỗi service cũ thành một module. Deploy bằng Docker Compose trên VPS. Frontend React/Vite giữ nguyên, chỉ đặt `VITE_API_URL` khi build.

Mã được sao chép từ https://github.com/YODDON/Comic_Management (chỉ tham chiếu, không sửa repo đó).

## Quyết định đã chốt (không tự đổi)

- **Bỏ:** ApiGateway/YARP (thay bằng middleware đổi đường dẫn), gRPC và 16 file `.proto` (thay bằng interface gọi trực tiếp), RabbitMQ (MassTransit chạy in-memory), Redis (`AddDistributedMemoryCache()`), OData, `Azure.Containers.Tools.Targets`, `Microsoft.AspNetCore.Http.Features 5.0.17`, n8n + LibreTranslate và tính năng dịch.
- **Giữ:** MassTransit core + saga `PurchaseStateMachine`, MediatR, AutoMapper 12, EF Core 8 + SQL Server với 8 DbContext / 8 database (mỗi module tự `Database.Migrate()`), JWT, BCrypt, Google.Apis.Auth, MailKit, Cloudinary, DotNetEnv.
- **Chưa quyết, phải hỏi:** EF Outbox của MassTransit (T22), gộp 8 database thành một.
- **Khóa version:** không đổi major của MassTransit (8), MediatR (12), AutoMapper (12). Bản mới đã đổi giấy phép. Hook sẽ chặn.

## Lệnh kiểm chứng

```
cd backend && dotnet build prn232_comic_api.sln
cd frontend && npm install && npm run build
```

Có project test thì chạy thêm `dotnet test`. Chỉ báo "xong" khi các lệnh chạy sạch và dán kết quả. Build sạch không chứng minh logic đúng.

## Quy tắc

1. Mỗi phiên làm **một việc** trong `PLAN.md`. Không gộp, không làm trước việc sau.
2. Đọc file liên quan trước. Việc rủi ro Cao hoặc nhiều file: nêu kế hoạch (file sẽ sửa, cách làm) và **chờ người dùng "ok"**.
3. Chỉ sửa trong phạm vi việc. Không refactor, đổi tên, định dạng lại file không liên quan.
4. Không sửa thư mục `Migrations`.
5. Giữ nguyên đường dẫn API frontend đang gọi. Gateway cũ đổi `/comics`, `/categories`, `/chapters`, `/comments`, `/favorites`, `/follows`, `/reading-history`, `/payments`, `/uploads`, `/missions`, `/notifications` thành `/api/...`; giữ nguyên `/auth`, `/wallets`, `/currency`, `/withdraws`, `/banners`. Không còn route `/translations`.
6. Giữ nguyên danh sách endpoint public của gateway cũ (`ApiGateway/Program.cs`).
7. Không đọc, sửa, commit `.env` hay bí mật. Chỉ sửa `.env.example` với giá trị giả.
8. Không commit vào `main`. Mỗi giai đoạn một nhánh (`phase-1-bo-dich`...), mỗi việc một commit `T04: <mô tả>`.
9. Không nâng version package nếu việc không yêu cầu. Package mới phải hỏi trước.
10. Không xóa, sửa test để cho qua; không tắt cảnh báo hay lỗi.
11. Sửa thất bại 3 lần cùng một lỗi: dừng, dán nguyên văn lỗi, nêu 1–2 hướng và hỏi.

## Vùng nhạy cảm (dừng và hỏi)

PaymentAPI, WalletAPI, saga `PurchaseStateMachine`, mua chapter, nạp/rút tiền, webhook SePay/VietQR, JWT, đăng nhập, Google OAuth, danh sách endpoint public, mọi thao tác có thể mất dữ liệu. Giải thích sẽ sửa gì, rủi ro, chờ đồng ý. Sau khi sửa, nói rõ cách kiểm tra thủ công. Không sửa logic Payment/Wallet trước khi có test đặc tính (T37).

## Gộp module (T09)

Lời gọi gRPC giữa các module (Mission↔Social, Chapter↔Mission, Comic↔Chapter, Payment→Wallet/Chapter): **không tự thiết kế lại**. Ghi vào bảng "gRPC còn lại" trong `PLAN.md`; nếu chặn build thì dừng và hỏi.

## Báo cáo cuối việc

- **Đã đổi:** danh sách file.
- **Kiểm chứng:** lệnh đã chạy và kết quả.
- **Chưa chắc / cần người dùng kiểm tra:** điều build không chứng minh được.

Sau đó cập nhật trạng thái trong `PLAN.md` và thêm một dòng Nhật ký.
