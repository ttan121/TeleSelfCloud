# Cài đặt TeleSelfCloud trên Windows x64

## Yêu cầu

- Windows 10 hoặc Windows 11 x64.
- Microsoft Visual C++ Redistributable 2015–2022 x64. TDLib native yêu cầu runtime này; gói TeleSelfCloud hiện không cài nó tự động.
- Không cần cài .NET riêng: bản phát hành Windows x64 đã kèm .NET 10 self-contained và ASP.NET Core 10 dùng cho máy chủ media cục bộ trên loopback.

Tải Visual C++ Redistributable x64 từ trang Microsoft: <https://aka.ms/vs/17/release/vc_redist.x64.exe>.

## Chạy ứng dụng

1. Cài Visual C++ Redistributable nếu máy chưa có.
2. Giải nén toàn bộ thư mục phát hành vào vị trí bạn có quyền ghi.
3. Mở `TeleSelfCloud.Desktop.exe`.
4. Giữ nguyên các tệp DLL, `licenses/`, `THIRD-PARTY-NOTICES.md` và `INSTALL_WINDOWS.md` bên cạnh ứng dụng.

Dữ liệu cục bộ được lưu trong `%LOCALAPPDATA%\TeleSelfCloud\P0`. Không xóa thư mục này khi cập nhật ứng dụng nếu bạn muốn giữ phiên Telegram, hàng đợi và chỉ mục cục bộ.

Mỗi hồ sơ local chỉ được một instance TeleSelfCloud sử dụng. Instance thứ hai hiển thị thông báo để bạn chuyển sang cửa sổ đang chạy và thoát trước khi migrate database, phục hồi hàng đợi hoặc mở phiên TDLib. Khóa hệ điều hành được giữ đến khi ứng dụng thoát và tự giải phóng khi tiến trình kết thúc; tệp `.profile.lock` vẫn còn trên đĩa. Không xóa tệp khóa để thử mở đồng thời hai instance. Khi cập nhật từ phiên bản cũ chưa hỗ trợ khóa, hãy đóng phiên bản cũ trước.

Upload lưu part đã nhận trước khi publish manifest. Nếu Telegram đã nhận manifest nhưng bước lưu local lỗi, retry dùng lại metadata đã chuẩn bị và các part đã nhận. Có thể có bản tin metadata lặp khi mất acknowledgment; ứng dụng chưa bảo đảm exactly-once trên Telegram.

Khi tìm lại part hoặc metadata đã gửi, ứng dụng quét lịch sử đến cuối và kiểm tra đúng kho, caption, độ dài và SHA-256. Lịch sử lớn có thể mất thời gian; có thể tạm dừng và thử tiếp tục. Nếu Telegram còn báo tài liệu đang gửi, ứng dụng chờ/retry theo policy thay vì gửi ngay bản trùng. Lịch sử sai cấu trúc hoặc xác nhận không khớp request làm thao tác dừng và giữ hàng đợi; hãy kiểm tra kho/đồng bộ trước khi thử lại.

## Thêm nhiều tệp hoặc thư mục

### Hủy và bỏ tác vụ khỏi danh sách truyền tệp

Chọn **Hủy** trên tác vụ để dừng và chuyển sang **Đã hủy**, rồi chọn **Xóa mục đã kết thúc** để bỏ mục đó khỏi danh sách. Việc refresh, chuyển trang hoặc mở lại ứng dụng không tự tạo tác vụ mới từ bản nháp chưa commit. Chỉ thao tác tải lên/tiếp tục do người dùng chủ động mới xếp công việc vào hàng đợi.

Hủy và xóa dòng hàng đợi giữ bản nháp, staging và các phần đã nhận để có thể phục hồi; không xóa tệp nguồn hoặc phần đã gửi lên Telegram. Xóa bản ghi/tệp là thao tác riêng.

Trên trang **Tệp**, mở menu **Chọn tệp** để chọn một hoặc nhiều tệp, hoặc chọn **Chọn thư mục** để thêm cả cây thư mục. Có thể kéo nhiều tệp/thư mục từ File Explorer vào vùng danh sách; thao tác kéo-thả bắt đầu chuẩn bị và xếp các tệp vào hàng đợi ngay. Thư mục gốc được đặt dưới thư mục hiện tại, giữ tên và cây con; cả thư mục rỗng cũng được ghi vào cấu trúc kho. Nếu bật mã hóa, cùng cụm mật khẩu đã xác nhận sẽ bảo vệ từng tệp bằng khóa nội dung riêng.

TeleSelfCloud quét và kiểm tra toàn bộ lựa chọn trước khi tạo thư mục từ xa; từ chối đường dẫn bị thiếu, symbolic link/reparse point và batch vượt 10.000 tệp. Tệp lỗi riêng lẻ không ngăn các tệp còn lại được xếp hàng; thông báo hiển thị số lỗi và tooltip liệt kê tối đa 20 tên để người dùng chọn lại. Nguồn gốc luôn được giữ nguyên. Nếu thao tác bị hủy giữa batch, các mục đã xếp hàng vẫn được giữ và có thể tiếp tục trong **Truyền tệp**.

## Phát nhạc và video

Với tệp đã commit trong kho đang kết nối, chọn **Xem trước** cho `.aac`, `.m4a`, `.mp3`, `.ogg`, `.opus`, `.wav`, `.m4v`, `.mkv`, `.mov`, `.mp4` hoặc `.webm`. TeleSelfCloud đọc các byte player yêu cầu từ Telegram qua loopback, rồi chuyển URL phiên ngắn hạn cho ứng dụng media mặc định của Windows. Giữ cửa sổ xem trước mở trong khi phát; đóng cửa sổ để kết thúc stream. Tua/phát phụ thuộc player và codec đã cài. Nếu player không hỗ trợ URL HTTP/range, hãy tải tệp về để mở bằng ứng dụng phù hợp.

