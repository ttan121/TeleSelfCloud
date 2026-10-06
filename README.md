# TeleSelfCloud Desktop

Ứng dụng Windows quản lý kho tệp cá nhân trên Telegram theo mô hình **local-first**. Giao diện tổ chức tệp theo thư mục; TDLib kết nối trực tiếp với Telegram, còn chỉ mục và trạng thái truyền tải được lưu trên máy người dùng. TeleSelfCloud không vận hành backend hoặc proxy trung gian riêng.

README tổng hợp định hướng từ tài liệu đề xuất dự án, báo cáo chức năng và báo cáo cách xây dựng; phần hướng dẫn build dựa trên mã nguồn desktop trong repository.

## Mục tiêu sản phẩm

- Tổ chức và tìm lại tài liệu, ảnh, video, bộ cài theo cây thư mục thay vì duyệt lịch sử chat.
- Biểu diễn một tệp lớn thành **một mục logic**, dù dữ liệu được chia thành nhiều phần trên Telegram.
- Lưu checkpoint để tiếp tục truyền tải sau gián đoạn; kiểm tra kích thước và SHA-256 khi hoàn tất.
- Phục hồi danh mục từ manifest từ xa khi chỉ mục cục bộ bị mất.
- Giữ nguyên tệp nguồn sau khi upload.

Local-first cho phép sử dụng chỉ mục đã lưu trên máy; upload, download và đồng bộ từ xa vẫn cần Internet và tài khoản Telegram. Mỗi phần chịu giới hạn của Telegram và tài khoản đang dùng. Dự án không cam kết dung lượng vô hạn hoặc thay thế một hệ thống sao lưu độc lập.

## Trạng thái bản desktop

Đây là bản đang phát triển. Mã nguồn hiện có các thành phần cho:

| Nhóm | Phạm vi đã có trong mã nguồn |
| --- | --- |
| Tài khoản và kho | Đăng nhập qua TDLib; hồ sơ tài khoản; tạo/chọn kho và chuyển kho |
| Quản lý tệp | Duyệt/lọc/tìm kiếm; thư mục; đổi tên, di chuyển, yêu thích và thùng rác |
| Truyền tải | Chọn hoặc kéo thả tệp/thư mục; chia phần; hàng đợi; hủy, thử lại và checkpoint |
| Đồng bộ và phục hồi | Manifest có phiên bản; phục hồi chỉ mục; xử lý xung đột metadata và thao tác dở dang |
| Bảo vệ dữ liệu | AES-GCM cho nội dung khi bật mã hóa; bảo vệ khóa local bằng DPAPI; tùy chọn bảo vệ database/cache/metadata |
| Tiện ích | Xem trước; phát media qua loopback bằng player của Windows; kiểm tra và tái sử dụng nội dung trùng |

Có mã nguồn và kiểm thử tự động không đồng nghĩa mọi luồng đã được xác nhận trên tài khoản Telegram thật hoặc máy Windows sạch. Cần kiểm chứng đăng nhập, truyền tải, phục hồi và đóng gói trước khi coi đây là bản ổn định. Hướng dẫn hành vi và giới hạn chi tiết nằm trong [tài liệu cài đặt](docs/INSTALL_WINDOWS.md).

Kiểm tra bản mã nguồn đưa lên repository ngày 06/10/2026: build Release đạt 0 cảnh báo/0 lỗi; probe SQLite đạt 12/12; bộ xUnit đạt 820/821, không bỏ qua test. Lỗi còn lại là panel lịch sử metadata vượt chiều cao cửa sổ trong bố cục tiếng Việt 760 × 650; chạy riêng test vẫn tái hiện. Script publish sẽ dừng tại lỗi kiểm thử này cho đến khi được sửa.

## Kiến trúc

```text
WPF Desktop
    ├── Core: mô hình tệp, manifest, quy tắc chia phần
    └── Infrastructure
          ├── TDLib → Telegram: phần tệp và manifest từ xa
          ├── SQLite: chỉ mục, hàng đợi, checkpoint
          └── Transfer/Security: streaming, SHA-256, AES-GCM, DPAPI
```

| Thành phần | Công nghệ trong bản hiện tại |
| --- | --- |
| Desktop | C#, .NET 10, WPF/XAML, Windows x64 |
| Telegram | `TDLib.Native.win-x64` 1.8.67, giao diện JSON/native |
| Dữ liệu local | `Microsoft.Data.Sqlite.Core` 10.0.12; `SQLite3MC.PCLRaw.bundle` 2.4.0 |
| Bảo mật | AES-GCM, SHA-256, Windows DPAPI |
| Media | ASP.NET Core loopback; player mặc định của Windows |
| Kiểm thử | xUnit; adapter giả, tệp/DB tạm và probe native |

