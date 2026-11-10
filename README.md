# LIS Connect

> **Bản web (server + agent + web):** xem [docs/HUONG-DAN-CAI-DAT.md](docs/HUONG-DAN-CAI-DAT.md) — khởi tạo, cài đặt, kết nối máy.

Phần mềm quản lý xét nghiệm (LIS) cho khoa xét nghiệm, cài trên Windows, nối hai chiều với máy xét nghiệm và tích hợp được với HIS. Chi tiết mục đích, phạm vi và các quyết định thiết kế nằm ở bản thiết kế: [`docs/superpowers/specs/2026-11-03-lis-windows-design.md`](docs/superpowers/specs/2026-11-03-lis-windows-design.md).

Kế hoạch triển khai (chia theo task) nằm ở: [`docs/superpowers/plans/2026-11-03-lis-01-nen-tang-va-lis-doc-lap.md`](docs/superpowers/plans/2026-11-03-lis-01-nen-tang-va-lis-doc-lap.md).

Tài liệu giao tiếp của từng hãng máy (driver) nằm ở [`docs/drivers/`](docs/drivers/README.md); các bản PDF/tài liệu gốc của hãng (nặng, có thể hạn chế bản quyền) nằm ở `docs/drivers/tai-lieu-hang/` và **không đẩy lên GitHub** (xem `.gitignore`).

Hướng dẫn cài đặt, cấu hình và sử dụng (tiếng Việt, cho IT và nhân viên khoa xét nghiệm) nằm ở [`docs/HUONG-DAN/`](docs/HUONG-DAN/README.md).

## Cấu trúc solution

```
LisConnect.slnx
├── src/
│   ├── Lis.Core/      Lớp nghiệp vụ lõi, không phụ thuộc Data/Reports/App
│   ├── Lis.Data/      Truy cập dữ liệu (phụ thuộc Lis.Core)
│   ├── Lis.Reports/   Sinh và in phiếu kết quả (phụ thuộc Lis.Core)
│   └── Lis.App/       Ứng dụng Avalonia (desktop, chạy cả Windows và macOS khi phát triển)
└── tests/
    ├── Lis.Core.Tests/
    ├── Lis.Data.Tests/     (cần Docker — Testcontainers)
    ├── Lis.Reports.Tests/
    └── Lis.App.Tests/
```

Chiều phụ thuộc chỉ đi một hướng: `Lis.Data → Lis.Core`, `Lis.Reports → Lis.Core`, `Lis.App → Lis.Core, Lis.Data, Lis.Reports`. Không có chiều ngược lại — được kiểm bằng kiểm thử kiến trúc `Core_DoesNotReferenceDataReportsOrApp` trong `tests/Lis.Core.Tests`.

## Yêu cầu máy phát triển

- .NET SDK 10 (bản `10.0.403`, khai trong `global.json`). Nếu SDK không nằm trong `PATH` hệ thống, dùng:

  ```bash
  export PATH=$HOME/.dotnet:$PATH
  ```

- Docker đang chạy, nếu cần chạy `Lis.Data.Tests` (dùng Testcontainers để kiểm thử với CSDL thật).

## Build và chạy test

Khôi phục gói và build toàn bộ solution, coi cảnh báo là lỗi:

```bash
dotnet restore LisConnect.slnx
dotnet build LisConnect.slnx -warnaserror
```

Chạy test cho một project:

```bash
dotnet test tests/Lis.Core.Tests
```

Chạy test cho toàn solution. Vì một số project test chưa có test nào (sẽ bổ sung ở các task sau), cần bỏ qua mã thoát "không có test nào chạy" (mã 8) cho các project đó:

```bash
dotnet test tests/Lis.Core.Tests
dotnet test tests/Lis.Data.Tests --ignore-exit-code 8
dotnet test tests/Lis.Reports.Tests --ignore-exit-code 8
dotnet test tests/Lis.App.Tests --ignore-exit-code 8
```

Dự án dùng xunit.v3 với nền chạy test mới (`Microsoft.Testing.Platform`), khai trong `global.json` (mục `test.runner`).

## Gói và phiên bản

Phiên bản các gói NuGet được khai một lần trong `Directory.Packages.props` (Central Package Management). Các cấu hình chung của project (`Nullable`, `TreatWarningsAsErrors`, `ImplicitUsings`, …) khai một lần trong `Directory.Build.props`.

## Tích hợp liên tục (CI)

`.github/workflows/build.yml` chạy `dotnet build` và `dotnet test` trên cả `windows-latest` và `ubuntu-latest`. `Lis.Data.Tests` cần Docker nên chỉ chạy trên `ubuntu-latest`.

## Build cho Windows

Để tạo bản phân phối có thể cài trên Windows:

**Trên macOS/Linux** (cross-compile):
```bash
pwsh scripts/build-windows.ps1
# Nếu pwsh không có, chạy trực tiếp dotnet:
dotnet publish src/Lis.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/LisConnect-win-x64/app
dotnet publish tools/Lis.SeedTool -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/LisConnect-win-x64/seedtool
```

Kết quả: `dist/LisConnect-win-x64.zip` sẵn sàng triển khai.

## Chạy thử trên Windows

Xem hướng dẫn chi tiết: [`docs/CHAY-THU-WINDOWS.md`](docs/CHAY-THU-WINDOWS.md)

Tóm tắt:
1. Cài PostgreSQL 16+ (https://www.postgresql.org/download/windows/)
2. Tạo CSDL `lis` và tài khoản `lis_user`
3. Chạy `LisConnect.exe` → hoàn thành wizard cài đặt
4. Nạp danh mục từ `mau-danh-muc.xlsx`
5. Tạo dữ liệu demo (tuỳ chọn): `Lis.SeedTool.exe seed-demo --conn "..."`