Tệp mã hóa yêu cầu cụm mật khẩu khôi phục; TeleSelfCloud giữ khóa nội dung trong bộ nhớ của phiên preview và xác thực từng frame AES-GCM trước khi gửi plaintext cho player. Phát một phần chưa xác minh SHA-256 toàn tệp; dùng **Tải xuống** rồi **Xác minh bản ngoại tuyến** để kiểm tra toàn bộ. TDLib có thể giữ byte-range trong cache local; đóng preview dừng endpoint nhưng range đã được TDLib nhận có thể hoàn tất vào cache. Database, TDLib cache, profile và metadata local có các giới hạn bảo vệ riêng đã nêu ở phần mã hóa/dữ liệu local.

## Tiếp tục thay đổi thư mục sau gián đoạn

- Khi đổi tên hoặc xóa thư mục, ứng dụng lưu kế hoạch phục hồi trước khi phát hành các phiên bản metadata. Xóa thư mục đưa các tệp về thư mục cha; thao tác này không xóa nội dung tệp trên Telegram.
- Nếu thao tác lỗi hoặc bị hủy giữa chừng, mở lại đúng tài khoản và kho, rồi chọn **Tiếp tục thay đổi thư mục** trên trang tệp. Các phiên bản đã lưu được giữ nguyên. Nếu chỉ bước đồng bộ cấu trúc thư mục bị lỗi, nút này thử lại bước đó.
- Nếu một tệp hoặc cấu trúc thư mục đã thay đổi, ứng dụng dừng để giữ dữ liệu và kế hoạch phục hồi. Chọn **Xem thay đổi thư mục đang dở** để xem bản local còn khớp trước/sau kế hoạch, đã thay đổi, bị thiếu hoặc khác tài khoản. **Dừng các bước còn lại** giữ vị trí tệp và cấu trúc thư mục hiện tại, không hoàn tác các bước đã áp dụng. Chưa có tự động hợp nhất/rebase toàn bộ kế hoạch; không tự xóa journal để bỏ qua cảnh báo.
- Kế hoạch nằm trong thư mục `folder-operations` của hồ sơ tài khoản, chia theo chat ID của kho. Đóng ứng dụng trước khi sao lưu toàn bộ hồ sơ; giữ cả database, journal, session và khóa bảo vệ cùng nhau. Journal có metadata và đường dẫn cache, hiện chưa được mã hóa.
- Thử lại sau khi Telegram đã nhận metadata nhưng trước khi lưu local có thể gửi lại **cùng phiên bản metadata**. Không tải lại các part; đây chưa phải giao dịch phân tán có bảo đảm exactly-once.
- Khi chọn dừng, ứng dụng lưu quyết định trước, gửi cấu trúc thư mục hiện tại rồi lưu bản kế hoạch trong `folder-operations/<chat-id>/stopped` trước khi giải phóng journal đang dở. Nếu sync/ghi archive/xóa journal lỗi, quyết định dừng vẫn được giữ. Mở lại đúng kho và chọn **Hoàn tất việc dừng**: ứng dụng tiếp tục hoàn tất việc dừng, không chạy lại các bước chuyển tệp còn lại. Quyết định dừng không xóa các file/cache hoặc rollback các thay đổi mới hơn.
- Dừng giữ trạng thái **local** ngay lúc đó, không hoàn tác metadata Telegram đã nhận. Nếu xác nhận remote đã đến nhưng local chưa lưu được, đồng bộ sau này vẫn có thể nhập revision đã gửi và đổi lại vị trí tệp trong chỉ mục. Archive giữ cả Before/After để kiểm tra trường hợp này; dừng không phải giao dịch rollback phân tán.
- Archive giữ Before/After và checkpoint cũ để kiểm tra, hiện vẫn chứa metadata/cache paths chưa mã hóa và chưa có UI xuất/rotate. Không tự sửa hoặc xóa archive khi báo checksum khác/hỏng; sao lưu cả hồ sơ và phục hồi bản sao phù hợp. Journal schema 1 cũ không có trường quyết định dừng vẫn được hỗ trợ. Đồng bộ cấu trúc có thể phát hành lại snapshot khi local acknowledgment bị mất.

## Tải xuống và tệp phục hồi

- Lượt tải mới lưu checkpoint trong các tệp `.tsc-download-*.partial` cạnh đích tải, kèm hồ sơ `.tsc-download-*.json`. Hồ sơ phân biệt tài khoản, nội dung/part và đích tải; không đổi tên hoặc xóa các tệp này khi muốn tiếp tục download đang dở.
- Chỉ part đủ độ dài và khớp SHA-256 được dùng lại. Destination cũ chỉ được thay sau khi toàn bộ dữ liệu được xác minh. Lượt tải/khôi phục đang chạy khóa đích để hai thao tác không đồng thời ghi vào cùng tệp.
- Nếu mọi part của một mục còn trên máy, chọn tệp ở trang Files rồi nhấn **Khôi phục từ máy này** để chọn nơi lưu. Thao tác này không cần kết nối Telegram; ứng dụng xác minh từng part và SHA-256 toàn tệp trước khi thay destination. Với nội dung mã hóa, cần passphrase khôi phục. Nút chỉ hiện khi đủ part và mục không ở Thùng rác.
- Nếu có `.partial`, `.encrypted-payload` hoặc `.encrypted-payload.partial` từ phiên bản cũ, ứng dụng chỉ đọc/copy sang checkpoint mới rồi xác minh. Các tệp cũ được giữ nguyên cả sau thành công vì tên tệp không đủ chứng minh đó là dữ liệu của ứng dụng. Bạn có thể sao lưu/kiểm tra trước khi tự dọn chúng.
- Sai passphrase giữ lại ciphertext đã xác minh để lần thử lại không phải tải lại. Tệp plaintext tạm của lượt giải mã bị lỗi được dọn theo đường dẫn riêng; ứng dụng không xóa tệp `.decrypt.partial` đã có trước đó. Restore từ staged parts dùng tên `.tsc-restore-*.partial` riêng và dọn partial do thao tác hiện tại tạo ra khi bị hủy/lỗi; destination hiện có chỉ được thay sau khi kiểm tra toàn vẹn thành công.
- Các checkpoint/marker chưa được mã hóa riêng. Tệp `.tsc-download-*.lock` được giữ lại nhưng khóa hệ điều hành được giải phóng khi thao tác kết thúc/tiến trình thoát. Hồ sơ phục hồi hỏng được giữ nguyên và chặn retry; chưa có UI dọn/hợp nhất các download checkpoint mồ côi.

