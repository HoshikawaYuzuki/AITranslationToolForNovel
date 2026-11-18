# BASI-001 — Kết quả nghiệm thu và bàn giao hoàn thiện

Cập nhật: 18/11/2026 (Asia/Saigon).

## 1. Phạm vi và kết luận

Chỉ BASI-001 — Foundation & Repository Bootstrap. Không triển khai hoặc đánh giá các giai đoạn khác.

**Verdict hiện tại: ACCEPT WITH ISSUES.** Foundation đã có bằng chứng build, automated tests, frontend và kết nối PostgreSQL hoạt động. Còn khoảng trống automated test cho configuration và yêu cầu làm rõ precedence. Không có lỗi Critical/High đã xác nhận còn chặn hoạt động Foundation.

Không được coi hạn chế socket, Docker pipe hoặc test runner trong sandbox Codex là lỗi code khi kết quả Windows đã chứng minh chạy được. Mock AI Provider là đúng kế hoạch, không thuộc phần cần sửa dưới đây.

## 2. Baseline và thay đổi chưa commit

- Branch: `basi-001-foundation`.
- HEAD kiểm tra: `6094b53`.
- Working tree ngày cập nhật có thay đổi: file báo cáo này, docker-compose.yml, src/Basi.Api/appsettings.Development.json.
- Hai thay đổi cấu hình đã được người dùng yêu cầu: Docker publish 5433:5432; backend development dùng Host=127.0.0.1;Port=5433.
- Không ghi đè các thay đổi này hoặc quay lại localhost:5432. PostgreSQL Windows 18 đang dùng cổng 5432; PostgreSQL Docker BASI 17 dùng cổng host 5433, cổng nội bộ 5432.
- Trong lượt cập nhật này auditor chỉ sửa file báo cáo; không sửa tests, source, configuration hoặc database.

## 3. Bằng chứng đã xác nhận

| Hạng mục | Kết quả | Nguồn và giới hạn |
|---|---|---|
| Backend solution build | PASS, 7 project, 0 warnings/errors | Auditor chạy dotnet build Basi.sln --no-restore -m:1 -nr:false tại commit 6094b53 |
| Backend automated tests | PASS: 21/21, 15 unit + 6 integration, failed 0, skipped 0, exit 0 | Log PowerShell Windows người dùng cung cấp trong pwsh1.txt; HEAD 6094b53, lúc đó chỉ báo cáo bị modified. Không phải auditor tự chạy thành công trong sandbox |
| Frontend check/build | PASS, check 0 errors/warnings, build exit 0 | Auditor chạy bằng Windows fnm Node v26.13.1, pnpm 12.21.0 |
| Backend startup | PASS, listening localhost:5000 | Log Windows người dùng cung cấp |
| Health khi DB offline | PASS, HTTP 200, database=disconnected, khoảng 1,15 giây | pwsh1.txt/pwsh2.txt của người dùng |
| Docker Compose config | PASS, exit 0 | Đã validate cả cấu hình cổng mới |
| PostgreSQL container | PASS, healthy và accepting connections | Log Windows người dùng cung cấp |
| Database/user bên trong container | PASS, basi_dev / basi | SELECT current_database(), current_user() qua psql trong container |
| Backend EF Core/Npgsql kết nối DB | PASS, HTTP 200, database=connected | Response Windows người dùng gửi sau đổi cổng, timestamp 2026-11-18T03:40:32.8936968Z; áp dụng working tree có cấu hình 5433, không phải baseline cấu hình 5432 |
| Environment variable override | Đã có bằng chứng thủ công | Khi đặt ConnectionStrings__DefaultConnection với Host=::1, log backend chuyển đích sang ::1. Khi đó kết nối DB vẫn lỗi, nhưng biến override có hiệu lực |

Không tuyên bố suite 21/21 đã được chạy lại sau đổi cổng: bằng chứng suite thuộc baseline 6094b53; health connected thuộc working tree đã đổi cổng. AI tiếp theo cần chạy lại suite sau phần sửa của mình.

## 4. Các lỗi cũ đã đóng

