using Lis.App.Services;
using Lis.App.ViewModels.Results;
using Lis.Core.Auth;
using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Auth;
using Lis.Data.Results;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>IResultService giả: giữ một mẫu trong bộ nhớ, ghi lại các lệnh ghi đã nhận.</summary>
internal sealed class FakeResultService : IResultService
{
    public SampleResults? Sample { get; set; }

    public List<SampleListItem> ListItems { get; } = [];

    public int GetCalls { get; private set; }

    public List<(Guid OrderItemId, string Value, Guid ActorId, string? Reason)> SetManualCalls { get; } = [];

    public List<Guid> ApproveCalls { get; } = [];

    public List<(Guid SampleId, string Reason)> UnapproveCalls { get; } = [];

    public Task<IReadOnlyList<SampleListItem>> ListAsync(DateOnly from, DateOnly to, string? status, string? query) =>
        Task.FromResult<IReadOnlyList<SampleListItem>>(ListItems);

    public Task<SampleResults> GetAsync(Guid sampleId)
    {
        GetCalls++;
        return Task.FromResult(Sample ?? throw new InvalidOperationException("Không tìm thấy mẫu"));
    }

    public Task SetManualAsync(Guid orderItemId, string value, Guid actorId, string? reason)
    {
        SetManualCalls.Add((orderItemId, value, actorId, reason));
        return Task.CompletedTask;
    }

    public Task ApproveAsync(Guid sampleId, Guid actorId)
    {
        ApproveCalls.Add(sampleId);
        Sample = Sample! with { ApprovedAt = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero), ApprovedBy = "Kỹ thuật viên Thử" };
        return Task.CompletedTask;
    }

    public Task UnapproveAsync(Guid sampleId, Guid actorId, string reason)
    {
        UnapproveCalls.Add((sampleId, reason));
        Sample = Sample! with { ApprovedAt = null, ApprovedBy = null };
        return Task.CompletedTask;
    }

    public Task AcceptPendingAsync(Guid resultId, Guid actorId) => Task.CompletedTask;

    public Task RejectPendingAsync(Guid resultId, Guid actorId) => Task.CompletedTask;

    public Task<bool> TryAutoApproveAsync(Guid sampleId) => Task.FromResult(false);
}

/// <summary>IReportPrinter giả: ghi lại mẫu đã yêu cầu in.</summary>
internal sealed class FakeReportPrinter : IReportPrinter
{
    public List<Guid> Printed { get; } = [];