## Lỗi khởi động và quyền riêng tư chẩn đoán

Gói hiện dùng `sqlite3mc.dll` (SQLite3 Multiple Ciphers 2.4.0) thay `e_sqlite3.dll`, giữ đọc database SQLite cũ. Không trộn DLL từ gói cũ vào bản mới; giải nén toàn bộ package vào thư mục mới rồi dùng hồ sơ hiện có. Provider đã kiểm chứng native encryption/FTS/WAL. Chỉ thay provider không tự mã hóa database; dùng Cài đặt → Mã hóa catalog local để bật cho workspace đang hiển thị sau khi lưu backup và khởi động lại. Khóa metadata remote là một chức năng riêng.

- Lỗi khởi động hiển thị mã và report ID Việt/Anh, không hiện nguyên exception. Khi ứng dụng giữ được khóa hồ sơ, `startup-diagnostic.json` chỉ ghi phiên bản, thời điểm, phase, mã lỗi cố định và report ID; không có message, stack trace, API hash, passphrase hoặc đường dẫn tệp.
- Khi cập nhật, `startup-error.log` cũ được đọc dưới exclusive file lease, lưu thành `diagnostics/legacy-<id>.dpapi`, đọc/giải mã lại để xác minh rồi thay nội dung log gốc bằng marker không nhạy cảm. Archive cần cùng Windows user/máy để mở. Không gửi archive cho người khác; nó có thể chứa chi tiết nhạy cảm từ bản cũ. Giữ archive cùng backup hồ sơ nếu cần phục hồi diagnostic.
- Nếu log cũ bị khóa, là filesystem link, lớn hơn 4 MiB hoặc archive không ghi/xác minh được, khởi động dừng và giữ dữ liệu. Đóng bản ứng dụng cũ, kiểm tra quyền/dung lượng; với log quá lớn/link, sao lưu log đích thực ra vị trí riêng an toàn rồi di chuyển log/link khỏi `startup-error.log` trước khi thử lại. Không xóa database/session để sửa lỗi chẩn đoán.
- Queue chỉ lưu message cố định hoặc protocol token đã cho phép. Lỗi chi tiết khác được thay bằng hướng kiểm tra nguồn/đích, kết nối, quyền, dung lượng hoặc recovery. Lần mở store đầu tiên giảm thiểu `LastError` cũ trong transaction; giữ task/state/progress/attempts/timestamps. Không dùng bản cũ ghi queue cùng lúc.
- Migration này không bảo đảm xóa sạch byte cũ khỏi SQLite free pages/WAL, backup hoặc SSD. Catalog chỉ được mã hóa khi bật riêng theo hướng dẫn bên dưới; app lock là cổng giao diện, không thay thế mã hóa cache/temp hoặc catalog.

## Khóa ứng dụng

Trong **Cài đặt → Khóa ứng dụng**, đặt cụm mật khẩu để khóa cửa sổ khi rời máy, chọn thời gian tự khóa (1–30 phút hoặc tắt tự khóa), rồi dùng **Khóa ngay** khi cần. Khi bật, mỗi lần khởi động phải mở khóa trước khi ứng dụng khôi phục phiên Telegram; khóa màn hình Windows cũng khóa ứng dụng. Đóng ứng dụng từ màn hình khóa vẫn được phép. Queue đang truyền tiếp tục chạy, còn cửa sổ preview trong ứng dụng sẽ đóng khi khóa.

Ứng dụng chỉ lưu verifier PBKDF2-SHA256, không lưu cụm mật khẩu. Đây là cổng UI để ngăn người khác dùng phiên TeleSelfCloud còn mở trên desktop không khóa; nó **không mã hóa dữ liệu local**, không bảo vệ trước tiến trình chạy cùng Windows user/quyền quản trị, và không thể thu hồi plaintext đã mở bằng ứng dụng khác. Dùng riêng mã hóa catalog/nội dung và khóa Windows để bảo vệ dữ liệu.

Nếu quên cụm mật khẩu, thoát TeleSelfCloud, sao lưu toàn bộ profile, rồi xóa riêng `%LOCALAPPDATA%\TeleSelfCloud\P0\app-lock.json`. Lần mở kế tiếp sẽ không còn cổng UI. Thao tác này không phục hồi hay xóa khóa/dữ liệu đã mã hóa; không xóa database, session hoặc catalog.

Pointer phiên Telegram mới (`telegram-active-session.json`, `telegram-signin-session.json`, `telegram-session-move.pending.json`) được bảo vệ bằng DPAPI CurrentUser, nên tên account/session và đường dẫn move không hiện dưới dạng JSON. Pointer JSON cũ vẫn được đọc để tương thích; lần ghi kế tiếp chuyển sang định dạng bảo vệ. Các pointer này gắn với Windows user hiện tại, không phải backup di chuyển session sang user/máy khác. Không xóa/chỉnh tay khi giải quyết lỗi; giữ nguyên profile và dùng chức năng đăng nhập/khôi phục trong app.

Preview trong cửa sổ và bản materialize dự phòng cho media được giải mã/ghép vào workspace `transient/preview` có marker dưới profile local. Luồng media thường đọc lazy theo range và không ghép toàn bộ file. Bản sao để **Mở bằng ứng dụng khác** nằm riêng trong `transient/opened`; cả hai dùng marker sở hữu và chỉ dọn workspace hợp lệ sau khi giữ exclusive profile lease. Preview đóng cửa sổ thì được xóa; bản mở ngoài chỉ đủ điều kiện dọn sau 30 ngày không thay đổi, và có thể giữ lâu hơn khi file đang mở hoặc lần khởi động kế tiếp chưa diễn ra. Nội dung vẫn là plaintext, kiểm tra file đang mở chỉ là best-effort, xóa không bảo đảm secure wipe. Bản tạm legacy tại `%TEMP%\TeleSelfCloud\preview` và `%TEMP%\TeleSelfCloud\opened` được giữ nguyên vì thiếu marker sở hữu.