Phiên bản phụ thuộc được khai báo trong các file `.csproj`; NuGet cung cấp thư viện native khi restore. Không cần chép DLL thủ công vào repository.

## Chạy từ mã nguồn

Yêu cầu: Windows x64, .NET 10 SDK và Microsoft Visual C++ Redistributable x64 phù hợp với TDLib. Bản này dùng WPF và DPAPI của Windows.

```powershell
git clone https://github.com/ttan121/TeleSelfCloud.git
cd TeleSelfCloud
dotnet restore TeleSelfCloud.slnx
dotnet build TeleSelfCloud.slnx -c Release
dotnet run --project src/TeleSelfCloud.Desktop/TeleSelfCloud.Desktop.csproj -c Release
```

Trong ứng dụng, nhập `api_id` và `api_hash` của ứng dụng Telegram của bạn, đăng nhập và tạo/chọn kho riêng. Không đưa credential, mã đăng nhập hay mật khẩu vào mã nguồn. Dữ liệu chạy ứng dụng nằm dưới `%LOCALAPPDATA%\TeleSelfCloud\P0`; giữ dữ liệu này khi cập nhật nếu muốn giữ phiên, hàng đợi và chỉ mục.

## Kiểm thử và đóng gói

```powershell
# Kiểm thử tự động với dữ liệu tự sinh và adapter giả
dotnet test tests/TeleSelfCloud.Tests/TeleSelfCloud.Tests.csproj -c Release

# Kiểm tra SQLite native và khả năng bảo vệ database
./scripts/Test-SqliteCipherProvider.ps1

# Kiểm tra, build và publish bản Windows x64 self-contained
./scripts/Publish-WindowsX64.ps1
```

Script publish kiểm tra probe SQLite, bộ unit test và build Release trước khi xuất gói vào `artifacts/publish/`. Gói bao gồm runtime, thư viện native, hướng dẫn cài đặt và giấy phép bên thứ ba. Thư mục đầu ra không được commit lên Git.

`TeleSelfCloud.LiveAcceptance` là công cụ kiểm thử tích hợp riêng với Telegram thật, yêu cầu cờ `--run-live` và phiên đã đăng nhập. Công cụ có thể tạo/thay đổi dữ liệu từ xa; chỉ dùng với tài khoản/kho thử nghiệm chuyên dụng sau khi đọc mã và chọn chế độ phù hợp.

## Lộ trình theo kế hoạch

1. **Proof of concept:** đăng nhập TDLib; upload/download nhiều phần; xác minh checksum; checkpoint và phục hồi manifest.
2. **MVP desktop:** Explorer, thư mục, tìm kiếm, hàng đợi, tiếp tục truyền, đổi tên/di chuyển/thùng rác và phục hồi chỉ mục.
3. **Beta ổn định:** kiểm chứng mã hóa, khôi phục khóa, phục hồi trên máy sạch và đóng gói Windows x64.
4. **Mở rộng:** hoàn thiện preview/media, nhiều kho, khử trùng lặp và tích hợp Windows khi truyền tải/phục hồi đã ổn định.

Tiêu chí chấp nhận: tải về khớp SHA-256 nguồn; không truyền lại phần đã checkpoint; dựng lại danh mục từ manifest; không hiển thị tệp dở như hoàn chỉnh; thao tác metadata và xóa có trạng thái phục hồi rõ ràng.

## Dữ liệu và bảo mật khi đóng góp

Repository chỉ chứa mã nguồn desktop, mã kiểm thử tự sinh, cấu hình build, script cần thiết và tài liệu phân phối. `.gitignore` dùng danh sách cho phép để loại bản build, thư viện cài đặt, log, báo cáo chạy thử, file sửa tạm và dữ liệu cá nhân.

- Không commit `.env`, API hash, token, phiên Telegram, khóa, mật khẩu hoặc chứng chỉ riêng.
- Không commit database, cache, staging, bản sao lưu, tệp cá nhân hay dữ liệu thử nghiệm của người dùng.
- Dùng dữ liệu tự sinh trong kiểm thử; kiểm tra `git diff --cached` trước khi commit.
- Telegram Cloud Chats không tự cung cấp mã hóa đầu cuối cho kho tệp. Không coi DPAPI là cơ chế khôi phục khóa đa thiết bị; giữ cụm mật khẩu/bản khôi phục và bản sao độc lập cho dữ liệu quan trọng.

## Tài liệu phân phối

- [Cài đặt và sử dụng trên Windows](docs/INSTALL_WINDOWS.md)
- [Thành phần và giấy phép bên thứ ba](docs/THIRD_PARTY_NOTICES.md)
- [Cách cung cấp TDLib native](src/TeleSelfCloud.Desktop/native/win-x64/README.md)

Các file trong `licenses/` là giấy phép phụ thuộc cần cho phân phối, không phải tuyên bố giấy phép cho toàn bộ mã nguồn TeleSelfCloud.
