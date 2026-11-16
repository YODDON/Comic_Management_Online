# PLAN.md

Kế hoạch chuyển `Comic_Management` (microservices) sang `Comic_Management_Online` (modular monolith).
Trạng thái: `Chưa làm` / `Đang làm` / `Xong` / `Bị chặn`. Giờ là ước tính thô. Rủi ro: Thấp / TB / Cao.

## Mốc ban đầu (điền ở T00)

- `dotnet build` bản microservices: <kết quả>
- `npm run build` frontend: <kết quả>
- Lỗi hoặc cảnh báo có sẵn từ trước: <ghi lại để không nhầm là lỗi do mình gây ra>

## Kế hoạch

| ID | Nhánh | Việc | Rủi ro | Giờ | Trạng thái |
| --- | --- | --- | --- | --- | --- |
| T00 | main (tay) | Ghi mốc: build bản hiện tại, ghi lỗi/cảnh báo có sẵn vào mục "Mốc ban đầu" | Thấp | 1 | Chưa làm |
| T01 | main (tay) | Sao chép mã từ repo cũ (không mang `.git`), kiểm `.gitignore`, thêm bộ file harness | Thấp | 1 | Chưa làm |
| T02 | phase-0-chuan-bi | Đồng bộ package 10.x về 8.0.x (`Caching.StackExchangeRedis`, `Logging.Abstractions`) | Thấp | 1 | Chưa làm |
| T03 | phase-0-chuan-bi | Xóa OData, `Azure.Containers.Tools.Targets`, `Http.Features 5.0.17` (BannerAPI) | Thấp | 0.5 | Chưa làm |
| T04 | phase-1-bo-dich | Xóa 6 file dịch trong ComicAPI | Thấp | 0.5 | Chưa làm |
| T05 | phase-1-bo-dich | Sửa `Program.cs` và `appsettings.json` của ComicAPI | Thấp | 0.5 | Chưa làm |
| T06 | phase-1-bo-dich | Frontend: viết lại `useTranslatedTexts`, xóa `translationService.js` | Thấp | 0.5 | Chưa làm |
| T07 | phase-1-bo-dich | Dọn `backend/n8n/`, `docker-compose.yml`, `.env.example`, README | Thấp | 0.5 | Chưa làm |
| T08 | phase-2-host | Tạo `ComicMonolith.Api` (net8.0), thêm vào `.sln`, endpoint `/health` | TB | 3 | Chưa làm |
| T09 | phase-2-host | Chuyển 8 API thành `AddXxxModule` (mỗi module một phiên: Banner, User, Wallet, rồi còn lại) | TB | 8 | Chưa làm |
| T10 | phase-2-host | Xử lý trùng tên (class `Program`, profile AutoMapper, DTO, namespace) | TB | 3 | Chưa làm |
| T11 | phase-2-host | Đăng ký MediatR và AutoMapper cho mọi module ở host | Thấp | 2 | Chưa làm |
| T12 | phase-2-host | Gộp cấu hình: một `.env`, một `appsettings`, JWT một lần | Thấp | 2 | Chưa làm |
| T13 | phase-2-host | Middleware đổi đường dẫn thay YARP, kèm unit test | TB | 3 | Chưa làm |
| T14 | phase-2-host | Chuyển danh sách endpoint public sang host, kèm test 401 | Cao | 2 | Chưa làm |
| T37 | phase-3-test | **Làm trước T17 và T23:** test đặc tính cho nạp tiền, mua chapter, webhook SePay, saga | Cao | 6 | Chưa làm |
| T15 | phase-3-grpc | Liệt kê mọi lời gọi gRPC, điền bảng "gRPC còn lại" | TB | 2 | Chưa làm |
| T16 | phase-3-grpc | Tạo interface cho từng hướng gọi (theo cặp module) | TB | 4 | Chưa làm |
| T17 | phase-3-grpc | Cài interface bằng gọi trực tiếp (Payment có 6 handler, phải hỏi) | Cao | 10 | Chưa làm |
| T18 | phase-3-grpc | Xóa gRPC client/server, `.proto`, `Grpc.*`, biến `*_API_URL` | Thấp | 2 | Chưa làm |
| T19 | phase-3-grpc | Thay Redis bằng `AddDistributedMemoryCache()` (2 query ComicAPI) | Thấp | 1 | Chưa làm |
| T20 | phase-4-masstransit | Gộp 6 lần `AddMassTransit` thành một bus ở host | Cao | 4 | Chưa làm |
| T21 | phase-4-masstransit | Đổi transport sang in-memory, bỏ `MassTransit.RabbitMQ`, `RABBITMQ_*` | Thấp | 1 | Chưa làm |
| T22 | phase-4-masstransit | Chỉ đề xuất: EF Outbox (5 DbContext) giữ hay bỏ. **Người dùng quyết** | Cao | 3 | Chưa làm |
| T23 | phase-4-masstransit | Giữ saga in-memory, chạy test T37, kiểm sập app giữa chừng | Cao | 4 | Chưa làm |
| T24 | phase-5-db | Xác nhận 8 DbContext, 8 connection string, thứ tự migrate khi khởi động | Thấp | 2 | Chưa làm |
| T25 | phase-5-db | Đưa mật khẩu SQL vào `.env`, bỏ mặc định `changeme123` trong compose | Thấp | 0.5 | Chưa làm |
| T26 | phase-6-kiem-thu | `dotnet build` sạch toàn solution | TB | 2 | Chưa làm |
| T27 | phase-6-kiem-thu | Test tay các luồng chính (danh sách bên dưới) | Cao | 6 | Chưa làm |
| T28 | phase-6-kiem-thu | Frontend: `VITE_API_URL`, `npm run build`, đối chiếu đường dẫn API | Thấp | 1 | Chưa làm |
| T29 | phase-7-deploy | Dockerfile nhiều tầng cho `ComicMonolith.Api` | Thấp | 1.5 | Chưa làm |
| T30 | phase-7-deploy | `docker-compose.prod.yml`: backend, SQL Server, Nginx | TB | 2 | Chưa làm |
| T31 | phase-7-deploy | Nginx phục vụ frontend, chuyển tiếp API cùng domain | TB | 2 | Chưa làm |
| T32 | phase-7-deploy | `.env.production.example`, thêm `.env.production` vào `.gitignore` | TB | 1 | Chưa làm |
| T33 | phase-7-deploy | `docs/DEPLOY.md`: VPS, Docker, firewall 22/80/443, domain, HTTPS | TB | 3 | Chưa làm |
| T34 | tay | Cập nhật Google OAuth, webhook SePay/VietQR, CORS cho domain thật | Cao | 1.5 | Chưa làm |
| T35 | phase-7-deploy | Script backup SQL Server hằng ngày, giữ 7 bản, hướng dẫn khôi phục | TB | 2 | Chưa làm |
| T36 | tay | Smoke test trên production | TB | 2 | Chưa làm |