Khi bật bảo vệ catalog local, part staging và payload mã hóa bằng passphrase được bảo vệ bằng khóa catalog theo từng part/payload, không đổi manifest path/schema. Lúc khởi động, mở account hoặc mở kho, ứng dụng xác minh hash/size rồi chuyển staging legacy từng tệp; mỗi tệp thay thế atomically nên lần mở sau có thể tiếp tục nếu app đóng giữa chừng. Nếu bảo vệ catalog tắt, staging mới tiếp tục ở dạng plaintext. Staging ngoài thư mục staging của catalog không thuộc migration/bảo vệ này. Trong lúc upload/restore, plaintext chỉ được materialize tạm dưới `transient/staging` của profile đang giữ lease và dọn khi consumer đóng handle; Windows file delete không bảo đảm secure wipe.

Manifest đang tham chiếu giữ staging để retry/resume. Khi mở profile/account/kho, ứng dụng có thể dọn các part/payload chuẩn bị không được manifest của đúng catalog tham chiếu và đã không thay đổi ít nhất 30 ngày. Nếu catalog không đọc được, đường dẫn có filesystem link hoặc thư mục chứa file/tên không nhận dạng, dữ liệu được giữ. Thumbnail cục bộ hiện không được tạo (TDLib upload truyền `thumbnail = null`).

## Mã hóa metadata và khóa phục hồi

- Trong **Cài đặt**, mở đúng kho rồi chọn **Bật và lưu khóa phục hồi**. Nhập và xác nhận passphrase tối thiểu 12 ký tự, lưu `.tsc-key.json` ngoài hồ sơ ứng dụng. Ứng dụng kiểm tra giải mã bản backup trước khi bật. Giữ thêm bản backup ngoài thiết bị và giữ passphrase riêng; chỉ sao chép DPAPI key sang máy khác không đủ phục hồi.
- **Xuất khóa phục hồi** tạo bản backup mới cho cùng khóa, không đổi khóa của kho hoặc vô hiệu backup cũ. Trên máy/Windows user mới, mở đúng tài khoản/kho rồi chọn **Phục hồi khóa metadata**, chọn backup và nhập passphrase. Sai kho, sai passphrase, dữ liệu hỏng hoặc khóa khác policy bị chặn và giữ dữ liệu hiện có. Nghiệm thu máy mới thực tế còn mở.
- Sau khi bật, manifest mới/được sửa dùng protocol caption 2 và snapshot thư mục dùng protocol 3. Lịch sử remote cũ vẫn đọc được và vẫn có thể chứa metadata rõ. Dùng build hỗ trợ các protocol này trên mọi thiết bị; build cũ dừng đồng bộ khi gặp protocol chưa hỗ trợ. Không tự sửa caption/schema hoặc xóa khóa để khắc phục.
- Mất/hỏng key record local chặn gửi metadata, không tự tạo khóa thay hoặc chuyển sang plaintext. Giữ `metadata-key-policy.json`, `metadata-key.dpapi.json` và toàn bộ profile khi backup. Khôi phục cùng khóa từ backup; policy hỏng cần phục hồi profile phù hợp. Chưa hỗ trợ disable/rotation hoặc mã hóa lại toàn bộ lịch sử.
- Tạo khóa cần kết nối Telegram và quét toàn bộ lịch sử để chặn tạo khóa mới khi kho đã có metadata được bảo vệ/protocol chưa hỗ trợ. Lỗi quét không được coi là kho trống. Chỉ bật khóa lần đầu trên một thiết bị rồi phục hồi cùng khóa ở các thiết bị khác; chưa có khóa phân tán để bảo đảm hai thiết bị cùng bật lần đầu không tạo hai khóa.
- `protected-outbox` giữ ciphertext đã ghi bền vững để retry cùng payload sau gián đoạn. Có thể tăng dung lượng theo số metadata thay đổi; chưa có UI dọn/export/rotate. Không xóa thủ công khi có thao tác đang dở.
- Metadata encryption không tự bật mã hóa nội dung tệp. Database/catalog/queue/journal/cache/staging/preview local vẫn chưa được mã hóa đầy đủ; caption/file ID, thời điểm, độ dài và lưu lượng remote vẫn có thể quan sát. Đây chưa phải bảo vệ E2EE/zero-knowledge cho toàn bộ sản phẩm.

## Giới hạn đã biết

