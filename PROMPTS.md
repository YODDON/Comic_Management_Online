# PROMPTS.md

Câu lệnh giao việc cho agent. Mỗi việc một phiên mới. Thay `<...>` bằng giá trị thật.
Trước khi giao việc của giai đoạn mới, tự tạo nhánh: `git checkout -b <nhánh trong PLAN.md>`.

## A. Dùng chung

### A1. Khởi động phiên

```
Đọc AGENTS.md và PLAN.md. Hôm nay làm việc <ID>.
Đọc các file liên quan, rồi nêu kế hoạch tối đa 10 dòng: file sẽ sửa, cách làm, lệnh kiểm chứng.
Dừng và chờ tôi trả lời "ok". Chưa sửa gì.
```

### A2. Mẫu giao một việc (khi PROMPTS.md chưa có prompt riêng)

```
Việc <ID> — <tên>. Phạm vi: chỉ sửa <file/thư mục>. Không đụng <thứ cấm>.
Mẫu tham chiếu trong repo: <file có cách làm tương tự, nếu có>.
Xong khi: <lệnh kiểm chứng và kết quả mong đợi>.
Sau khi xong: chạy lệnh kiểm chứng trong AGENTS.md, commit "<ID>: <mô tả>",
cập nhật PLAN.md (trạng thái + một dòng Nhật ký), báo cáo theo mẫu cuối AGENTS.md.
```

### A3. Khi build lỗi

```
Build lỗi, nguyên văn:
<dán toàn bộ lỗi>
Chỉ xử lý lỗi này. Nêu nguyên nhân, cách sửa nhỏ nhất, rồi sửa. Không sửa ngoài phạm vi việc <ID>.
Nếu đây là lần thứ 3 sửa cùng lỗi mà chưa được: dừng và đề xuất 2 hướng.
```

### A4. Tự review diff trước khi commit

```
Xem git diff của việc <ID>. Liệt kê (chưa sửa): file ngoài phạm vi, chỗ đổi hành vi so với bản cũ,
bí mật bị lộ, cảnh báo hoặc test bị tắt/sửa để cho qua, package mới.
```

### A5. Review Payment/Wallet (chạy trong phiên riêng, không phải phiên đã viết code)

```
Bạn là người review, không viết code. Đọc git diff của <ID> ở PaymentAPI/WalletAPI/saga.
Tìm: ví bị trừ hai lần, trừ tiền mà chapter không mở, saga kẹt khi một bước lỗi,
webhook SePay xử lý trùng hoặc không kiểm chữ ký/số tiền, endpoint cần đăng nhập bị mở.
Mỗi vấn đề: file, dòng, kịch bản tái hiện. Nói rõ phần nào chưa kiểm chứng được.
```

### A6. Kết phiên

```
Cập nhật PLAN.md: trạng thái <ID>, bảng "gRPC còn lại" nếu đổi, một dòng Nhật ký. Không sửa gì khác.
```

## B. Theo việc

### T00, T01 — bạn làm tay

Xem phần "Chuẩn bị" trong doc. Sau T01, prompt kiểm tra:

```
Chạy lệnh kiểm chứng trong AGENTS.md cho bản hiện tại. Không sửa code.
Điền mục "Mốc ban đầu" trong PLAN.md: kết quả build, lỗi và cảnh báo có sẵn (nguyên văn, rút gọn nếu quá dài).
```

### T02

```
Việc T02. Liệt kê mọi PackageReference version 10.x trong backend, đề xuất bản 8.0.x tương ứng, chờ tôi ok rồi đổi.
Không đổi MassTransit, MediatR, AutoMapper. Xong khi dotnet build sạch.
```

### T03

```
Việc T03. Grep để xác nhận không có code dùng OData (EnableQuery, AddOData, ODataController).
Rồi xóa PackageReference: Microsoft.AspNetCore.OData (8 project), Microsoft.VisualStudio.Azure.Containers.Tools.Targets,
Microsoft.AspNetCore.Http.Features (BannerAPI). Xong khi dotnet build sạch.
```