- Contract AiRequest khớp model (MaxTokens=2048, Temperature=0.7).
- README không còn yêu cầu chạy migration chưa triển khai trong BASI-001.
- Placeholder Assert.True(true) đã thay bằng 6 health integration tests qua WebApplicationFactory; file đã đổi tên HealthEndpointIntegrationTests.cs.
- Offline test assert chính xác disconnected và giới hạn thời gian <3s.
- Program.cs đã đăng ký appsettings.Local.json; hướng dẫn biến môi trường đã sửa thành ConnectionStrings__DefaultConnection.
- Credential development được giải thích rõ; không có bằng chứng secret production bị lộ.
- Handoff đã thống nhất số 21 test tại baseline. Số này phải cập nhật theo kết quả mới nếu tests thay đổi.
- Frontend EPERM không tái hiện ở lượt build thành công gần nhất; không tiếp tục coi là lỗi source.
- Backend kết nối DB đã thành công sau tách cổng; không cần reset password PostgreSQL Windows, xóa volume hay sửa database.

## 5. Công việc còn lại giao AI sửa

### R1 — Medium: Kiểm thử configuration đúng provider và host thực tế

**File chính:** tests/Basi.Tests.Unit/ConfigurationOverrideTests.cs; src/Basi.Api/Program.cs; cấu hình test liên quan.

**Vấn đề đã xác nhận:** Hai test EnvironmentVariable_Overrides_DefaultConnectionString và LocalJsonFile_Overrides_BaseAppSettings chỉ ghép dictionary bằng AddInMemoryCollection. Chúng không đọc biến môi trường thật, không đọc JSON thật, không kiểm tra composition của Program. Test có thể PASS dù bỏ đăng ký Local JSON trong Program.cs.

Đây là thiếu sót về phạm vi kiểm thử, không phải phản đối mock và không chứng minh chức năng override hiện đang hỏng. Không liên quan MockAiProvider; tuyệt đối không thay bằng API AI thật.

**Yêu cầu sửa:**

1. Kiểm tra environment variable qua provider thật, gồm ánh xạ ConnectionStrings__DefaultConnection.
2. Kiểm tra Local JSON qua file tạm/provider thật với tên và content root đúng cơ chế ứng dụng.
3. Có kiểm chứng trên host ứng dụng thực tế hoặc composition configuration được dùng chung trực tiếp với Program; không chỉ dựng một ConfigurationBuilder độc lập rồi lặp lại thuật toán mong muốn.
4. Xác minh configuration hiệu lực và connection string mà BasiDbContext nhận, không mở kết nối DB.
5. Kiểm tra thiếu file Local không làm startup thất bại, và Local có thể override cấu hình nền theo policy được chọn.
6. Test phải có ý nghĩa regression: loại bỏ đăng ký file Local hoặc làm sai cơ chế override phải khiến test tương ứng thất bại.
7. Không ghi file Local vào thư mục cấu hình thật của developer. Dùng thư mục tạm cô lập, cleanup sau test. Nếu sửa process environment, lưu/khôi phục bằng try/finally và tránh chạy song song gây ảnh hưởng test khác. Không thay biến môi trường hệ thống.
8. Dùng giá trị test không nhạy cảm; không yêu cầu AI API, database đang chạy hoặc dependency mới nếu công cụ hiện có đáp ứng.

**Nghiệm thu R1:** Tests kiểm chứng provider và wiring thực tế, chạy được offline; kết quả không phụ thuộc Local JSON hoặc môi trường cá nhân ngoài fixture.

### R2 — Làm rõ policy: Thứ tự ưu tiên configuration

**File chính:** src/Basi.Api/Program.cs:7–10, hướng dẫn development, tests configuration.

**Hiện trạng:** CreateBuilder nạp provider mặc định; AddJsonFile(Local) được thêm sau nên Local có thể ghi đè environment variables và command-line arguments.

Hành vi này không tự động là bug. Cần xác định và ghi rõ precedence thay vì báo lỗi chắc chắn.

**Yêu cầu xử lý:**