- Một tài khoản có thể mở nhiều kho. Trong bảng **Kho**, chọn kho rồi **Mở kho**; **Thêm kho có sẵn** nhận chat ID của kênh TeleSelfCloud riêng tư do tài khoản sở hữu; **Tạo kho** tạo thêm kênh Telegram, giữ các kênh hiện có. Sau khi mở kho, bấm **Đồng bộ** để nhập catalog remote. Không mở/đổi kho khi đang chạy tác vụ.
- Kho đầu tiên giữ thư mục và database cũ của tài khoản; kho bổ sung có database/queue/staging/cache riêng. Session TDLib vẫn thuộc tài khoản. Queue của kho không mở được giữ và không tự chạy; quay lại kho để tiếp tục. Đổi kho đóng preview, bỏ lựa chọn/tệp chuẩn bị và giữ dữ liệu đã lưu. Các tên kho giống nhau phân biệt bằng chat ID.
- Sao lưu toàn bộ thư mục tài khoản, gồm `vaults.json`, thư mục `vaults` và dữ liệu kho đầu tiên. Registry hỏng không tự tạo mapping mới; cần phục hồi từ backup. Nếu hồ sơ chứa metadata từ kênh/tài khoản khác, mở kho bị chặn và giữ dữ liệu, chưa có công cụ tách hồ sơ lẫn kho tự động. Registry giới hạn 256 kho / 1 MiB và chưa mã hóa.
- Nếu tạo kênh đã được Telegram xác nhận nhưng xác minh/ghi local thất bại, giữ chat ID trong lỗi rồi thêm kênh đó bằng **Thêm kho có sẵn**. Không tự tạo lại để thử lỗi. Nếu mất phản hồi tạo kênh, chọn **Phục hồi tạo kho**: ứng dụng tìm đúng mã yêu cầu trong mô tả kênh, không gửi lại yêu cầu đã được ghi là dispatched. Nếu chưa có đúng một kết quả phù hợp, giữ journal và thử phục hồi sau. Chọn **Tìm kho** để tải danh sách chính và lưu trữ, tìm các kênh có marker TeleSelfCloud và quyền riêng tư phù hợp. Chọn một kết quả rồi Mở kho; không tự đổi kho khi chỉ tìm kiếm. Nghiệm thu nhiều kho với Telegram thật còn mở.

- Trong bảng Kho lưu trữ, **Đồng bộ** đọc phần lịch sử mới; **Quét lại toàn bộ** đọc lại lịch sử kho và giữ tệp local/cache/kế hoạch phục hồi. Quét không tìm thấy metadata không tự xóa dòng local. Metadata đã quan sát không bảo đảm các part vẫn tồn tại trên Telegram.
- Báo cáo đồng bộ được lưu riêng theo tài khoản/kho. Khi quét dở, dữ liệu đã nhập được giữ và checkpoint chưa tiến; **Thử lại đồng bộ đang dở** kiểm tra lại lịch sử, giữ chế độ full scan của lượt trước. Khi catalog đã hoàn tất nhưng gửi cấu trúc thư mục thất bại, nút thử lại chỉ gửi cấu trúc hiện tại, không quét lại catalog. Crash trong lúc quét chỉ giữ báo cáo lúc bắt đầu; lần thử lại xác minh lịch sử từ checkpoint đã lưu.
- Báo cáo hỏng được giữ dưới đuôi `.invalid` khi chọn quét lại toàn bộ; không sửa session/database/cache. Giới hạn báo cáo 8 MiB / 100.000 file ID, chưa có export/rotate UI. Báo cáo chỉ chứa ID/counters, vẫn là metadata local chưa mã hóa.

- Đồng bộ metadata chọn bản đã commit trước draft local, sau đó ưu tiên revision cao hơn và thời điểm cập nhật mới hơn. Nếu cả revision và thời điểm đều trùng, fingerprint SHA-256 của metadata đã chuẩn hóa chọn cùng một bản trên các thiết bị, không phụ thuộc thứ tự quét hoặc đường dẫn cache. Đây là chọn toàn bộ bản metadata; các thay đổi riêng lẻ từ hai thiết bị chưa được hợp nhất tự động.
- Nếu một revision đổi kích thước/hash nội dung của cùng file ID, đồng bộ dừng và giữ bản local/checkpoint cũ. Hãy sao lưu hồ sơ và kiểm tra lịch sử kho. Bản metadata từ tài khoản khác cũng bị chặn. Không tự sửa database để bỏ qua xung đột.

- Gói này chưa được kiểm thử trên Windows sạch; cần xác minh cài runtime và lần chạy đầu trước khi phân phối rộng.
- Mã hóa nội dung tệp là tùy chọn. Metadata, database ứng dụng và cache local chưa được bảo vệ đầy đủ; không coi toàn bộ sản phẩm là E2EE/zero-knowledge. Telegram không bảo đảm dung lượng không giới hạn.
- Tài khoản, mạng và giới hạn Telegram có thể ảnh hưởng việc đăng nhập, đồng bộ và truyền tệp.

## Xem và áp dụng metadata xung đột

- Trong trang Tệp, chọn một tệp đã commit thuộc kho đang kết nối, rồi chọn **Xem phiên bản xung đột**. Đồng bộ giữ lại cả hai bản khi chúng cùng revision nhưng khác tên/thư mục/Favorites/Archive/Hidden/Trash hoặc thời điểm sửa tệp. Đây không phải lịch sử đầy đủ của mọi lần sửa metadata. Có thể cần đồng bộ toàn bộ để tìm các bản cũ chưa từng được lưu local.
- Chọn bản muốn giữ rồi **Áp dụng bản tổ chức đã chọn**. Ứng dụng gửi revision mới, lấy các thuộc tính tổ chức từ bản đó nhưng giữ nội dung, part references, mã hóa và cache của bản hiện tại. Tệp có tác vụ Pending/Running/Paused phải được hoàn tất hoặc hủy trước. Thay đổi trạng thái Trash/Hidden/Archive có thể đưa tệp ra khỏi bộ lọc hiện tại.
- Kế hoạch được lưu trước khi gửi. Nếu bị gián đoạn, mở lại danh sách và thử đúng lựa chọn để gửi lại cùng metadata; không upload lại part. Sau khi local đã lưu bản được xác nhận, retry chỉ hoàn tất hồ sơ local. Metadata remote có thể lặp khi xác nhận đã đến nhưng local chưa lưu được.
- Nếu tệp đã thay đổi hoặc muốn đổi lựa chọn đang chờ, dùng **Áp dụng bằng revision mới**. Revision mới cao hơn bản hiện tại và các kế hoạch đã chuẩn bị; kế hoạch cũ được giữ trong lịch sử. Không tự hợp nhất các field mà chưa có lựa chọn người dùng.
- Lịch sử nằm ở `metadata-history/<chat-id>` trong hồ sơ tài khoản. File names được hash theo account/vault/file ID; checksum phát hiện corruption và write dùng tệp tạm + atomic replace. Hiện metadata/envelope trong hồ sơ này chưa được mã hóa và checksum không bảo vệ trước tiến trình cùng Windows user sửa dữ liệu. Đóng ứng dụng rồi sao lưu cả hồ sơ, gồm cả lịch sử và các outbox.
- Mỗi tệp giới hạn 256 snapshots và 256 kế hoạch thay thế, tối đa 64 MiB. Vượt giới hạn hoặc lịch sử hỏng sẽ giữ tệp và dừng thao tác liên quan; chưa có UI xuất/rotate lịch sử. Các bản cạnh tranh trên remote không bị xóa bởi lựa chọn này.