### T04

```
Việc T04. Trong backend/ComicAPI xóa 6 file: Controllers/TranslationsController.cs,
Features/Translations/Commands/TranslateTextCommand.cs, DTOs/Translation/TranslateTextRequestDto.cs,
Interfaces/ITranslationService.cs, Infrastructure/Services/N8nTranslationService.cs,
Infrastructure/Settings/N8nTranslationSettings.cs (đường dẫn nằm dưới các project ComicAPI.*).
Grep ITranslationService, N8nTranslation, TranslateText và liệt kê tham chiếu còn sót, chưa sửa Program.cs.
Build có thể lỗi ở Program.cs, báo rõ.
```

### T05

```
Việc T05. Trong ComicAPI.API/Program.cs xóa đăng ký N8nTranslationSettings và AddHttpClient cho ITranslationService.
Trong appsettings.json xóa mục N8nTranslation. Xong khi dotnet build sạch và grep "N8nTranslation" trong backend không còn kết quả.
```

### T06

```
Việc T06. Viết lại frontend/src/hooks/useTranslatedTexts.js: luôn trả văn bản gốc, không gọi API,
giữ nguyên kết quả { translations, translating: false, translationError: '' }.
Xóa frontend/src/services/translationService.js. Không sửa các trang dùng hook.
Xong khi npm run build sạch và không còn import translationService.
```

### T07

```
Việc T07. Xóa backend/n8n/. Trong backend/docker-compose.yml xóa service n8n, libretranslate,
volume n8n_data, libretranslate_data và mọi depends_on trỏ tới chúng.
Trong backend/.env.example xóa dòng N8nTranslation__*. Trong backend/README.md, frontend/README.md, README.md
xóa nội dung về n8n, LibreTranslate, dịch nội dung.
Xong khi `docker compose -f backend/docker-compose.yml config` không lỗi và grep -ri "n8n\|libretranslate"
không còn kết quả ngoài AGENTS.md, PLAN.md, PROMPTS.md.
```

### T08

```
Việc T08. Tạo backend/ComicMonolith.Api (net8.0, web), thêm vào prn232_comic_api.sln.
Program.cs chỉ có khung: DotNetEnv load một lần, Swagger, CORS (origin đọc từ FRONTEND_URL),
JWT dùng AddCustomJwtAuthentication của SharedKernel, endpoint GET /health. Chưa nạp module.
Không sửa project cũ. Xong khi build sạch và `dotnet run` trả 200 ở /health.
```

### T09 (một module mỗi phiên: Banner → User → Wallet → Comic → Chapter → Social → Mission → Payment)

```
Việc T09 cho <BannerAPI>. Đọc <BannerAPI.API>/Program.cs.
Tạo extension Add<Banner>Module(IServiceCollection, IConfiguration) chứa DbContext, repository, service,
và hàm Use<Banner>Module(WebApplication) gọi Database.Migrate() cho DbContext của module.
Host gọi hai hàm này và nạp controller bằng AddApplicationPart. Không lặp JWT, CORS, Swagger trong module.
Lời gọi gRPC sang module khác: để nguyên, ghi vào bảng "gRPC còn lại" trong PLAN.md. Chặn build thì dừng hỏi.
Xong khi build sạch, host chạy, Swagger hiện endpoint của module.
```

Với PaymentAPI và WalletAPI: thêm câu "Đây là vùng nhạy cảm: nêu kế hoạch, chờ ok, không đổi logic nghiệp vụ."

### T10

```
Việc T10. Tìm trùng tên giữa các module (class Program, Profile AutoMapper, DTO, namespace, hằng số, file .proto
sinh cùng namespace). Liệt kê thành bảng và đề xuất đổi tên. Chờ ok rồi mới sửa. Xong khi build sạch.
```

### T11

```
Việc T11. Ở host, đăng ký MediatR bằng RegisterServicesFromAssemblies và AddAutoMapper với assembly của 8 module.
Xóa đăng ký trùng trong module. Xong khi build sạch và gọi được một endpoint đại diện của mỗi module.
```

