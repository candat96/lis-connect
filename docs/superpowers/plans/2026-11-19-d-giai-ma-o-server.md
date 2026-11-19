# D — Giải mã ở server — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agent không giải mã kết quả nữa. Nó cắt dòng byte máy gửi thành từng **bản tin thô** tự đủ, ghi xuống đĩa rồi gửi lên. Server lưu bản tin thô, giải mã bằng cùng bộ driver và đưa kết quả qua `IngestService`. Sửa một lỗi đọc kết quả chỉ cần cập nhật server, và bản tin cũ giải mã lại được.

**Architecture:**
- Driver được thêm một tín hiệu mức đường truyền, `DriverOutput.Boundaries`. Tín hiệu phát ra **khi phía nhận của driver trở về trạng thái rảnh**: hết khung, hết khối dở, hết phiên. Nhờ vậy mỗi đoạn byte giữa hai ranh giới giải mã được một mình bằng một driver mới tinh.
- `InstrumentRunner` có thêm chế độ "bản tin thô" (chỉ agent dùng). Bộ gom `RawMessageCollector` cắt byte theo ranh giới. Bản tin hoàn tất được ghi đĩa **trước khi** gửi trả lời của lượt đó cho máy. `Readings` bị bỏ qua.
- Agent gửi lên `POST /agent/v1/raw-messages`, theo thứ tự và chống trùng bằng UUID agent sinh ra.
- Server ghi `raw_message` (CSDL phòng khám) rồi `RawMessageProcessor` giải mã. Giải mã + ingest + ghi trạng thái nằm trong **một giao dịch**, nên server chết giữa chừng thì giải mã lại an toàn.
- Lis.App không đổi.

**Tech Stack:** .NET 10 / C# 14, Dapper + Npgsql + DbUp (tenant migration 0010, master migration 0005), ASP.NET Core minimal API, xunit v3 + Shouldly + Testcontainers + FakeTimeProvider, `tools/Lis.Simulator`. Web: React + antd + TanStack Query + vitest.

**Spec:** `docs/superpowers/specs/2026-11-16-mot-server-agent-dieu-khien-tu-web-design.md`, mục 2 ("Điều phải tránh"), 5.D, 6, 9 ("Ranh giới bản tin thô", "Định nghĩa giống hệt", "Nguồn dữ liệu chuẩn", "Khóa chống trùng bản tin thô"). Nền là nhánh C (`docs/superpowers/plans/2026-11-19-c-kenh-lenh-agent.md`).

## Global Constraints