## Tìm kho và phục hồi tạo kho

Bật mã hóa catalog và phục hồi khóa theo phần Mã hóa catalog local bên dưới. Kho remote và catalog local có khóa/phạm vi riêng.

- **Tìm kho** tải cả danh sách chính/lưu trữ bằng loadChats đến khi TDLib báo hoàn tất, rồi đọc snapshot. Giới hạn 10.000 lượt tải mỗi danh sách / 100.000 chat mỗi snapshot; vượt giới hạn, lỗi mạng hoặc response sai làm dừng, không kết luận kho không tồn tại. Danh sách thay đổi trong lúc tìm có thể cần tìm lại. Có thể tìm/chọn kho ngay sau đăng nhập khi chưa có registry; nếu tìm thấy nhiều kho, kết nối ban đầu yêu cầu chọn rõ một kho.
- Mô tả kho cũ giữ nguyên; kho mới có thêm `Creation: <request ID>` để phục hồi dù tên kênh đổi. Không tự sửa/xóa marker của lượt tạo đang dở. Sao lưu `vault-creation.json`, `created-vaults`, `abandoned-vault-creations` cùng toàn bộ hồ sơ tài khoản; journal có checksum và chỉ chứa metadata, chưa mã hóa.
- Sau khi gửi yêu cầu tạo, ứng dụng không tự gửi lại. Phục hồi xác minh account/quyền riêng tư và mã yêu cầu; journal Confirmed giữ chat ID trước khi đăng ký local. Lỗi đăng ký/lưu receipt giữ journal để thử lại mà không tạo thêm kênh.
- **Dừng phục hồi** cần xác nhận, chỉ lưu trữ journal local và giữ mọi kênh remote. Kênh có thể đã được tạo dù chưa thấy trong kết quả; tìm kho/kiểm tra Telegram trước khi chủ động tạo kho khác để tránh kênh trùng. Journal hỏng không được bỏ qua tự động; giữ tệp và phục hồi backup trước khi tạo tiếp.

### Catalog local cũ tham chiếu nhiều kho

Khi tài khoản chưa có registry kho và catalog cũ có manifest không thể gán an toàn cho chat đang xác minh, kết nối sẽ dừng, giữ catalog và hiện **Phục hồi vào kho riêng**. Draft hợp lệ thuộc đúng account và chưa có tham chiếu remote vẫn có thể đăng ký vào kho bình thường. Sau khi xác minh lại quyền truy cập, hộp thoại nêu số mục local cần giữ và yêu cầu xác nhận. Thao tác đăng ký một catalog rỗng tại thư mục vault riêng; catalog cũ, hàng đợi, checkpoint và staging ở nguyên thư mục account. Ứng dụng quét toàn bộ lịch sử chat đã xác minh để dựng catalog mới. Khóa metadata DPAPI cũ của đúng account/chat được xác minh rồi sao chép và xác minh ở vault mới. Khóa hợp lệ thuộc chat khác được giữ ở nguồn; hồ sơ khóa thiếu, hỏng hoặc sai account làm dừng phục hồi, không tạo khóa thay thế.

Sau đó dùng **Xem catalog local cũ** để duyệt dữ liệu được giữ. Đây là chế độ chỉ đọc: thao tác Telegram/upload/đổi metadata/bỏ mục bị tắt và các handler từ chối sửa hàng đợi/catalog, kể cả nút Hủy trên từng dòng. **Khôi phục local** chỉ hiện khi mọi part staging của tệp có sẵn và được kiểm tra lại trước khi ghi đích; dữ liệu thiếu part vẫn cần đúng kho remote hoặc backup. Chọn **Quay lại kho đang kết nối** để xác minh lại kênh và trở lại catalog mới. Nút mở khóa/đóng ứng dụng vẫn dùng được khi app lock đang bật.

Khi đăng nhập lại tài khoản đã có registry, dữ liệu từ workspace shared chỉ được nhập vào kho primary nếu chứng minh đúng chat hoặc là draft hợp lệ chưa có tham chiếu remote. Mục của chat khác/không rõ được giữ ở shared cùng hàng đợi. Checkpoint shared được giữ và không nhập sang kho đã đăng ký, để không bỏ qua lịch sử chưa có trong catalog đích. Kho active được mở theo registry sau bước nhập này; catalog account cũ đã được giữ lại không nhận dữ liệu nhập mới.

Nếu một tệp đã có ở hồ sơ đích nhưng khác metadata/tham chiếu remote hoặc thiếu bản sao part local đã xác minh thuộc staging riêng của kho đích, bước nhập dừng và giữ cả hai hồ sơ. Bản đích còn trỏ vào staging của workspace chung cũng được giữ để xử lý thay vì bỏ nguồn. Cùng hash nội dung chưa đủ để bỏ bản nguồn. Đóng ứng dụng và sao lưu đầy đủ trước khi xử lý xung đột; hiện chưa có màn hình hợp nhất hai bản trong luồng nhập shared này.

Luồng tách kho hiện dừng nếu catalog hoặc cache cũ bật mã hóa local chưa thể kế thừa an toàn; không tắt/mã hóa lại chúng tự động. Tính năng này có test tự động và UI fixture, nhưng chưa được chạy trên hồ sơ Telegram thật. Trước khi tiếp tục, đóng ứng dụng và sao lưu đầy đủ hồ sơ. Hủy xác nhận giữ registry chưa tạo; lỗi giữa chuẩn bị giữ journal intent để có thể thử tiếp, không chép đè catalog nguồn.

