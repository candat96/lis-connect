using Lis.App.ViewModels.Unmatched;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Ingest;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>IUnmatchedService giả: trả danh sách/chỉ số lá đã dựng sẵn, ghi lại các lệnh gán/bỏ qua đã nhận.</summary>
internal sealed class FakeUnmatchedService : IUnmatchedService
{
    public List<UnmatchedItem> Items { get; set; } = [];

    public List<SampleLeafItem> LeafItems { get; set; } = [];

    public Exception? SampleItemsException { get; set; }

    public Exception? AssignException { get; set; }

    public Exception? DismissException { get; set; }

    public List<(Guid UnmatchedId, Guid OrderItemId, Guid ActorId)> AssignCalls { get; } = [];

    public List<(Guid UnmatchedId, Guid ActorId, string Reason)> DismissCalls { get; } = [];

    public Task<IReadOnlyList<UnmatchedItem>> ListOpenAsync(DateOnly from, DateOnly to) =>
        Task.FromResult<IReadOnlyList<UnmatchedItem>>(Items);

    public Task<IReadOnlyList<SampleLeafItem>> SampleItemsAsync(string barcode) =>
        SampleItemsException is null
            ? Task.FromResult<IReadOnlyList<SampleLeafItem>>(LeafItems)
            : Task.FromException<IReadOnlyList<SampleLeafItem>>(SampleItemsException);

    public Task AssignAsync(Guid unmatchedId, Guid orderItemId, Guid? actorId)
    {
        AssignCalls.Add((unmatchedId, orderItemId, actorId!.Value));
        return AssignException is null ? Task.CompletedTask : Task.FromException(AssignException);
    }

    public Task DismissAsync(Guid unmatchedId, Guid actorId, string reason)
    {
        DismissCalls.Add((unmatchedId, actorId, reason));
        return DismissException is null ? Task.CompletedTask : Task.FromException(DismissException);
    }
}