### T12

```
Việc T12. Gộp cấu hình: DotNetEnv chỉ load ở host, JWT cấu hình một lần, gom 8 connection string, Cloudinary,
SMTP, Google Client ID, SePay vào một appsettings.json và một backend/.env.example (giá trị giả).
Không đọc hay sửa .env thật. Xong khi host khởi động đủ cấu hình.
```

### T13

```
Việc T13. Đọc ApiGateway/appsettings.json (ReverseProxy) và ApiGateway/Program.cs.
Viết middleware ở host: đổi /comics, /categories, /chapters, /comments, /favorites, /follows, /reading-history,
/payments, /uploads, /missions, /notifications thành /api/...; giữ /auth, /wallets, /currency, /withdraws, /banners.
Đối chiếu từng route của gateway, báo route nào khác danh sách trên. Không chuyển /translations.
Tạo project test (xUnit) và viết unit test cho hàm đổi đường dẫn, gồm query string và đường dẫn con.
Xong khi dotnet test và build sạch.
```

### T14

```
Việc T14 (vùng nhạy cảm). Đọc ApiGateway/Program.cs, lập bảng mọi endpoint public kèm điều kiện method
(ví dụ GET /comics trừ /comics/me, /comics/purchased; POST /payments/sepay-webhook). Chưa sửa, chờ tôi duyệt bảng.
Sau khi duyệt: chuyển luật sang host, giữ thứ tự middleware routing → auth. Viết test: mỗi endpoint public không trả 401,
endpoint đại diện cần đăng nhập trả 401 khi không có token. Xong khi dotnet test sạch.
```

### T37 (trước T15)

```
Việc T37. Không đổi hành vi nào. Viết test đặc tính ghi lại hành vi HIỆN TẠI của: nạp tiền thành công,
webhook SePay (chữ ký sai, số tiền không khớp, gọi trùng), mua chapter thành công, ví không đủ tiền,
mua chapter đã sở hữu, chuỗi trạng thái của PurchaseStateMachine.
Dùng database test phù hợp. Giá trị kỳ vọng lấy từ chạy code hiện tại, không suy đoán.
Kịch bản nào cần dịch vụ ngoài chưa có thì nêu và hỏi. Xong khi các test PASS trên code hiện tại.
```

### T15

```
Việc T15. Chỉ đọc. Từ 16 file .proto và các client/server gRPC, điền bảng "gRPC còn lại" trong PLAN.md:
module gọi, module bị gọi, hàm rpc, handler dùng. Đánh dấu các cặp gọi vòng. Không sửa code.
```

### T16

```
Việc T16 cho cặp <Mission → Social>. Tạo interface trong SharedKernel (hoặc project Contracts riêng nếu gây tham chiếu vòng),
tên và kiểu dữ liệu lấy từ hàm rpc tương ứng. Chưa thay lời gọi. Xong khi build sạch.
```

### T17

```
Việc T17 cho cặp <Mission → Social>. Cài interface bằng gọi trực tiếp service của module kia, thay lời gọi gRPC trong handler.
Giữ nguyên hành vi, kể cả xử lý lỗi và giá trị trả về khi không tìm thấy. Cặp có Payment hoặc Wallet: nêu kế hoạch, chờ ok,
sau khi sửa chạy test T37. Xong khi build và test sạch; nêu cách tôi kiểm tra tay. Cập nhật bảng "gRPC còn lại".
```

### T18

```
Việc T18. Khi bảng "gRPC còn lại" đã thay hết: xóa gRPC client/server, 16 file .proto, Protobuf item trong .csproj,
package Grpc.* và Google.Protobuf, biến *_API_URL và GrpcEndpoints trong cấu hình và .env.example.
Xong khi build, test sạch và grep -ri "grpc" trong backend không còn kết quả trong code.
```

### T19

```
Việc T19. Trong ComicAPI thay AddStackExchangeRedisCache bằng AddDistributedMemoryCache, giữ code dùng IDistributedCache.
Xóa package Microsoft.Extensions.Caching.StackExchangeRedis, biến REDIS_CONNECTION, service redis trong compose. Xong khi build sạch.
```