## Mã hóa catalog local

1. Mở đúng workspace/tài khoản/kho, vào **Cài đặt → Mã hóa catalog local** và kiểm tra phạm vi đang hiển thị. Thao tác chỉ bảo vệ catalog đó; các catalog khác giữ chính sách hiện có.
2. Chọn **Lưu khóa và bật khi khởi động lại**, nhập/xác nhận passphrase ít nhất 12 ký tự, lưu tệp `*.tsc-db-key.json` mới ngoài `%LOCALAPPDATA%\TeleSelfCloud\P0`. Không ghi đè backup hiện có. Giữ bản sao bên ngoài thiết bị và giữ passphrase riêng.
3. Backup được đọc lại/giải mã/xác minh trước khi lưu policy/key local. Catalog đang mở chưa được mã hóa ngay; đóng ứng dụng bình thường và khởi động lại. Trong quá trình chờ restart có thể tiếp tục sử dụng workspace, nhưng mở lại catalog đã yêu cầu bật sẽ yêu cầu hoàn tất migration.
4. Lần khởi động sau kiểm tra catalog shared, primary account và vaults trước khi mở SQLite stores hoặc TDLib. Migration tạo snapshot, mã hóa bản sao, kiểm tra schema/hàng và thay database có journal phục hồi. Khi xong, Settings báo catalog đã mã hóa. Giữ nguyên profile nếu migration báo lỗi; không xóa journal/key/backup để thử tạo key mới.
5. **Xuất khóa phục hồi** tạo backup mới đã xác minh với passphrase bạn chọn. Nó chỉ chứa khóa, không chứa catalog/tệp/phiên Telegram. Khi sao lưu hoặc chuyển máy, đóng ứng dụng và sao lưu toàn bộ hồ sơ kèm backup khóa của từng catalog; không chỉ sao chép `manifests.db` đang mở vì có thể còn WAL.
6. Khi DPAPI key không mở được (ví dụ Windows user/máy mới), ứng dụng hiển thị **Phục hồi khóa database** trước khi mở catalog. Chọn backup đúng catalog và passphrase. Sai backup/passphrase giữ các tệp hiện có; hủy thì ứng dụng thoát, không mở catalog bằng plaintext. Settings cũng có nút phục hồi wrapper hiện có. Key này không phục hồi Telegram session/credential hoặc remote metadata key.

Phạm vi: SQLite manifest/index, queue, folder/tombstone và sync checkpoint của catalog. JSON cache-verification/registry/journals khác, staging, thumbnail/preview/temp, TDLib/session và backups cũ có chính sách riêng; không coi đây là mã hóa toàn bộ thư mục profile. Migration dở có thể giữ plaintext scratch/originals đến khi resume; original archives chỉ thay raw copies sau decrypt/hash verification. Xóa file không secure wipe SSD/free blocks. Có giới hạn 256 account roots/256 additional roots mỗi account, 1 triệu hàng/table và hạn mức field khi xác minh; catalog vượt giới hạn dừng và giữ copies. Chưa nghiệm thu trên máy Windows sạch/máy mới hoặc profile thật; progress startup hiển thị kiểm kê/bước xử lý và số catalog đã biết, không ước lượng phần trăm/thời gian. **Dừng khởi động** chờ bước đang chạy; nếu đã ghi intent thay catalog, ứng dụng hoàn tất thay và xác minh backup của catalog đó rồi thoát trước khi mở stores/Telegram. Không tự xóa bản phục hồi khi đang chờ.
Nếu workspace thay đổi trong khi nhập passphrase/chọn backup, thao tác lưu/phục hồi khóa bị chặn trước khi ghi. Mở lại Cài đặt của đúng workspace rồi thử lại. Một thao tác đã bắt đầu ghi vẫn gắn với scope ban đầu; thông báo hoàn tất phân biệt scope đó với workspace hiện tại.

Khóa metadata: nếu kho/khóa đang dùng thay đổi khi nhập thông tin hoặc chờ xử lý, thao tác không cài khóa vào workspace mới. Nếu backup đã lưu cho scope ban đầu, thông báo nêu rõ backup được giữ và chưa cài khóa; mở lại đúng kho rồi thử lại. Recovery khi mất policy chỉ tạo lại policy nếu KeyId/proof/account/chat của bản ghi khóa hiện có khớp backup. Nếu bản ghi ràng buộc cũng hỏng, giữ các tệp và phục hồi verified-profile backup trước; không xóa record để thử tạo replacement key.

Mốc local-record cipher có API đọc/ghi cache-verification được bảo vệ, nhưng **chưa nối vào Desktop hoặc migrate JSON cũ**. SQLite encryption hiện tại không tự mã hóa cache JSON/registry/journals. Không sửa/cấp cipher bằng tay cho cache đang dùng; keyed reader từ chối plaintext và cần migration đã xác minh trước. Phạm vi hiện tại của Settings giữ nguyên.

Cache JSON migration backend đã có journal/snapshot/switch/verify và phục hồi sau gián đoạn, nhưng chưa nối startup/Settings. Gói hiện không tự migrate cache JSON của hồ sơ; đừng xóa journal/raw copies của thao tác đang dở. Những giới hạn và phạm vi của SQLite Settings vẫn giữ nguyên.

## Bảo vệ JSON xác minh cache

Sau khi catalog của workspace báo mã hóa sẵn sàng, vào Cài đặt và chọn **Bảo vệ metadata cache sau restart**. Thao tác dùng lại khóa và backup phục hồi catalog hiện có, không tạo khóa khác. Đóng/khởi động lại để migrate snapshot/cache JSON trước khi mở stores hoặc Telegram; Settings báo JSON xác minh cache đã mã hóa sau khi xong. Session đang chạy giữ cache store cũ tới khi đóng; không mở lại workspace có request đang chờ trước restart.