Tổng ước tính khoảng 92 giờ. Thứ tự làm theo thứ tự hàng trong bảng (T37 nằm trước T15).

## Ghi chú theo việc

- **T04 — 6 file (ComicAPI):** `Controllers/TranslationsController.cs`, `Features/Translations/Commands/TranslateTextCommand.cs`, `DTOs/Translation/TranslateTextRequestDto.cs`, `Interfaces/ITranslationService.cs`, `Infrastructure/Services/N8nTranslationService.cs`, `Infrastructure/Settings/N8nTranslationSettings.cs`. Không đụng database.
- **T06:** hook giữ nguyên kết quả `{ translations, translating, translationError }` để khoảng 20 trang không phải sửa.
- **T09:** BannerAPI đơn giản nhất, làm trước để tinh chỉnh prompt.
- **T22:** chưa kiểm chứng một bus dùng outbox cho nhiều DbContext được không. Phương án: (a) bỏ outbox, in-memory, thêm job dọn saga kẹt; (b) giữ RabbitMQ; (c) gộp DB Payment/Wallet/Chapter, mua chapter thành một transaction.
- **T27 — luồng test tay:** đăng ký, đăng nhập thường và Google, đọc truyện, bình luận, follow, favorite, nhiệm vụ, nạp tiền SePay, mua chapter, rút tiền, banner, upload ảnh.
- **T37:** giá trị kỳ vọng lấy từ việc chạy hệ cũ (có RabbitMQ), không lấy từ suy đoán của agent.

## gRPC còn lại (điền ở T09, T15)

| Từ module | Sang module | Hàm rpc | Handler dùng | Đã thay? |
| --- | --- | --- | --- | --- |
| | | | | |

## Quyết định mở

- [ ] T22: EF Outbox giữ hay bỏ.
- [ ] Có gộp 8 database thành một không.
- [ ] Phương án thay MassTransit sau khi v8 hết hỗ trợ bảo mật (cần kiểm tra ngày chính thức).

## Nhật ký

| Ngày | Việc | Lỗi gặp / điều học được |
| --- | --- | --- |
| | | |