### T20

```
Việc T20 (rủi ro Cao). Liệt kê mọi AddMassTransit, consumer, saga, ReceiveEndpoint trong 6 module.
Đề xuất cách gộp thành một bus ở host (vẫn RabbitMQ ở bước này). Chờ ok rồi sửa. Chạy test T37 sau khi sửa.
```

### T21

```
Việc T21. Đổi transport sang UsingInMemory, bỏ MassTransit.RabbitMQ, RABBITMQ_*, service rabbitmq trong compose.
Xong khi build sạch, test T37 PASS và một event đại diện (ComicViewedIntegrationEvent) được consume.
```

### T22

```
Việc T22. Chỉ đọc và đề xuất. Tìm mọi AddEntityFrameworkOutbox, UseBusOutbox.
Với version MassTransit trong .csproj, xác định một bus có dùng outbox cho nhiều DbContext được không,
chỉ dựa trên tài liệu hoặc mã nguồn thư viện (ghi rõ nguồn). Viết các phương án vào "Quyết định mở". Không sửa code.
```

### T23

```
Việc T23. Áp dụng phương án T22 tôi đã chọn: <phương án>. Giữ PurchaseStateMachine trên bus in-memory.
Chạy test T37. Đề xuất cách kiểm khi app sập giữa chừng và cách phát hiện, dọn saga kẹt; chờ ok trước khi viết job dọn.
Sau đó tôi sẽ chạy review A5 ở phiên khác.
```

### T24, T25

```
Việc T24. Xác nhận 8 DbContext và 8 connection string đúng sau khi gộp, mỗi module migrate khi khởi động.
Liệt kê thứ tự khởi động và quyền SQL cần có. Không đổi migration.
```

```
Việc T25. Bỏ mật khẩu mặc định changeme123 khỏi docker-compose.yml, bắt buộc lấy từ SQL_SA_PASSWORD.
Cập nhật .env.example bằng giá trị giả. Sửa README cho khớp.
```

### T26, T28

```
Việc T26. Build toàn solution. Với mỗi cảnh báo mới so với "Mốc ban đầu": nêu nguyên nhân, đề xuất sửa nhỏ nhất.
```

```
Việc T28. Đặt VITE_API_URL trong frontend/.env.example, chạy npm run build. Grep mọi đường dẫn API trong frontend/src/services,
đối chiếu với route của host sau middleware. Liệt kê đường dẫn không khớp.
```

### T29 đến T32

```
Việc T29. Viết backend/Dockerfile nhiều tầng cho ComicMonolith.Api (sdk:8.0 build, aspnet:8.0 chạy), user không phải root,
cổng qua ASPNETCORE_URLS. Không copy .env vào image. Thêm .dockerignore.
```

```
Việc T30 và T31. Viết docker-compose.prod.yml: backend, sqlserver (volume dữ liệu, không publish 1433), nginx (80, 443).
Nginx phục vụ frontend đã build và chuyển tiếp các route API về backend cùng domain. Đọc biến từ .env.production.
Xong khi `docker compose -f docker-compose.prod.yml config` không lỗi.
```

```
Việc T32. Viết .env.production.example (giá trị giả): JWT, SQL, Cloudinary, SMTP, Google Client ID, SePay, FRONTEND_URL.
Thêm .env.production vào .gitignore.
```

### T33, T35

```
Việc T33. Viết docs/DEPLOY.md: chuẩn bị VPS (Docker, firewall chỉ 22/80/443, domain, HTTPS Let's Encrypt),
chạy compose, cập nhật phiên bản, việc cần làm ở Google OAuth, SePay/VietQR, CORS, checklist smoke test. Chỉ viết tài liệu.
```

```
Việc T35. Viết script backup SQL Server hằng ngày (8 database), giữ 7 bản, không chứa mật khẩu trong script,
kèm hướng dẫn sao chép ra ngoài VPS và khôi phục thử vào database tạm.
```