1. Kiểm tra yêu cầu hiện có trước khi chọn policy. Nếu không có yêu cầu khác, ưu tiên giữ hành vi hiện tại và ghi rõ Local được nạp sau các provider mặc định; không tự đổi policy chỉ để làm audit đẹp.
2. Thêm test có đồng thời các nguồn cấu hình xung đột để chứng minh thứ tự đã chọn, qua wiring thực tế của ứng dụng.
3. Nếu đề xuất đổi sang environment/command line ưu tiên hơn Local, trình bày rõ thay đổi hành vi và phạm vi; tuân thủ quy tắc phê duyệt contract/decision hiện có.
4. Không được tuyên bố environment luôn thắng nếu Local vẫn được đăng ký sau và chứa cùng key.

**Nghiệm thu R2:** Policy rõ ràng, tài liệu và test khớp implementation; developer biết giá trị nào được dùng khi có nhiều nguồn.

### R3 — Low, cải thiện tùy chọn: Tính cô lập offline DB test

Factory dùng cổng cố định 54329 và gọi nó là guaranteed/deterministic offline. Không thể bảo đảm cổng này trống trên mọi máy. Assertion disconnected hiện đã đúng; chưa có bằng chứng failure vì cổng bị chiếm.

Không bắt buộc thêm hạ tầng hoặc test container. Có thể chỉ điều chỉnh mô tả để ghi rõ giả định, hoặc cải thiện fixture cô lập nếu cần. Không biến mục này thành blocker mới.

### R4 — Bàn giao chính xác

Sau sửa R1/R2:

- Cập nhật CURRENT_TASK, PROJECT_STATUS, SESSION-001 bằng số test và lệnh thực sự chạy.
- Phân biệt kết quả auditor tự chạy với kết quả người dùng cung cấp; ghi commit/working tree áp dụng.
- Ghi cổng development 5433 và sự khác nhau với cổng nội bộ 5432 ở vị trí hướng dẫn phù hợp.
- Không ghi all findings resolved nếu R1 chưa kiểm tra wiring thật hoặc R2 còn chưa chốt.

## 6. Giới hạn phạm vi cho AI tiếp theo

Chỉ hoàn thiện BASI-001 theo R1/R2 và cập nhật handoff liên quan. Không triển khai tính năng giai đoạn khác. Không gọi API AI trả phí, không sửa database/migration, không xóa volume, không reset Git, không tự commit/push/deploy. Giữ các thay đổi cổng đang có. Không sửa CONTRACTS.md hoặc DECISIONS.md khi chưa có phê duyệt theo quy tắc repository.

Không cần thay stack, đổi SDK, viết lại Mock Provider hoặc xây thêm hạ tầng chỉ để xử lý giới hạn sandbox. Nếu sandbox không chạy được test, ghi NOT VERIFIED trong môi trường đó và dùng bằng chứng Windows đúng revision; không tự báo PASS.

## 7. Kiểm tra sau khi sửa

Chạy tuần tự, không restore/cài thêm dependency tự động:

```powershell
dotnet build Basi.sln --no-restore -m:1 -nr:false
dotnet test Basi.sln --no-restore -m:1 -nr:false --logger "console;verbosity=normal"
```

Chạy frontend check/build khi thay đổi liên quan frontend hoặc cần xác minh toàn bộ Foundation; dùng Windows fnm đã thống nhất, không coi phiên bản sandbox khác là lỗi dự án.

Nếu thay đổi startup/configuration wiring, xác minh lại health trên Windows với DB offline và online bằng cấu hình development 5433. Không tự khởi động/dừng/xóa database ngoài phạm vi được phép; có thể nhờ người dùng thực hiện kiểm tra runtime.

## 8. Quyết định hiện tại

**BASI-001: ACCEPT WITH ISSUES.** Foundation hoạt động theo bằng chứng build/test/runtime hiện có. Phần sửa còn lại là làm automated tests configuration phản ánh đúng cơ chế thật và làm rõ precedence; không phải sửa một lỗi kết nối DB còn tồn tại.

Sau khi R1/R2 đạt tiêu chí và build/test lại thành công, có thể xem xét nâng nghiệm thu BASI-001 lên ACCEPT. Báo cáo này không đưa ra quyết định về bất kỳ giai đoạn nào khác.