Sao lưu toàn bộ closed profile, gồm cache-record-policy.tsc, cache-record-migration.tsc và cache-record-migrations, cùng catalog key backup. Sai/thiếu key/policy/journal hoặc state hỏng dừng và giữ dữ liệu, không ghi plaintext fallback. Metadata verification của tệp đã chuyển vào hồ sơ tài khoản được invalidated để xác minh lại theo đường dẫn mới; các rows không liên quan được giữ. Chỉ bảo vệ JSON trạng thái xác minh, không mã hóa bytes của file cache/staging, registry/journals khác, preview/temp hay TDLib. Các ghi chú backend-only ở mốc trước là lịch sử; luồng này đã nối startup/Settings/store APIs nhưng native/new-user/live/large-profile gates vẫn mở.

### Bảo vệ danh sách kho theo tài khoản

Trong Cài đặt, mục **Bảo vệ danh sách kho** hiển thị riêng ID tài khoản và toàn bộ kho đã đăng ký của tài khoản đó. Trước khi bật, mở kho chính của tài khoản và bật mã hóa catalog local, lưu khóa phục hồi ra ngoài hồ sơ, rồi khởi động lại để catalog sẵn sàng. Sau đó chọn **Bảo vệ danh sách kho khi khởi động lại**. Có thể chọn một kho phụ khi bật; registry và khóa vẫn thuộc catalog chính của tài khoản, không thuộc catalog kho phụ.

Thao tác chỉ lưu yêu cầu; khởi động lại để chuyển đổi danh sách kho trước khi mở stores/session. Giữ hồ sơ, khóa phục hồi catalog chính và các tệp migration. Tên kho, primary/active mapping và creation receipt giữ nguyên; bản gốc được lưu mã hóa và kiểm chứng trước khi dọn bản raw. Nếu thiếu/hỏng key, policy, latch hoặc registry, giữ dữ liệu và phục hồi thay vì xóa để tạo mapping mới. Backup khóa catalog chính phục hồi đúng master; backup khóa của kho phụ không thay thế nó. Nội dung tệp, staging, storage-channel setting, journal/reports và các JSON khác có phạm vi bảo vệ riêng. Tính năng không bảo đảm xóa byte cũ khỏi ổ đĩa hay chống rollback/tiến trình cùng user.

### Dedup upload (tái sử dụng nội dung đã xác minh)

Sau khi chọn tệp, checkbox **Tái sử dụng nội dung từ xa đã xác minh** mặc định tắt. Bật riêng cho upload mới nếu muốn tìm candidate trong đúng account/kho. Tệp thường: nếu layout part khác, ứng dụng xác minh remote rồi chia lại staging theo layout của candidate, giữ FileId và metadata riêng; cần thêm dung lượng local. Tệp mã hóa: nếu tìm thấy candidate cùng logical size/SHA, ứng dụng hỏi cụm mật khẩu khôi phục của bản cũ; passphrase của lần upload mới được dùng để bọc lại file key cho mục mới. Xác minh tất cả part/ciphertext và xác thực giải mã toàn bộ trước khi copy. Hủy hoặc nhập sai passphrase sẽ bỏ qua candidate và giữ ciphertext đã staging để upload thường. Candidate vượt giới hạn part hiện hành không được dùng. Giữ staging cũ để phục hồi đến khi tác vụ mới hoàn tất.

Xác minh có thể tải toàn bộ nội dung remote trước, nên chưa bảo đảm giảm tổng băng thông. Part-copy dùng file Telegram đã có để tạo message mới với caption/FileId riêng; không chia sẻ locator message của mục cũ. Copy intent và checkpoint được lưu local. Khi pause/restart/retry, service phục hồi saved intent vẫn hoạt động dù checkbox hiện tắt; copy mất ack được tìm theo caption/content của mục mới trước resend. Nếu source-copy bị mất hoặc scope sai, giữ draft/staged data; không tự đổi sang upload trên acknowledgment chưa rõ. Native ownership/bandwidth và restore/sync/delete acceptance còn mở; encrypted reuse và native/live different-layout acceptance chưa hoàn tất F10.

Nếu muốn chuyển phần copy còn lại sang upload thường, dừng queue, chọn tác vụ upload lỗi/đã pause/đã hủy trong **Transfers**, rồi chọn **Upload phần còn lại**. Nút chỉ xuất hiện khi draft còn copy intent và chỉ hoạt động trong phiên đã đăng nhập, đúng tài khoản/kho và profile đang được ứng dụng giữ độc quyền. Ứng dụng kiểm tra dữ liệu staging và tìm copy đã accepted của mục mới trước khi lưu chuyển đổi. Copy đã accepted giữ locator riêng; phần chưa accepted trở thành upload thường. Nếu không kiểm tra được lịch sử hoặc staging, phục hồi quyền truy cập/dữ liệu và mở lại tác vụ để thử lại. Thao tác không gửi part, không publish metadata và không tự chạy queue; chọn **Thử lại mục đã chọn** hoặc chạy queue khi sẵn sàng. Giữ bản sao staging cho đến khi hoàn tất.

Xóa vĩnh viễn trong **Thùng rác** yêu cầu xác nhận trong ứng dụng và không thể hoàn tác. Dừng queue và xử lý các tác vụ còn tham chiếu đến tệp trước khi xóa. Với kho mã hóa metadata, phải phục hồi đúng khóa nếu khóa bị thiếu/hỏng; ứng dụng dùng bản sao khóa thuộc đúng tài khoản/kho để kiểm tra các revision remote. Đổi kho, khóa, session hoặc sửa tệp khi hộp xác nhận đang mở sẽ yêu cầu chọn và xác nhận lại. Khi thao tác đã bắt đầu, requests và cập nhật local giữ scope kho ban đầu. Nếu thao tác dở dang hoặc kho đổi sau khi bắt đầu, quay lại kho ban đầu và đồng bộ để kiểm tra kết quả trước khi thử tiếp. Xóa bản dedup dùng locator message riêng của bản đó; chưa có nghiệm thu Telegram thật về lifetime của blob dùng chung.