/// <summary>Kiểm tra màn Chưa ghép: nạp danh sách, ô "Gán vào mẫu…" (tra barcode → chọn chỉ số → gán), bỏ qua bắt buộc lý do, quyền <see cref="Permission.GhepTay"/>.</summary>
public class UnmatchedViewModelTests
{
    private static readonly Guid UnmatchedId = Guid.NewGuid();
    private static readonly Guid OrderItemId = Guid.NewGuid();
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 4, 9, 15, 0, TimeSpan.FromHours(7));

    private static UnmatchedItem Row(string reason) =>
        new(UnmatchedId, "Máy thử XN-1000", "9261104001", "HGB", "130", "g/L", reason, ReceivedAt, null, null);

    private static UnmatchedViewModel CreateViewModel(FakeUnmatchedService service, string role = Roles.KyThuatVien)
    {
        var user = new UserInfo(Guid.NewGuid(), "ktv.thu", "Kỹ thuật viên Thử", null, [role], true);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.FromHours(7)));
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        return new UnmatchedViewModel(service, new CurrentUser(user), clock);
    }

    [Fact]
    public async Task LoadAsync_PopulatesRows()
    {
        var service = new FakeUnmatchedService { Items = [Row(UnmatchedReason.NoBarcode), Row(UnmatchedReason.ChannelUnmapped)] };
        var viewModel = CreateViewModel(service);

        await viewModel.LoadAsync();

        viewModel.Rows.Count.ShouldBe(2);
        viewModel.Rows[0].ReasonLabel.ShouldBe("Không có barcode");
        viewModel.CountText.ShouldBe("2 dòng chưa ghép");
    }

    [Fact]
    public async Task AssignFlow_LookupThenConfirm_CallsServiceAndReloads()
    {
        var service = new FakeUnmatchedService { Items = [Row(UnmatchedReason.ChannelUnmapped)] };
        var viewModel = CreateViewModel(service);
        await viewModel.LoadAsync();
        viewModel.SelectedRow = viewModel.Rows.Single();

        viewModel.StartAssignCommand.Execute(null);
        viewModel.AssignPanel.IsVisible.ShouldBeTrue();
        viewModel.AssignPanel.BarcodeText.ShouldBe("9261104001");

        service.LeafItems = [new SampleLeafItem(OrderItemId, "HGB", "Huyết sắc tố", null)];
        await viewModel.AssignPanel.LookupCommand.ExecuteAsync(null);
        viewModel.AssignPanel.Items.Count.ShouldBe(1);

        viewModel.AssignPanel.SelectedItem = viewModel.AssignPanel.Items.Single();
        viewModel.AssignPanel.CanConfirm.ShouldBeTrue();

        service.Items = []; // sau khi gán, dòng không còn trong danh sách mở
        await viewModel.AssignPanel.ConfirmCommand.ExecuteAsync(null);

        service.AssignCalls.ShouldBe([(UnmatchedId, OrderItemId, viewModel.ActorId)]);
        viewModel.AssignPanel.IsVisible.ShouldBeFalse();
        viewModel.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task AssignLookup_UnknownBarcode_ShowsError()
    {
        var service = new FakeUnmatchedService
        {
            Items = [Row(UnmatchedReason.NoBarcode)],
            SampleItemsException = new InvalidOperationException("Không tìm thấy mẫu có barcode 0000000000"),
        };
        var viewModel = CreateViewModel(service);
        await viewModel.LoadAsync();
        viewModel.SelectedRow = viewModel.Rows.Single();
        viewModel.StartAssignCommand.Execute(null);

        viewModel.AssignPanel.BarcodeText = "0000000000";
        await viewModel.AssignPanel.LookupCommand.ExecuteAsync(null);

        viewModel.AssignPanel.ErrorMessage.ShouldBe("Không tìm thấy mẫu có barcode 0000000000");
        viewModel.AssignPanel.Items.ShouldBeEmpty();
        viewModel.AssignPanel.CanConfirm.ShouldBeFalse();
    }

    [Fact]
    public async Task AssignConfirm_ServiceRejects_ShowsErrorAndKeepsPanelOpen()
    {
        var service = new FakeUnmatchedService
        {
            Items = [Row(UnmatchedReason.ChannelUnmapped)],
            LeafItems = [new SampleLeafItem(OrderItemId, "HGB", "Huyết sắc tố", null)],
            AssignException = new InvalidOperationException("Dòng này đã được xử lý"),
        };
        var viewModel = CreateViewModel(service);
        await viewModel.LoadAsync();
        viewModel.SelectedRow = viewModel.Rows.Single();
        viewModel.StartAssignCommand.Execute(null);
        await viewModel.AssignPanel.LookupCommand.ExecuteAsync(null);
        viewModel.AssignPanel.SelectedItem = viewModel.AssignPanel.Items.Single();

        await viewModel.AssignPanel.ConfirmCommand.ExecuteAsync(null);

        viewModel.AssignPanel.ErrorMessage.ShouldBe("Gán thất bại: Dòng này đã được xử lý");
        viewModel.AssignPanel.IsVisible.ShouldBeTrue();
        viewModel.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DismissFlow_RequiresReason_ThenCallsService()
    {
        var service = new FakeUnmatchedService { Items = [Row(UnmatchedReason.NoBarcode)] };
        var viewModel = CreateViewModel(service);
        await viewModel.LoadAsync();
        viewModel.SelectedRow = viewModel.Rows.Single();

        viewModel.StartDismissCommand.Execute(null);
        viewModel.IsDismissPromptVisible.ShouldBeTrue();

        await viewModel.ConfirmDismissCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.ShouldBe("Nhập lý do bỏ qua");
        service.DismissCalls.ShouldBeEmpty();

        viewModel.DismissReason = "Mẫu QC gửi nhầm";
        service.Items = [];
        await viewModel.ConfirmDismissCommand.ExecuteAsync(null);

        service.DismissCalls.ShouldBe([(UnmatchedId, viewModel.ActorId, "Mẫu QC gửi nhầm")]);
        viewModel.IsDismissPromptVisible.ShouldBeFalse();
        viewModel.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutGhepTayPermission_CannotAct()
    {
        var service = new FakeUnmatchedService { Items = [Row(UnmatchedReason.NoBarcode)] };
        var viewModel = CreateViewModel(service, Roles.TiepNhan);
        await viewModel.LoadAsync();
        viewModel.SelectedRow = viewModel.Rows.Single();
        viewModel.CanAct.ShouldBeFalse();

        viewModel.StartAssignCommand.Execute(null);
        viewModel.AssignPanel.IsVisible.ShouldBeFalse();

        viewModel.StartDismissCommand.Execute(null);
        viewModel.IsDismissPromptVisible.ShouldBeFalse();
    }
}
