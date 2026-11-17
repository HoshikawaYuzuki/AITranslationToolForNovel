# BASI-001 — Targeted Re-Audit

Ngày cập nhật: 17/11/2026 (Asia/Saigon).

## Phạm vi và repository

Chỉ kiểm tra BASI-001 và các lỗi từ báo cáo trước. Không đánh giá Task 2–6; loại trừ sai lệch phiên bản Node.js, pnpm và Docker giữa Windows và sandbox.

- Branch: `basi-001-foundation`.
- Commit được kiểm tra: `96be8fd`.
- Khi kiểm tra: working tree sạch, không có staged/unstaged diff hoặc file chưa theo dõi.
- Không sửa code, cấu hình, dependency, migration hoặc database trong audit.
- File báo cáo này được ghi đè theo yêu cầu riêng của người dùng sau audit.

## Bảng đối chiếu

| Lỗi trước đó | Trạng thái hiện tại | Bằng chứng | Cần làm thêm |
|---|---|---|---|
| Contract AiRequest không khớp code | Đã sửa | docs/CONTRACTS.md và src/Basi.Core/Models/AiRequest.cs thống nhất MaxTokens = 2048, Temperature = 0.7 | Không |
| README yêu cầu migration chưa tồn tại | Đã sửa | README ghi migration thuộc Task 2 | Không yêu cầu migration cho BASI-001 |
| MASTER_PLAN ghi trạng thái cũ | Đã sửa | Task 1 đã chuyển sang Completed | Việc nghiệm thu vẫn cần bằng chứng runtime |
| Integration test chỉ Assert.True(true) | Đã thay thế ở mức implementation | tests/Basi.Tests.Integration/PlaceholderTests.cs chứa HealthEndpointIntegrationTests với 6 test qua WebApplicationFactory | Chạy thành công để xác nhận runtime |
| Credential development chưa được giải thích | Đã làm rõ, còn lỗi hướng dẫn | appsettings.Development.json chú thích đây là credential development | Sửa hướng dẫn override cấu hình |
| Test suite treo, chưa có kết quả | NOT VERIFIED | Chạy lại vẫn dừng sau thông báo 1 test files matched, không có tổng kết; đã ngắt thủ công | Điều tra test host; chưa đủ căn cứ quy lỗi cho PostgreSQL |
| Health endpoint chưa kiểm chứng runtime | NOT VERIFIED | Request localhost:5000 bị từ chối quyền socket | Kiểm tra trong môi trường cho phép kết nối |
| Docker Compose chưa validate | Đã xác minh cấu hình | Docker Windows chạy compose config --quiet, exit 0 | Runtime PostgreSQL chưa xác minh |
| PostgreSQL connectivity | NOT VERIFIED | Docker CLI không kết nối được pipe dockerDesktopLinuxEngine | Kiểm tra Engine/DB trong môi trường đang hoạt động |
| Số test trong handoff không thống nhất | Còn tồn tại | CURRENT_TASK.md:13 ghi 12; PROJECT_STATUS và SESSION ghi 19 | Đồng bộ số liệu, phân biệt test tồn tại với test đã chạy |

## Vấn đề trong phần sửa mới

### Medium — Hướng dẫn override cấu hình không khớp implementation

`src/Basi.Api/appsettings.Development.json:14-15` hướng dẫn dùng `appsettings.Local.json` hoặc biến `BASI__ConnectionStrings__DefaultConnection`.

`src/Basi.Api/Program.cs` không đăng ký nạp file Local hoặc prefix `BASI__`. Biến tương ứng với configuration mặc định là `ConnectionStrings__DefaultConnection`.

Hướng sửa: dùng đúng biến mặc định, hoặc bổ sung đăng ký configuration phù hợp rồi kiểm chứng giá trị thực sự được đọc. Audit không tự sửa.

### Medium — Test offline không chứng minh hành vi offline

`tests/Basi.Tests.Unit/HealthCheckTests.cs:65` có test `HealthEndpoint_DatabaseField_IsDisconnectedWhenNoDb`, nhưng assertion tại dòng 78 chấp nhận cả `connected` và `disconnected`.

Hướng sửa: kiểm soát tình huống DB không khả dụng và assert chính xác `disconnected`; kiểm tra thời gian phản hồi có giới hạn.

### Bằng chứng nguyên nhân test runner

Chưa có bằng chứng độc lập xác nhận lời giải thích trong handoff rằng chuyển sang môi trường Testing đã giải quyết nguyên nhân test runner treo. Không suy ra nguyên nhân chỉ từ việc thêm appsettings.Testing.json.

## Build và test đã chạy

| Kiểm tra | Kết quả thực tế |
|---|---|
| dotnet test Basi.sln --no-restore -m:1 -nr:false | Build API và unit-test project thành công; test runner không trả tổng kết. Ngắt thủ công, exit 1. Không xác nhận 19/19 hoặc toàn bộ solution test hoàn thành |
| pnpm --dir src/frontend run check | PASS, 0 lỗi và 0 cảnh báo |
| pnpm --dir src/frontend run build | Exit 1, EPERM mkdir tại .svelte-kit/output/client/_app/immutable; chưa chứng minh regression source |
| Docker compose config --quiet | PASS, exit 0 |
| Docker Engine | Không kết nối được pipe dockerDesktopLinuxEngine |
| GET http://localhost:5000/api/health | Bị từ chối quyền socket trong môi trường audit; NOT VERIFIED |

Frontend dùng thư mục Windows fnm:

```text
C:\Users\pc\AppData\Roaming\fnm\node-versions\v26.13.1\installation
Node.js: v26.13.1
pnpm: 12.21.0
```

Docker CLI dùng:

```text
C:\Users\pc\AppData\Local\Programs\DockerDesktop\resources\bin\docker.exe
```

Check và build frontend lần này chạy tuần tự mà vẫn gặp EPERM. Vì vậy, nhận định trước đây rằng EPERM chắc chắn do chạy song song là chưa đủ căn cứ. Không tính sai lệch phiên bản công cụ là lỗi dự án.

## Regression và kiến trúc

Không phát hiện lỗi Critical hoặc regression kiến trúc BASI-001 qua các thay đổi đã đọc. Integration tests đã có implementation thực chất. Tuy nhiên, kết quả runtime còn giới hạn bởi test runner, quyền socket và lỗi ghi build artifact; không được coi các mục này là PASS.

## Kết luận nghiệm thu

- 4 mục sửa hoàn chỉnh ở mức code/tài liệu: contract, README migration, trạng thái Master Plan, thay placeholder integration test.
- Credential development đã được giải thích, nhưng hướng dẫn override còn sai.
- Docker Compose đã được xác minh cấu hình; chưa xác minh PostgreSQL runtime.
- Còn khoảng trống bằng chứng test suite và health endpoint; có hai vấn đề Medium cần sửa nêu trên.

**Verdict: INSUFFICIENT EVIDENCE.**

**BASI-001 chưa được xác nhận hoàn thành đầy đủ. Chưa đủ cơ sở nghiệm thu chính thức để chuyển sang BASI-002.**

Trước khi nghiệm thu:

1. Hoàn tất chạy test suite và lưu kết quả thực tế; xác minh health endpoint/runtime trong môi trường phù hợp.
2. Sửa hướng dẫn override configuration và kiểm chứng hiệu lực.
3. Sửa assertion/test setup cho trường hợp database offline.
4. Đồng bộ CURRENT_TASK với PROJECT_STATUS và SESSION; chỉ ghi Fully Verified khi có đủ bằng chứng.
5. Xác minh lại frontend build sau khi giải quyết lỗi EPERM của môi trường.