    public Task PrintAsync(Guid sampleId)
    {
        Printed.Add(sampleId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Kiểm tra màn Kết quả: sửa ô đã có giá trị phải có lý do, Esc trả lại giá trị cũ, Duyệt gọi
/// service rồi nạp lại mẫu, Hủy duyệt chỉ hiện với quyền <see cref="Permission.HuyDuyet"/>.
/// </summary>
public class ResultsViewModelTests
{
    private static readonly Guid SampleId = Guid.NewGuid();
    private static readonly Guid HgbItemId = Guid.NewGuid();
    private static readonly Guid WbcItemId = Guid.NewGuid();

    private static ResultRow Row(Guid itemId, string code, string name, string? value, ResultFlag flag = ResultFlag.None) =>
        new(itemId, value is null ? null : Guid.NewGuid(), Guid.NewGuid(), code, name, false, 0, value, "g/L", "120 – 150", flag,
            value is null ? null : "cho-duyet", null, value is null ? null : "tay");

    private static SampleResults UnapprovedSample() => new(
        SampleId, "9261104001", "Nguyễn Văn Mẫu", "BN2611040001", new PatientFacts('M', new DateOnly(1990, 1, 1), null),
        null, null,
        [Row(HgbItemId, "HGB", "Huyết sắc tố", "119", ResultFlag.Low), Row(WbcItemId, "WBC", "Bạch cầu", null)]);

    private static ResultsViewModel CreateViewModel(FakeResultService service, string role, FakeReportPrinter? printer = null)
    {
        var user = new UserInfo(Guid.NewGuid(), "ktv.thu", "Kỹ thuật viên Thử", null, [role], true);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.FromHours(7)));
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        return new ResultsViewModel(service, new CurrentUser(user), printer ?? new FakeReportPrinter(), clock);
    }

    [Fact]
    public async Task CommitValue_OverwritingExistingValue_RequiresReason()
    {
        var service = new FakeResultService { Sample = UnapprovedSample() };
        var viewModel = CreateViewModel(service, Roles.KyThuatVien);
        await viewModel.LoadSampleAsync(SampleId);
        var row = viewModel.Rows.Single(r => r.Code == "HGB");

        row.EditText = "125";
        viewModel.IsReasonVisible.ShouldBeTrue();

        await viewModel.CommitValueAsync(row);

        viewModel.ErrorMessage.ShouldBe("Nhập lý do sửa kết quả");
        service.SetManualCalls.ShouldBeEmpty();
        row.EditText.ShouldBe("125");

        viewModel.ReasonText = "Nhập nhầm";
        var getCallsBefore = service.GetCalls;
        await viewModel.CommitValueAsync(row);

        viewModel.ErrorMessage.ShouldBeNull();
        service.SetManualCalls.ShouldBe([(HgbItemId, "125", viewModel.ActorId, "Nhập nhầm")]);
        service.GetCalls.ShouldBe(getCallsBefore + 1);
        viewModel.ReasonText.ShouldBe("");
        viewModel.IsReasonVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task CommitValue_EmptyCell_NoReasonNeeded()
    {
        var service = new FakeResultService { Sample = UnapprovedSample() };
        var viewModel = CreateViewModel(service, Roles.KyThuatVien);
        await viewModel.LoadSampleAsync(SampleId);
        var row = viewModel.Rows.Single(r => r.Code == "WBC");

        row.EditText = "6,5";
        viewModel.IsReasonVisible.ShouldBeFalse();

        await viewModel.CommitValueAsync(row);

        viewModel.ErrorMessage.ShouldBeNull();
        service.SetManualCalls.ShouldBe([(WbcItemId, "6,5", viewModel.ActorId, null)]);
    }

    [Fact]
    public async Task CancelEdit_RestoresStoredValue()
    {
        var service = new FakeResultService { Sample = UnapprovedSample() };
        var viewModel = CreateViewModel(service, Roles.KyThuatVien);
        await viewModel.LoadSampleAsync(SampleId);
        var row = viewModel.Rows.Single(r => r.Code == "HGB");

        row.EditText = "999";
        viewModel.CancelEdit(row);

        row.EditText.ShouldBe("119");
        row.IsDirty.ShouldBeFalse();
        viewModel.IsReasonVisible.ShouldBeFalse();
    }

    [Fact]
    public async Task Approve_CallsServiceAndReloads()
    {
        var service = new FakeResultService { Sample = UnapprovedSample() };
        var viewModel = CreateViewModel(service, Roles.KyThuatVien);
        await viewModel.LoadSampleAsync(SampleId);
        viewModel.CanApprove.ShouldBeTrue();
        viewModel.CanEditValues.ShouldBeTrue();

        await viewModel.ApproveCommand.ExecuteAsync(null);

        service.ApproveCalls.ShouldBe([SampleId]);
        viewModel.Current!.ApprovedAt.ShouldNotBeNull();
        viewModel.ApprovalText.ShouldContain("Kỹ thuật viên Thử");
        viewModel.CanApprove.ShouldBeFalse();
        viewModel.CanEditValues.ShouldBeFalse();
        viewModel.Rows.ShouldAllBe(r => !r.IsEditable);
    }

    [Fact]
    public async Task Unapprove_HiddenWithoutPermission()
    {
        var approvedSample = UnapprovedSample() with { ApprovedAt = DateTimeOffset.UtcNow, ApprovedBy = "Trưởng khoa Thử" };

        var technician = CreateViewModel(new FakeResultService { Sample = approvedSample }, Roles.KyThuatVien);
        await technician.LoadSampleAsync(SampleId);
        technician.CanUnapprove.ShouldBeFalse();

        var head = CreateViewModel(new FakeResultService { Sample = approvedSample }, Roles.TruongKhoa);
        await head.LoadSampleAsync(SampleId);
        head.CanUnapprove.ShouldBeTrue();
    }

    [Fact]
    public async Task ConfirmUnapprove_RequiresReason_ThenCallsService()
    {
        var service = new FakeResultService { Sample = UnapprovedSample() with { ApprovedAt = DateTimeOffset.UtcNow, ApprovedBy = "Trưởng khoa Thử" } };
        var viewModel = CreateViewModel(service, Roles.TruongKhoa);
        await viewModel.LoadSampleAsync(SampleId);

        viewModel.StartUnapproveCommand.Execute(null);
        viewModel.IsUnapprovePromptVisible.ShouldBeTrue();

        await viewModel.ConfirmUnapproveCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.ShouldBe("Nhập lý do hủy duyệt");
        service.UnapproveCalls.ShouldBeEmpty();

        viewModel.UnapproveReason = "Nhầm mẫu";
        await viewModel.ConfirmUnapproveCommand.ExecuteAsync(null);

        service.UnapproveCalls.ShouldBe([(SampleId, "Nhầm mẫu")]);
        viewModel.IsUnapprovePromptVisible.ShouldBeFalse();
        viewModel.Current!.ApprovedAt.ShouldBeNull();
        viewModel.CanEditValues.ShouldBeTrue();
    }

    [Fact]
    public async Task Print_CallsReportPrinter()
    {
        var printer = new FakeReportPrinter();
        var viewModel = CreateViewModel(new FakeResultService { Sample = UnapprovedSample() }, Roles.KyThuatVien, printer);
        await viewModel.LoadSampleAsync(SampleId);

        await viewModel.PrintCommand.ExecuteAsync(null);

        printer.Printed.ShouldBe([SampleId]);
    }

    [Fact]
    public async Task ReadOnlyUser_CannotEditValues()
    {
        var viewModel = CreateViewModel(new FakeResultService { Sample = UnapprovedSample() }, Roles.TiepNhan);
        await viewModel.LoadSampleAsync(SampleId);

        viewModel.CanEditValues.ShouldBeFalse();
        viewModel.CanApprove.ShouldBeFalse();
        viewModel.Rows.ShouldAllBe(r => !r.IsEditable);
    }
}