- Chạy .NET với `export PATH=$HOME/.dotnet:$PATH`. `dotnet build LisConnect.slnx` phải ra **0 warning, 0 error**. Máy phát triển rất tải: khi làm từng task chạy test có filter, cuối task chạy đủ project test bị đụng.
- **Hành vi giải mã của driver không đổi.** Với mọi input, `Send`, `Readings`, `Queries`, `Notes` giữ nguyên như trước. Chỉ được **thêm** `Boundaries`. Mọi test driver hiện có phải xanh mà không sửa assertion.
- **Lis.App không đổi hành vi**: vẫn `IngestReadingSink` ghi thẳng CSDL. Chế độ bản tin thô của `InstrumentRunner` chỉ bật khi truyền `IRawMessageSink`.
- **Mọi đường ghi kết quả từ máy**, kể cả giải mã lại, phải đi qua `IngestService` (khớp barcode, map kênh, cờ, "chờ thay" khi mẫu đã duyệt). Không viết đường ghi kết quả nào khác.
- **Không mất, không trùng.** Bản tin thô nằm trên đĩa agent cho tới khi server trả xác nhận đã lưu bản tin đó. Server chống trùng bằng `raw_message.id` (UUID do agent sinh). Một bản tin chỉ được ingest **đúng một lần** cho mỗi lượt giải mã, kể cả khi server chết giữa chừng.
- Server không giải mã được một bản tin thì vẫn lưu và đánh dấu, rồi trả 200 cho agent. Không có lỗi nào khiến agent gửi lại vô hạn với nhịp nhanh.
- Agent chỉ gửi bản tin của máy đang gán cho chính nó. Server kiểm quyền sở hữu như `/readings` hiện nay. Agent phòng khám khác thì không chạm được.
- Giải mã lại và giải mã thử: chỉ vai trò `quan-tri` và `truong-khoa`. Mỗi lần giải mã lại để lại dấu vết (ai, lúc nào, phiên bản bộ giải mã, kết quả).
- Không tương thích ngược với agent cũ: bỏ `/agent/v1/readings` và bảng master `agent_reading` (spec mục 10.6).
- Bản tin thô giữ 90 ngày. Không xoá bản tin đang `cho-giai-ma` / `dang-giai-ma` / `can-xem-lai`.
- Khoá agent không bao giờ nằm trong log, URL hay nội dung bản tin.
- Văn bản, lỗi, chú thích viết tiếng Việt có dấu. Dữ liệu test là dữ liệu giả, tên có chữ "Thử" hoặc "Mẫu".
- Mỗi task commit cục bộ một lần, thông điệp kết thúc bằng đúng một dòng `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Không push.

## Tên dùng chung (mọi task)

- Trạng thái giải mã (`raw_message.decode_status`):
  - `cho-giai-ma`: đã lưu, chưa chạy.
  - `dang-giai-ma`: đang chạy, do lượt giữ hàng rào (`decode_started_at` + `decode_kind` + `decode_attempts`, giao dịch chính giữ `FOR UPDATE` dòng này). Không đoán theo thời gian chạy dài: `RawMessageStore.RecoverAsync(grace = 1 phút)` chỉ xét dòng quá hạn ân hạn và lấy được khoá dòng (`FOR UPDATE SKIP LOCKED`, nghĩa là không còn lượt sống nào giữ). Lượt `tu-dong` mới claim dưới 3 lần thì về `cho-giai-ma` tự chạy lại; lượt `giai-ma-lai`, hoặc đã gián đoạn 3 lần, thì `can-xem-lai` kèm ghi chú "Giải mã bị gián đoạn {n} lần".
  - `da-giai-ma`: có ≥ 1 kết quả.
  - `khong-co-ket-qua`: giải mã xong, không ra kết quả nào, ví dụ chỉ có câu hỏi chỉ định hoặc byte rời.
  - `loi`: driver ném lỗi, bản tin dở dang có ghi chú lỗi, hoặc ingest ném lỗi.
  - `chi-luu`: họ giao thức `chi-nghe`, hoặc máy chưa chọn giao thức.
  - `can-xem-lai`: giải mã bị gián đoạn (lượt giải mã lại, hoặc lượt tự động đã gián đoạn 3 lần), không tự chạy lại.
- `raw_ref` của kết quả, QC và chưa ghép do bản tin thô sinh ra: `ban-tin:{raw_message.id}`.
- Phiên bản bộ giải mã: `RawMessageDecoder.Version` = `"{InformationalVersion của Lis.Core}"`.

## Review Focus

1. **ASTM gửi ACK frame cuối trước khi máy gửi EOT** nên không thể ghi đĩa trước ACK đó. Bản tin vẫn phải được ghi đĩa trước khi agent gửi bất kỳ byte nào sau ranh giới. Mất điện giữa ACK và EOT thì máy gửi lại. Test ở task 2: bản tin được lưu trước mọi `Send` cùng lượt có ranh giới.
2. **Byte chiều In không thuộc lượt nào**, gồm ACK/NAK máy trả khi LIS đang gửi chỉ định, rác ngoài khung, CR sau FS của HL7. Không được rơi: chúng dồn vào đầu bản tin kế tiếp, hoặc tự thành một bản tin `partial` sau 60 s im lặng. Driver mới đọc chúng phải không sinh kết quả sai. Test ở task 1 (corpus "byte rời") và task 2.
3. **Beckman AU gom kết quả qua nhiều khung.** Ranh giới chỉ phát khi `_pending` rỗng và không còn khung dở, nên bản tin AU là trọn một mẫu. Test ở task 1: chia ngẫu nhiên dòng byte AU nhiều khối, kết quả đường mới bằng đường cũ.
4. **Giải mã lại một bản tin mà mẫu đã duyệt** chỉ ra "chờ thay", không ghi đè. Giải mã lại bản tin QC hoặc chưa ghép thì không chèn thêm dòng trùng (khóa `raw_ref` + barcode + kênh). Test ở task 3.
5. **Server chết giữa lúc ingest** (ném lỗi sau reading thứ 2 của 3): giao dịch huỷ, trạng thái vẫn `cho-giai-ma`. Lần khởi động sau giải mã đúng một lần, không có reading nào ghi hai lần. Test ở task 3.

---

### Task 1: Ranh giới bản tin ở driver + bộ giải mã một bản tin + corpus đối chiếu (an toàn người bệnh)

**Files:**
- Modify: `src/Lis.Core/Drivers/IInstrumentDriver.cs` (thêm `DriverBoundary`, `DriverOutput.Boundaries`), `src/Lis.Core/Drivers/Hl7/Hl7MllpDriver.cs`, `src/Lis.Core/Drivers/Astm/AstmDriver.cs`, `src/Lis.Core/Drivers/Astm/AstmLink.cs`, `src/Lis.Core/Drivers/BeckmanAu/BeckmanAuDriver.cs`
- Create: `src/Lis.Core/Drivers/RawMessageDecoder.cs`
- Test: `tests/Lis.Core.Tests/Drivers/Corpus/DriverCorpus.cs`, `tests/Lis.Core.Tests/Drivers/BoundaryTests.cs`, `tests/Lis.Core.Tests/Drivers/RawMessageEquivalenceTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DriverBoundary(int Offset, bool Abandoned);`
    - Trong output của `Receive(data, now)`: `Offset` là vị trí **sau** byte cuối của lượt, tính trong `data`, nằm trong khoảng `0..data.Length`.
    - Trong output của `Tick(now)`: `Offset = 0`, nghĩa là "mọi byte đã nhận trước lần Tick này".
    - `Abandoned = true` khi lượt bị bỏ dở: hết giờ, ENQ/VT giữa chừng, khung quá cỡ, sai checksum dẫn tới bỏ phiên.
  - `DriverOutput.Boundaries : List<DriverBoundary>`, mặc định rỗng. `IsEmpty` tính cả trường này.
  - Quy tắc phát (chung cho mọi driver): phát **đúng một** ranh giới mỗi lần phía nhận chuyển từ "đang có dữ liệu dở" sang "rảnh". Dữ liệu dở là khung dở, khối dở hoặc phiên đang mở. Byte đến khi đang rảnh mà không mở lượt mới thì không phát gì.
    - HL7: rảnh sau FS (`0x1C`). VT giữa khung, khung > 2 MB, hết 30 s đều cho `Abandoned`. CR sau FS là byte rời.
    - ASTM: rảnh sau EOT kết thúc phiên nhận (ENQ..EOT). ENQ giữa phiên (ranh giới đặt **trước** ENQ mới) và hết 30 s cho `Abandoned`. ACK/NAK máy trả trong phiên LIS→máy là byte rời.
    - Beckman AU: rảnh khi không còn khung dở **và** `_pending` rỗng. Hết 3 s khung dở và hết 30 s `_pending` cho `Abandoned`.
    - ListenOnly: không bao giờ phát.
  - `public static class RawMessageDecoder`
    - `public static string Version { get; }`
    - `public static RawMessageDecodeResult Decode(IInstrumentDriver freshDriver, ReadOnlySpan<byte> bytes, DateTimeOffset receivedAt)`
    - Cách chạy: `Reset()` → `Receive(bytes, receivedAt)` → mọi `Queries` được trả bằng `AnswerQuery(q, [], receivedAt)` (bỏ `Send`) → `Tick(receivedAt + 5 phút)`. Gom `Readings`, `Queries`, `Notes`, `Boundaries` của mọi bước.
    - `public sealed record RawMessageDecodeResult(IReadOnlyList<DriverReading> Readings, IReadOnlyList<string> QueryBarcodes, IReadOnlyList<string> Notes, bool Abandoned);` Trong đó `Abandoned` = có ranh giới bỏ dở.
  - `DriverCorpus` (test): `public static IEnumerable<CorpusStream> All()`, với `public sealed record CorpusStream(string Name, string Family, string? Model, string? DriverConfigJson, byte[] InBytes, int ExpectedReadings);`. Mỗi họ có ít nhất các dòng sau, byte lấy từ các test driver hiện có và từ bộ dựng bản tin của `tools/Lis.Simulator`:
    - một kết quả;
    - nhiều mẫu liền nhau;
    - QC;
    - câu hỏi chỉ định, kèm byte máy trả trong phiên LIS→máy;
    - lượt bỏ dở;
    - sai checksum;
    - byte rác trước lượt đầu;
    - riêng AU: kết quả nhiều khối, ít nhất 3 khối;
    - riêng ASTM: ENQ giữa phiên, và Access 2.

- [ ] **Step 1: Test đỏ, `BoundaryTests`.** Mỗi họ có ít nhất 4 ca:
  - `Hl7_ranh_gioi_ngay_sau_FS`: bản tin trong một lần `Receive` cho `Boundaries == [new(viTriSauFS, false)]`.
  - `Astm_ENQ_giua_phien_dat_ranh_gioi_truoc_ENQ`: Offset bằng vị trí ENQ thứ hai, `Abandoned = true`.
  - `Astm_het_gio_tick_phat_ranh_gioi_bo_do`: `Tick(now + 31 s)` cho ra `[new(0, true)]`.
  - `Au_nhieu_khoi_chi_mot_ranh_gioi_sau_khoi_E`.
  - `Byte_roi_khi_ranh_khong_phat_ranh_gioi`.
  - `ListenOnly_khong_bao_gio_phat`.
- [ ] **Step 2: Test đỏ, `RawMessageEquivalenceTests`** (phép thử spec mục 9). Với mỗi `CorpusStream` và với 50 cách chia khúc ngẫu nhiên (seed cố định, in seed khi đỏ), lần lượt:
  1. **Đường cũ**: một driver sống nhận các khúc theo thứ tự (mỗi khúc `now` tăng 10 ms). Câu hỏi được trả bằng danh sách kênh rỗng. Cuối cùng `Tick(+5 phút)`. Gom `Readings`.
  2. **Đường mới**: driver sống thứ hai nhận cùng các khúc. Cắt `InBytes` theo `Boundaries` theo đúng quy tắc của `RawMessageCollector` ở task 2: byte giữa hai ranh giới thành một bản tin, phần dư sau ranh giới cuối thành bản tin cuối `partial`. Mỗi bản tin được `RawMessageDecoder.Decode(DriverRegistry.Create(...)!, ...)`.
  3. Assert danh sách `Readings` của hai đường bằng nhau theo thứ tự, so cả `Barcode`, `IsQc`, `Items` (từng `ChannelCode`, `RawValue`, `Unit`, `InstrumentFlag`) và `RawText`. Số reading bằng `ExpectedReadings`.
  4. Assert nối mọi bản tin lại bằng đúng `InBytes`, không rơi và không lặp byte nào.
- [ ] **Step 3: Chạy và xác nhận đỏ.** `dotnet test tests/Lis.Core.Tests --filter "Boundary|RawMessageEquivalence"`.
- [ ] **Step 4: Làm.** Thêm `Boundaries` vào từng driver theo quy tắc trên.
  - Chỉ thêm việc ghi offset tại chỗ trạng thái chuyển sang rảnh. Không đổi luồng xử lý byte.
  - Viết `RawMessageDecoder`.
  - Họ nào không đạt phép thử với đơn vị nhỏ thì **dừng và báo** (spec: nâng đơn vị lên, không nới phép thử). Không tự nới assertion.
- [ ] **Step 5: Xanh.**
  - `dotnet test tests/Lis.Core.Tests` toàn bộ, kể cả mọi test driver cũ không sửa.
  - `dotnet test tests/Lis.Simulator.Tests`.
- [ ] **Step 6: Commit.** Thông điệp: `feat(core): driver báo ranh giới lượt truyền, giải mã một bản tin thô độc lập, đối chiếu đường cũ/mới`.

### Task 2: Gom bản tin thô ở `InstrumentRunner` + hàng chờ trên đĩa

**Files:**
- Create: `src/Lis.Connections/RawMessages/RawMessage.cs`, `RawMessageCollector.cs`, `RawMessageQueue.cs`
- Modify: `src/Lis.Connections/InstrumentRunner.cs`, `src/Lis.Connections/InstrumentConnectionManager.cs` (tham số tuỳ chọn `IRawMessageSink? rawSink = null`, truyền xuống runner)
- Test: `tests/Lis.Connections.Tests/RawMessageCollectorTests.cs`, `RawMessageQueueTests.cs`, `RawModeRunnerTests.cs`. Nếu chưa có project `Lis.Connections.Tests` thì đặt ở `tests/Lis.App.Tests` cạnh `InstrumentRunnerTests.cs`.

**Interfaces:**
- Consumes: `DriverOutput.Boundaries` (task 1).
- Produces:
  - `public sealed record RawMessage(Guid Id, Guid InstrumentId, long Seq, string Session, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, byte[] Bytes, bool Partial);`
    - `Id` = UUID v7 (`Guid.CreateVersion7()`).
    - `Seq` = tăng dần theo runner: `max(seqTrước + 1, unixMs * 1000)`.
    - `Session` = UUID v7 dạng chuỗi, sinh mỗi lần mở kết nối.
  - `public interface IRawMessageSink { Task SaveAsync(RawMessage message, CancellationToken ct); }`. Phải ghi xong xuống đĩa mới trả về.
  - `RawMessageCollector(Guid instrumentId, TimeProvider clock, TimeSpan idleFlush)`:
    - `IReadOnlyList<RawMessage> OnReceive(ReadOnlySpan<byte> data, IReadOnlyList<DriverBoundary> boundaries, DateTimeOffset now)`
    - `IReadOnlyList<RawMessage> OnTick(IReadOnlyList<DriverBoundary> boundaries, DateTimeOffset now)`: ranh giới từ Tick, hoặc hết `idleFlush` từ byte cuối mà bộ đệm còn byte; trường hợp sau sinh bản tin `Partial`.
    - `IReadOnlyList<RawMessage> OnDisconnect(DateTimeOffset now)`: còn byte thì sinh một bản tin `Partial`.
    - `void NewSession()`.
    - `Partial` = ranh giới `Abandoned`, hoặc sinh do im lặng / ngắt kết nối.
    - `idleFlush`: 60 s cho mọi họ, riêng `chi-nghe` là 2 s (khoảng lặng cắt bản tin, spec 5.D "Để người làm quyết").
  - `RawMessageQueue(string root)`, thư mục `{root}/{instrumentId:N}/`:
    - `Task SaveAsync(RawMessage m, CancellationToken ct)`: ghi `{Seq:D20}-{Id:N}.json` qua `.tmp` + flush + `File.Move`.
    - `IReadOnlyList<RawMessage> Peek(Guid instrumentId, int max)`: theo `Seq` tăng dần.
    - `IReadOnlyList<Guid> Instruments()`.
    - `void Delete(RawMessage m)`.
    - `int Count()`.
    - Tệp hỏng đổi thành `.loi`, không chặn hàng.
    - JSON camelCase, `bytes` là base64.
- Hành vi runner khi có `rawSink`:
  - Driver vẫn chạy đủ: `Send`, `Queries` qua `IOrderQueryService`, `Notes` → log, như cũ.
  - `Readings` **bị bỏ qua**, không gọi `IReadingSink`.
  - Trong `HandleAsync`, mọi bản tin hoàn tất của lượt này được `await rawSink.SaveAsync` **trước** khi ghi `Send` ra máy.
  - Tick, ngắt kết nối và dừng runner đều xả bộ gom.
  - Lỗi ghi đĩa: log lỗi, giữ bản tin trong bộ nhớ, thử ghi lại ở Tick sau. Không được làm dừng runner.

- [ ] **Step 1: Test đỏ.**
  - `Collector_cat_dung_ranh_gioi_va_byte_du_sang_ban_tin_sau`
  - `Collector_im_lang_60s_xa_ban_tin_partial`
  - `Collector_chi_nghe_khoang_lang_2s`
  - `Collector_ngat_ket_noi_xa_phan_du`
  - `Queue_ghi_nguyen_tu_va_doc_theo_seq`
  - `Queue_tep_hong_doi_ten_loi`
  - `Runner_raw_luu_ban_tin_truoc_khi_gui_ACK`: link giả ghi lại thứ tự "lưu" / "gửi"; dùng bản tin HL7 có ACK cùng lượt.
  - `Runner_raw_khong_goi_reading_sink`
  - `Runner_khong_raw_giu_nguyen_hanh_vi_cu`: `InstrumentRunnerTests` cũ vẫn xanh.
  - `Runner_raw_loi_ghi_dia_khong_dung_may`
- [ ] **Step 2: Chạy và xác nhận đỏ.**
- [ ] **Step 3: Làm.**
- [ ] **Step 4: Xanh.** Chạy test của task, `tests/Lis.App.Tests --filter InstrumentRunner`, và các test `Lis.Agent.Tests` đụng tới `InstrumentConnectionManager`.
- [ ] **Step 5: Commit.** Thông điệp: `feat(connections): gom bản tin thô theo ranh giới driver, ghi đĩa trước khi trả lời máy`.

### Task 3: Bảng bản tin thô và bộ xử lý giải mã ở CSDL phòng khám (an toàn người bệnh)

**Files:**
- Create: `src/Lis.Data/Migrations/0010_ban_tin_tho.sql`, `src/Lis.Data/RawMessages/RawMessageStore.cs`, `src/Lis.Data/RawMessages/RawMessageProcessor.cs`
- Modify: `src/Lis.Data/Ingest/IngestService.cs`, `src/Lis.Data/Ingest/InstrumentValuePipeline.cs` (chỉ khi cần truyền `raw_ref`)
- Test: `tests/Lis.Data.Tests/RawMessages/RawMessageStoreTests.cs`, `RawMessageProcessorTests.cs`, `tests/Lis.Data.Tests/Ingest/IngestServiceTests.cs` (thêm ca)

**Interfaces:**
- Consumes: `RawMessageDecoder` (task 1), `RawMessage` (task 2, chỉ dạng dữ liệu; Lis.Data không tham chiếu Lis.Connections, store nhận tham số rời).
- Produces:
  - Migration `0010_ban_tin_tho.sql`:
    - Bảng `raw_message`:
      - `id uuid PRIMARY KEY` (UUID agent sinh, không có DEFAULT)
      - `instrument_id uuid NOT NULL REFERENCES instrument(id) ON DELETE CASCADE`
      - `agent_id uuid NULL`
      - `seq bigint NOT NULL`
      - `session text NOT NULL`
      - `started_at timestamptz NOT NULL`
      - `completed_at timestamptz NOT NULL`
      - `stored_at timestamptz NOT NULL DEFAULT now()`
      - `bytes bytea NOT NULL`
      - `partial boolean NOT NULL`
      - `protocol_family text NULL`, `model text NULL`, `driver_config jsonb NULL`: ảnh chụp cấu hình máy lúc lưu
      - `decode_status text NOT NULL CHECK (decode_status IN ('cho-giai-ma','dang-giai-ma','da-giai-ma','khong-co-ket-qua','loi','chi-luu','can-xem-lai'))`
      - `decode_started_at timestamptz NULL`
      - `decoder_version text NULL`
      - `decode_result jsonb NULL`
      - `decoded_at timestamptz NULL`
      - Chỉ mục `(instrument_id, completed_at DESC)`, `(decode_status)`, `(stored_at)`.
    - Bảng `raw_message_decode`, là vết của từng lượt giải mã:
      - `id uuid PK DEFAULT uuidv7()`
      - `raw_message_id uuid NOT NULL REFERENCES raw_message(id) ON DELETE CASCADE`
      - `kind text NOT NULL CHECK (kind IN ('tu-dong','giai-ma-lai'))`
      - `actor_id uuid NULL REFERENCES app_user(id)`
      - `decoder_version text NOT NULL`
      - `protocol_family text NULL`, `model text NULL`, `driver_config jsonb NULL`
      - `status text NOT NULL`
      - `result jsonb NOT NULL`
      - `at timestamptz NOT NULL DEFAULT now()`
  - `IngestService`:
    - Thêm overload `Task<IngestOutcome> IngestAsync(InstrumentReading reading, NpgsqlConnection connection, NpgsqlTransaction transaction)`. Bản cũ `IngestAsync(reading)` gọi bản mới trong `db.InTransactionAsync`, hành vi không đổi.
    - Overload thêm tham số `ValueWriteMode mode` (`Live` | `Redecode`). `Redecode` (lượt `giai-ma-lai`): kết quả đã có thì giống → không ghi, khác → vào chờ thay, trùng chờ thay của bản tin khác → `Conflict` (không ghi), không bao giờ `PendingCleared`, không tự duyệt mẫu, không tạo lại dòng Chưa ghép đã xử lý. `Live` giữ hành vi cũ.
    - Không còn chống trùng `qc_result` / `unmatched_result` bằng `xmin`: lượt `tu-dong` chỉ chạy một lần nhờ hàng rào; lượt `giai-ma-lai` xoá dòng QC / Chưa ghép chưa xử lý của bản tin đó (`raw_ref = 'ban-tin:{id}'`) trước khi ingest.
  - `RawMessageStore(Db db)`:
    - `Task<RawMessageRow?> InsertIfAbsentAsync(RawMessageInsert m, CancellationToken ct)`: trả `null` khi id đã có, dùng `ON CONFLICT (id) DO NOTHING`.
    - `Task<RawMessageRow?> FindAsync(Guid id)`.
    - `Task<IReadOnlyList<RawMessageRow>> ListAsync(RawMessageFilter f)`, lọc theo `instrumentId`, `statuses`, `from`, `to`, `beforeCompletedAt` + `beforeId`, `limit ≤ 200`.
    - `Task<RawMessageRecovery> RecoverAsync(TimeSpan? grace = null, CancellationToken ct = default)`: thay `MarkInterruptedAsync`. `dang-giai-ma` quá `grace` (mặc định 1 phút) mà lấy được khoá `FOR UPDATE SKIP LOCKED`: lượt `tu-dong` có `decode_attempts < 3` → `cho-giai-ma`; còn lại → `can-xem-lai` ("Giải mã bị gián đoạn {n} lần").
    - `Task<IReadOnlyList<Guid>> PendingAsync(int max)`: `cho-giai-ma`, sắp theo `instrument_id, completed_at, seq` để giữ thứ tự truyền của từng máy.
    - `Task<int> DeleteOlderThanAsync(DateTimeOffset storedBefore)`: chỉ xoá các trạng thái kết thúc `da-giai-ma`, `khong-co-ket-qua`, `loi`, `chi-luu`.
    - Record `RawMessageInsert(Guid Id, Guid InstrumentId, Guid? AgentId, long Seq, string Session, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, byte[] Bytes, bool Partial)`.
    - `RawMessageRow` gồm đủ các cột, kèm `DecodeResult` dạng `JsonElement?`.
  - `RawMessageProcessor(Db db, IngestService ingest, IChannelMapLearning? learning, TimeProvider clock)`:
    - `Task<RawMessageRow> DecodeAsync(Guid id, string kind, Guid? actorId, CancellationToken ct)`. Chạy trong **một giao dịch**:
      1. `SELECT … FOR UPDATE` dòng `raw_message`. Đang `dang-giai-ma` do lượt khác thì bỏ, trả nguyên dòng.
      2. Đặt `dang-giai-ma` cùng `decode_started_at`, `decode_kind`, `decode_attempts + 1`. Bước này commit trong giao dịch riêng **trước**. `decode_started_at` đọc lại từ CSDL làm hàng rào lượt chạy.
      3. Mở giao dịch chính, mở đầu bằng `SELECT … FROM raw_message WHERE id AND decode_status='dang-giai-ma' AND decode_started_at=@claimedAt FOR UPDATE` (không thấy dòng thì huỷ lượt), rồi khoá mọi mẫu của bản tin theo `ORDER BY id`. Mọi `UPDATE` trạng thái ngoài giao dịch chính kèm cùng điều kiện hàng rào. Deadlock / lỗi tạm thời thì thử lại tối đa 3 lần (nghỉ 100 ms × lần thử, giải mã lại từ byte). Đọc cấu hình máy **hiện tại**: `protocol_family`, `model`, `driver_config`. Lượt tự động đầu tiên thì cấu hình này cũng là ảnh chụp lưu vào `raw_message`.
      4. Họ là `chi-nghe` hoặc trống thì chuyển `chi-luu`.
      5. Ngược lại: `RawMessageDecoder.Decode(DriverRegistry.Create(...)!, bytes, completed_at)`. Với mỗi reading, `ingest.IngestAsync(new InstrumentReading(instrumentId, r.Barcode, r.IsQc, completed_at, $"ban-tin:{id}", r.Items), conn, tx)` rồi cộng dồn `IngestOutcome`.
      6. Ghi `decode_result` = `{readings, stored, pendingReplacement, unmatched, qc, queries:[...], notes:[...], abandoned, error:null}`, kèm `decoder_version`, `decoded_at`, trạng thái theo bảng "Tên dùng chung", và một dòng `raw_message_decode`. Rồi commit.
      7. Sau commit: có `unmatched > 0` thì `learning.AutoLearnAsync(instrumentId)`. Lỗi ở bước này chỉ ghi log.
      - Driver hoặc ingest ném lỗi: rollback giao dịch chính. Giao dịch riêng ghi `loi` với `error` = thông điệp, kèm dòng vết. Không có reading nào được giữ.
    - `Task<RawMessageDecodeResult> PreviewAsync(Guid id)`: chỉ giải mã, không ghi gì.
    - `Task RecoverAsync(CancellationToken ct)`: gọi `RawMessageStore.RecoverAsync(grace 1 phút)`, rồi `DecodeAsync` từng id trong `PendingAsync`.
- [ ] **Step 1: Test đỏ** (Testcontainers, dùng các fixture của `IngestServiceTests`).
  - `Insert_trung_id_tra_null_khong_doi_dong_cu`
  - `Decode_hl7_ghi_ket_qua_qua_ingest_va_raw_ref_ban_tin`
  - `Decode_mau_da_duyet_chi_cho_thay`
  - `Giai_ma_lai_qc_va_chua_ghep_khong_chen_trung`
  - `Ingest_nem_loi_o_reading_thu_hai_rollback_het_trang_thai_loi`
  - `Server_chet_giua_chung_cho_giai_ma_duoc_giai_ma_dung_mot_lan`: giả bằng processor có hook ném lỗi sau reading thứ 2 rồi "khởi động lại" bằng `RecoverAsync`. Đếm `result_history` và `qc_result`.
  - `Dang_giai_ma_qua_5_phut_thanh_can_xem_lai_khong_tu_chay`
  - `Chi_nghe_chi_luu`
  - `Ban_tin_hong_checksum_khong_sinh_ket_qua_trang_thai_khong_co_ket_qua_hoac_loi`
  - `Xoa_90_ngay_khong_xoa_ban_tin_chua_xong`
  - `Ingest_ban_cu_khong_doi`: chạy lại toàn bộ `IngestServiceTests` cũ.
- [ ] **Step 2: Chạy và xác nhận đỏ.**
- [ ] **Step 3: Làm.**
- [ ] **Step 4: Xanh.** `dotnet test tests/Lis.Data.Tests` toàn bộ.
- [ ] **Step 5: Commit.** Thông điệp: `feat(data): bản tin thô, giải mã + ingest trong một giao dịch, giải mã lại không chèn trùng`.

### Task 4: API server — nhận bản tin thô, phục hồi, dọn, xem và giải mã lại

**Files:**
- Create: `src/Lis.Server/AgentApi/RawMessagesEndpoint.cs`, `src/Lis.Server/Api/RawMessagesEndpoints.cs`, `src/Lis.Server/RawMessages/RawMessageWorker.cs` (hosted service), `src/Lis.Server/MasterMigrations/0005_bo_agent_reading.sql`
- Modify: `src/Lis.Server/AgentApi/AgentEndpoints.cs` (bỏ `/readings`, `ReadingKey`), `src/Lis.Server/Tenancy/MasterStore.cs` (bỏ `Reserve/Complete/ReleaseReadingAsync`), `src/Lis.Server/Tenancy/TenantRegistry.cs` (`TenantServices` thêm `RawMessages`, `RawProcessor`), `src/Lis.Server/Program.cs`
- Test: `tests/Lis.Server.Tests/RawMessagesAgentApiTests.cs`, `RawMessagesApiTests.cs`. Bỏ các test `/readings` trong `AgentApiTests.cs`.

**Interfaces:**
- Consumes: task 3.
- Produces:
  - `POST /agent/v1/raw-messages`, header `X-Agent-Key`.
    - Thân: `{"messages":[{"id","instrumentId","seq","session","startedAt","completedAt","bytes"(base64),"partial"}]}`, tối đa 50 bản tin. Mỗi bản tin ≤ 4 MB sau giải base64, cả lô ≤ 16 MB (đặt `MaxRequestBodySize` riêng cho route).
    - Với từng bản tin, theo thứ tự:
      - máy không thuộc agent thì cho trạng thái `khong-thuoc-agent`, không lưu;
      - `InsertIfAbsentAsync`;
      - id mới thì `DecodeAsync(id, "tu-dong", null)` ngay;
      - id đã có thì trả trạng thái hiện có.
    - Trả 200 `{"results":[{"id","status","decodeStatus"}]}`, với `status ∈ {"da-luu","trung","khong-thuoc-agent","khong-hop-le"}`.
    - Giải mã lỗi vẫn là `da-luu`. Chỉ lỗi CSDL khi **lưu** mới trả 5xx cho cả lô. Bản tin lưu xong mà giải mã lỗi thì vẫn tính là đã lưu.
  - `RawMessageWorker`:
    - Khi server khởi động, gọi `RecoverAsync` cho mọi phòng khám.
    - Mỗi giờ, mỗi phòng khám gọi `DeleteOlderThanAsync(now - 90 ngày)`.
    - Mỗi 60 giây, gọi `RecoverAsync` (phục hồi `dang-giai-ma` không còn lượt sống) rồi mới `PendingAsync` để vớt bản tin `cho-giai-ma` sót lại.
  - `/api` (JWT, phòng khám lấy từ token):
    - `GET /api/raw-messages?instrumentId&status=loi,can-xem-lai&from&to&before&limit`, cho mọi vai trò xem log. Trả `[{id, instrumentId, instrumentName, seq, session, startedAt, completedAt, partial, size, decodeStatus, decoderVersion, summary}]`, trong đó `summary` là `decode_result` rút gọn.
    - `GET /api/raw-messages/{id}`: chi tiết, `hex` (tối đa 256 KB đầu), `decodeResult`, `decodes:[{kind, actorName, at, decoderVersion, status, result}]`.
    - `POST /api/raw-messages/{id}/preview`, cho `quan-tri` và `truong-khoa`. Trả `{readings:[{barcode, isQc, items:[{channelCode, rawValue, unit, flag}]}], queries, notes, abandoned}`, không ghi gì.
    - `POST /api/raw-messages/redecode`, cho `quan-tri` và `truong-khoa`. Thân `{"ids":[...]}` hoặc `{"instrumentId","from","to"}`, tối đa 500 bản tin, vượt thì 400 "Tối đa 500 bản tin mỗi lần — thu hẹp khoảng thời gian". Chạy tuần tự `DecodeAsync(id, "giai-ma-lai", userId)`. Trả `{count, stored, pendingReplacement, unmatched, qc, errors}`.
    - Thông báo lỗi: "Không tìm thấy bản tin" (404), "Bản tin đang được giải mã" (409).
  - Master migration `0005_bo_agent_reading.sql`: `DROP TABLE IF EXISTS agent_reading;`.
- [ ] **Step 1: Test đỏ.**
  - `Gui_lo_luu_va_giai_ma_tra_da_luu`
  - `Gui_lai_cung_id_tra_trung_khong_ingest_lan_hai`
  - `May_khong_thuoc_agent_khong_luu`
  - `Agent_phong_kham_khac_401_hoac_khong_thay`
  - `Ban_tin_qua_4MB_khong_hop_le`
  - `Giai_ma_loi_van_200_da_luu`
  - `Khoi_dong_lai_vot_cho_giai_ma`
  - `Readings_cu_404`
  - `Redecode_chi_quan_tri_truong_khoa_va_ghi_vet_nguoi`
  - `Redecode_qua_500_bao_loi`
  - `Preview_khong_ghi_gi`
  - `List_loc_trang_thai_va_phan_trang`
- [ ] **Step 2: Chạy và xác nhận đỏ.**
- [ ] **Step 3: Làm.**
- [ ] **Step 4: Xanh.** `dotnet test tests/Lis.Server.Tests` toàn bộ.
- [ ] **Step 5: Commit.** Thông điệp: `feat(server): nhận bản tin thô từ agent, giải mã ở server, phục hồi sau khởi động, xem và giải mã lại`.

### Task 5: Agent gửi bản tin thô thay cho kết quả đã giải mã

**Files:**
- Create: `src/Lis.Agent/RawMessageUploader.cs`
- Modify: `src/Lis.Agent/AgentWorker.cs` (truyền `RawMessageQueue` làm `IRawMessageSink` vào manager; bỏ `HttpReadingSink`), `src/Lis.Agent/ServerClient.cs` (thêm `SendRawMessagesAsync`, bỏ `SendReadingAsync` và `HttpReadingSink`), `src/Lis.Agent/Channel/AgentControl.cs` (`FlushPendingHandler` gọi uploader ngay, trả `{remaining}` = số bản tin còn trong hàng), trạng thái `pendingToRetry` báo lên web lấy từ hàng chờ bản tin.
- Test: `tests/Lis.Agent.Tests/RawMessageUploaderTests.cs`, cập nhật các test `FlushPending` / `AgentWorker` bị đụng.

**Interfaces:**
- Consumes: task 2 (`RawMessageQueue`), task 4 (API).
- Produces:
  - Thư mục hàng chờ: `{Root}/cho-gui/ban-tin/`. Cách ly khi ghép nối sang phòng khám khác giống `cho-gui` hiện nay (kiểm `PairingStore`).
  - `RawMessageUploader(RawMessageQueue queue, ServerClient client, TimeProvider clock, ILogger logger)`:
    - `Task RunAsync(CancellationToken ct)`, vòng lặp.
    - `Task<int> FlushNowAsync(CancellationToken ct)`.
  - Cách gửi:
    - Lần lượt từng máy, mỗi lô tối đa 50 bản tin theo `Seq`, lô tiếp theo chỉ gửi khi lô trước xong.
    - Bản tin có kết quả `da-luu` hoặc `trung` thì xoá tệp.
    - `khong-hop-le` thì đổi tệp thành `.loi` và log lỗi.
    - `khong-thuoc-agent` thì giữ tệp, máy đó tạm bỏ qua 5 phút rồi thử lại. Không xoá.
    - Lỗi mạng / 5xx thì chờ 1, 2, 4 … 60 s (±20 %).
    - 401 (bị thu hồi) thì dừng gửi, không xoá.
  - Tệp hàng chờ dạng `InstrumentReading` cũ trong `cho-gui/` thì để nguyên và log cảnh báo một lần: "Có kết quả chờ gửi dạng cũ — bản này không gửi được nữa".
- [ ] **Step 1: Test đỏ** (server giả bằng `HttpMessageHandler`).
  - `Mat_mang_giu_tep_co_mang_gui_du_theo_thu_tu`
  - `Server_tra_trung_xoa_tep`
  - `Khong_thuoc_agent_giu_tep_thu_lai_sau_5_phut`
  - `Khong_hop_le_doi_ten_loi`
  - `Bi_thu_hoi_dung_gui`
  - `Flush_pending_gui_ngay_tra_so_con_lai`
  - `Manager_agent_dung_che_do_ban_tin_tho`: runner không gọi `IReadingSink`.
- [ ] **Step 2: Chạy và xác nhận đỏ.**
- [ ] **Step 3: Làm.**
- [ ] **Step 4: Xanh.** `dotnet test tests/Lis.Agent.Tests` toàn bộ. `scripts/publish-agent.sh` vẫn ra được `LisAgent.exe`.
- [ ] **Step 5: Commit.** Thông điệp: `feat(agent): gửi bản tin thô lên server thay cho kết quả đã giải mã, hàng chờ trên đĩa gửi theo thứ tự`.

### Task 6: Đối chiếu trọn vòng bằng bộ giả lập (an toàn người bệnh)

**Files:**
- Create: `tests/Lis.Server.Tests/EndToEnd/RawPipelineEquivalenceTests.cs`, cùng helper chạy agent-runner trong tiến trình test.

**Interfaces:**
- Consumes: mọi task trước, `tools/Lis.Simulator` (bộ dựng bản tin, `DriverLoopbackLink`) và `WebApplicationFactory` server.
- Produces: không có API mới.

- [ ] **Step 1: Test.** Với mỗi họ HL7, ASTM (Generic + Access 2) và Beckman AU (Au480, cả bản tin nhiều khối), chạy các kịch bản: 3 mẫu thường, 1 QC, 1 câu hỏi chỉ định rồi kết quả, 1 mẫu đã duyệt nhận kết quả mới, 1 kênh chưa map.
  - **Đường cũ:** `InstrumentRunner` + `IngestReadingSink` ghi vào phòng khám A.
  - **Đường mới:** `InstrumentRunner` chế độ bản tin thô + `RawMessageQueue` + `RawMessageUploader` gửi tới server thật trong test, ghi vào phòng khám B. Hai phòng khám có cùng danh mục, chỉ định và map.
  - Assert hai phòng khám có cùng dữ liệu, so theo barcode + mã chỉ số và bỏ qua id / thời điểm:
    - `result` (value, flag, status, pending_value);
    - `qc_result`;
    - `unmatched_result` (reason);
    - số dòng `result_history`.
  - Kịch bản lỗi:
    - (a) server dừng giữa lúc agent gửi, rồi bật lại;
    - (b) agent gửi lại cả lô đã gửi;
    - (c) một bản tin bị sửa một byte checksum;
    - (d) mất mạng 5 phút (`FakeTimeProvider`) trong lúc máy vẫn gửi.

    Phải không mất, không trùng. Riêng bản hỏng ở (c) hiện `khong-co-ket-qua` hoặc `loi`, không sinh kết quả sai.
- [ ] **Step 2: Chạy.** Test nào đỏ là lỗi thật của task 1–5: sửa ở đúng task đó, không nới test.
- [ ] **Step 3: Commit.** Thông điệp: `test: đối chiếu trọn vòng đường cũ và đường giải mã ở server cho HL7, ASTM, Beckman AU`.

### Task 7: Giao diện "Bản tin thô" (Claude tự làm)

**Files:**
- Create: `web/src/modules/ban-tin/RawMessagesPage.tsx`, `RawMessageDrawer.tsx`, `rawMessages.ts` (+ `.test.ts`), `RawMessagesPage.test.tsx`
- Modify: `web/src/services/index.ts` (`rawMessageService.list/get/preview/redecode`, kiểu dữ liệu, `qk`), router + menu ("Bản tin thô", đặt sau "Log agent"), `web/src/shared/ui/StatusTag.tsx` (domain `decode`, nhãn: Chờ giải mã, Đang giải mã, Đã giải mã, Không có kết quả, Lỗi, Chỉ lưu, Cần xem lại)

- [ ] **Step 1: Test đỏ.**
  - Bảng lọc theo máy, trạng thái và ngày; mặc định lọc "Lỗi + Cần xem lại" khi mở từ thông báo.
  - Mở một bản tin: hiện chữ đọc được (`decodeBytes`), kết quả giải mã và lịch sử giải mã.
  - Nút "Giải mã thử" (quan-tri/truong-khoa) hiện bảng kết quả, không ghi gì.
  - Nút "Giải mã lại" hỏi xác nhận, có câu "Mẫu đã duyệt chỉ nhận giá trị chờ thay, không bị ghi đè".
  - Giải mã lại theo khoảng thời gian của một máy, báo lỗi khi quá 500 bản tin.
  - Vai trò khác không thấy nút.
- [ ] **Step 2: Làm** theo quy ước giao diện (bàn phím: Enter mở bản tin đang chọn, phím mũi tên chuyển dòng).
- [ ] **Step 3: Xanh.** `npx vitest run`, `npm run typecheck`, `npm run build`.
- [ ] **Step 4: Kiểm bằng ảnh chụp thật.** Server dev + agent console + `Lis.Simulator send --family hl7-mllp --connect …`. Chụp: danh sách, chi tiết, giải mã thử, giải mã lại một mẫu đã duyệt (thấy "chờ thay" ở màn Kết quả). Sửa tới khi đúng.
- [ ] **Step 5: Commit.** Thông điệp: `feat(web): xem bản tin thô, giải mã thử, giải mã lại`.

### Task 8: Tài liệu, danh sách kiểm Windows, rà soát an toàn người bệnh

**Files:**
- Modify: `docs/server-agent.md` (luồng bản tin thô, trạng thái giải mã, 90 ngày, bỏ `/readings`), `docs/HUONG-DAN/02-cau-hinh.md` + `04-xu-ly-su-co.md` ("Bản tin lỗi / cần xem lại", giải mã lại sau khi sửa map kênh)
- Create: `docs/superpowers/checklists/2026-11-19-d-windows-that.md`, gồm:
  - BC-5380 / AU480 / Access 2 thật gửi kết quả, so với bản in của máy;
  - rút mạng khi máy đang gửi;
  - tắt server khi đang gửi;
  - sửa map kênh rồi giải mã lại.

- [ ] **Step 1: Viết tài liệu và danh sách kiểm.**
- [ ] **Step 2: Commit.** Thông điệp: `docs: giải mã ở server, bản tin thô, danh sách kiểm Windows cho phần D`.
- [ ] **Step 3: Rà soát cuối toàn nhánh bằng reviewer mức cao nhất** (spec: phần an toàn người bệnh), tập trung vào task 1, 3, 6.
