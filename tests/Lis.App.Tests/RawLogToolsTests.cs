using System.Text;
using Lis.App.ViewModels.Instruments;
using Lis.App.ViewModels.Settings;
using Lis.Core.Instruments;
using Lis.Data.Instruments;
using Lis.Data.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>Màn "Log thô": giải mã thử, xuất/nhập .listrace; tab cài đặt "Log thô".</summary>
public class RawLogToolsTests
{
    private static readonly Guid InstrumentId = Guid.NewGuid();

    private static byte[] Mllp(string text) => [0x0B, .. Encoding.UTF8.GetBytes(text), 0x1C, 0x0D];

    private static string Oru(string barcode) =>
        "MSH|^~\\&|BC-5380|Mindray|||20261110090000||ORU^R01|1|P|2.3.1||||||UNICODE\r" +
        $"OBR|1||{barcode}|00001^Automated Count^99MRC\r" +
        "OBX|6|NM|6690-2^WBC^LN||7.5|10*9/L|4.00-10.00|N|||F\r";

    private static RawLogViewModel Create(out FakeRawLogStore logs, string family = "hl7-mllp")
    {
        logs = new FakeRawLogStore();
        var now = DateTimeOffset.Now;
        logs.Append(InstrumentId, new RawLogEntry(now, RawLogDirection.In, "p1", Mllp(Oru("2611100001"))));
        logs.Append(InstrumentId, new RawLogEntry(now.AddMilliseconds(5), RawLogDirection.Out, "p1", Mllp("MSH|^~\\&|LIS\rMSA|AA|1\r")));
        logs.Append(InstrumentId, new RawLogEntry(now.AddSeconds(1), RawLogDirection.In, "p2", Mllp(Oru("2611100002"))));

        var store = new FakeInstrumentStore { Instruments = [new InstrumentInfo(InstrumentId, "BC 5380", "Mindray BC-5380", family, null, null, null, true)] };
        return new RawLogViewModel(store, logs);
    }

    [Fact]
    public async Task DecodeTry_UsesInstrumentFamily_AndRespectsSession()
    {
        var viewModel = Create(out _);
        await viewModel.LoadAsync();
        viewModel.DecodeFamily.ShouldBe("hl7-mllp");

        viewModel.DecodeTry();

        viewModel.IsDecodeOpen.ShouldBeTrue();
        viewModel.DecodeRows.Count.ShouldBe(2);
        viewModel.DecodeRows[0].Kind.ShouldBe("Kết quả");
        viewModel.DecodeRows[0].Text.ShouldBe("Barcode 2611100001 · 1 kênh: 6690-2=7.5 10*9/L [N]");
        viewModel.DecodeSummary!.ShouldStartWith("Driver hl7-mllp: 2 bản tin kết quả");

        viewModel.SelectedSession = "p2";
        viewModel.DecodeTry();
        viewModel.DecodeRows.ShouldHaveSingleItem().Text.ShouldContain("2611100002");
    }

    [Fact]
    public async Task DecodeTry_FamilyWithoutDriver_ShowsError()
    {
        var viewModel = Create(out _, family: "sysmex-ca");
        await viewModel.LoadAsync();

        viewModel.DecodeFamily.ShouldBeNull();
        viewModel.DecodeTry();

        viewModel.ErrorMessage.ShouldBe("Chọn họ giao thức có driver để giải mã thử");
        viewModel.IsDecodeOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsSessionIncludingAcks()
    {
        var source = Create(out _);
        await source.LoadAsync();
        source.SelectedSession = "p1";
        var exported = source.BuildListrace();
        exported.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
        source.SuggestedExportName.ShouldEndWith("-p1");
        source.SuggestedExportName.ShouldStartWith("BC-5380-");

        var target = Create(out var logs);
        logs.Entries.Clear();
        await target.LoadAsync();
        target.ImportListrace("may-a.listrace", exported + "dòng hỏng\n");

        target.IsImported.ShouldBeTrue();
        target.Sessions.ShouldBe([RawLogViewModel.AllSessions, "p1"]);
        target.Summary!.ShouldStartWith("Tệp nhập: may-a.listrace · ");
        target.DecodeTry();
        target.DecodeRows.ShouldHaveSingleItem().Text.ShouldContain("2611100001");

        // Tải lại → quay về log của máy/ngày.
        target.Reload();
        target.IsImported.ShouldBeFalse();
    }

    [Fact]
    public async Task Import_GarbageFile_ShowsError()
    {
        var viewModel = Create(out _);
        await viewModel.LoadAsync();

        viewModel.ImportListrace("x.txt", "không phải log");

        viewModel.ErrorMessage!.ShouldContain("không có bản ghi log thô đọc được");
        viewModel.IsImported.ShouldBeFalse();
    }

    private sealed class CountingRawLogStore : IRawLogStore
    {
        public (DateOnly Today, int KeepDays)? LastDelete { get; private set; }

        public string RootDirectory => "rawlog";

        public void Append(Guid instrumentId, RawLogEntry entry)
        {
        }

        public IReadOnlyList<DateOnly> ListDates(Guid instrumentId) => [];

        public IReadOnlyList<RawLogEntry> Read(Guid instrumentId, DateOnly date) => [];

        public string FilePath(Guid instrumentId, DateOnly date) => "";

        public int DeleteOlderThan(DateOnly today, int keepDays)
        {
            LastDelete = (today, keepDays);
            return 3;
        }

        public long TotalBytes() => 2L * 1024 * 1024 * 1024;
    }

    [Fact]
    public async Task Settings_LoadSaveAndCleanNow()
    {
        var settings = new InMemorySettingStore();
        var logs = new CountingRawLogStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
        var viewModel = new RawLogSettingsViewModel(settings, logs, clock);

        await viewModel.LoadAsync();
        viewModel.KeepDaysText.ShouldBe("30");
        viewModel.IsOverThreshold.ShouldBeTrue(); // 2 GB > 1024 MB mặc định
        viewModel.UsageText.ShouldBe("Đang dùng 2 GB trên máy trạm này");

        viewModel.KeepDaysText = "0";
        await viewModel.SaveCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.ShouldNotBeNull();

        viewModel.KeepDaysText = "7";
        viewModel.WarnMegabytesText = "4096";
        await viewModel.SaveCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.ShouldBeNull();
        settings.Values[SettingKeys.RawLogKeepDays].ShouldBe("7");
        viewModel.IsOverThreshold.ShouldBeFalse();

        await viewModel.CleanNowCommand.ExecuteAsync(null);
        logs.LastDelete!.Value.KeepDays.ShouldBe(7);
        viewModel.SuccessMessage.ShouldBe("Đã xóa 3 tệp log quá hạn");
    }
}
