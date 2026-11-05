using Lis.App.ViewModels.Instruments;
using Lis.Core.Auth;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Instruments;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>IInstrumentStore giả: giữ danh sách máy/map kênh trong bộ nhớ, ghi lại các lệnh đã nhận.</summary>
internal sealed class FakeInstrumentStore : IInstrumentStore
{
    public List<InstrumentInfo> Instruments { get; set; } = [];

    public Dictionary<Guid, List<ChannelMapItem>> ChannelsByInstrument { get; } = [];

    public Exception? SaveChannelException { get; set; }

    public List<ChannelMap> SaveChannelCalls { get; } = [];

    public Task<IReadOnlyList<InstrumentInfo>> ListAsync() => Task.FromResult<IReadOnlyList<InstrumentInfo>>(Instruments);

    public Task<Guid> SaveAsync(InstrumentInfo instrument)
    {
        var id = instrument.Id == Guid.Empty ? Guid.NewGuid() : instrument.Id;
        var saved = instrument with { Id = id };
        Instruments.RemoveAll(i => i.Id == id);
        Instruments.Add(saved);
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<ChannelMapItem>> ChannelsAsync(Guid instrumentId) =>
        Task.FromResult<IReadOnlyList<ChannelMapItem>>(ChannelsByInstrument.TryGetValue(instrumentId, out var list) ? list : []);

    public Task<Guid> SaveChannelAsync(ChannelMap channel)
    {
        SaveChannelCalls.Add(channel);
        if (SaveChannelException is not null)
        {
            return Task.FromException<Guid>(SaveChannelException);
        }

        var id = channel.Id == Guid.Empty ? Guid.NewGuid() : channel.Id;
        var list = ChannelsByInstrument.TryGetValue(channel.InstrumentId, out var existing) ? existing : ChannelsByInstrument[channel.InstrumentId] = [];
        list.RemoveAll(c => c.Map.Id == id);
        list.Add(new ChannelMapItem(channel with { Id = id }, "HGB", "Huyết sắc tố"));
        return Task.FromResult(id);
    }

    public Task DeleteChannelAsync(Guid channelId)
    {
        foreach (var list in ChannelsByInstrument.Values)
        {
            list.RemoveAll(c => c.Map.Id == channelId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>IServiceLookup giả: tra theo mã, không phân biệt hoa thường.</summary>
internal sealed class FakeServiceLookup : IServiceLookup
{
    public Dictionary<string, ServiceInfo> ByCode { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ServiceInfo?> FindByCodeAsync(string code) => Task.FromResult(ByCode.GetValueOrDefault(code));
}

/// <summary>Kiểm tra bảng map kênh (<see cref="ChannelMapEditorViewModel"/>): thêm, sửa, hệ số &gt; 0, lỗi trùng kênh.</summary>
public class ChannelMapEditorViewModelTests
{
    private static readonly Guid InstrumentId = Guid.NewGuid();
    private static readonly Guid HgbId = Guid.NewGuid();

    private static (ChannelMapEditorViewModel ViewModel, FakeInstrumentStore Store, FakeServiceLookup Services) Create()
    {
        var store = new FakeInstrumentStore();
        var services = new FakeServiceLookup { ByCode = { ["HGB"] = new ServiceInfo(HgbId, "HGB", "Huyết sắc tố", null, Guid.NewGuid(), "g/L", "so", 0, 0, false, false, true, null, []) } };
        return (new ChannelMapEditorViewModel(store, services), store, services);
    }

    [Fact]
    public async Task AddChannel_ValidInput_SavesAndClearsForm()
    {
        var (viewModel, store, _) = Create();
        await viewModel.LoadAsync(InstrumentId);

        viewModel.NewChannelCode = "HGB";
        viewModel.NewServiceCode = "HGB";
        viewModel.NewFactorText = "100";

        await viewModel.AddChannelCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBeNull();
        store.SaveChannelCalls.Single().Factor.ShouldBe(100m);
        viewModel.Channels.Single().ChannelCode.ShouldBe("HGB");
        viewModel.NewChannelCode.ShouldBe("");
        viewModel.SaveButtonLabel.ShouldBe("+ Thêm kênh");
    }

    [Fact]
    public async Task AddChannel_FactorNotPositive_ShowsErrorWithoutCallingStore()
    {
        var (viewModel, store, _) = Create();
        await viewModel.LoadAsync(InstrumentId);
        viewModel.NewChannelCode = "HGB";
        viewModel.NewServiceCode = "HGB";
        viewModel.NewFactorText = "0";

        await viewModel.AddChannelCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Hệ số phải lớn hơn 0");
        store.SaveChannelCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddChannel_UnknownServiceCode_ShowsError()
    {
        var (viewModel, store, _) = Create();
        await viewModel.LoadAsync(InstrumentId);
        viewModel.NewChannelCode = "XYZ";
        viewModel.NewServiceCode = "KHONGCO";

        await viewModel.AddChannelCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Không có chỉ số mã KHONGCO");
        store.SaveChannelCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EditExistingRow_LoadsFormAndUpdatesInPlace()
    {
        var (viewModel, store, _) = Create();
        await viewModel.LoadAsync(InstrumentId);
        viewModel.NewChannelCode = "HGB";
        viewModel.NewServiceCode = "HGB";
        viewModel.NewDecimalsText = "0";
        await viewModel.AddChannelCommand.ExecuteAsync(null);
        var row = viewModel.Channels.Single();

        row.EditCommand.Execute(null);

        viewModel.IsEditingExisting.ShouldBeTrue();
        viewModel.SaveButtonLabel.ShouldBe("Lưu kênh");
        viewModel.NewChannelCode.ShouldBe("HGB");
        viewModel.NewDecimalsText.ShouldBe("0");

        viewModel.NewFactorText = "10";
        await viewModel.AddChannelCommand.ExecuteAsync(null);

        viewModel.Channels.Count.ShouldBe(1); // cập nhật tại chỗ, không thêm dòng mới
        store.SaveChannelCalls.Count.ShouldBe(2);
        store.SaveChannelCalls[1].Id.ShouldBe(row.Id);
        viewModel.IsEditingExisting.ShouldBeFalse();
    }

    [Fact]
    public async Task AddChannel_DuplicateChannel_ShowsStoreErrorVerbatim()
    {
        var (viewModel, store, _) = Create();
        await viewModel.LoadAsync(InstrumentId);
        store.SaveChannelException = new InvalidOperationException("Mã kênh HGB đã map cho chỉ số Huyết sắc tố");
        viewModel.NewChannelCode = "HGB";
        viewModel.NewServiceCode = "HGB";

        await viewModel.AddChannelCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Mã kênh HGB đã map cho chỉ số Huyết sắc tố");
    }
}

/// <summary>Kiểm tra màn "Máy &amp; map" (<see cref="InstrumentsViewModel"/>): nạp danh sách, chọn máy nạp form + bảng kênh, quyền <see cref="Permission.QuanTri"/>.</summary>
public class InstrumentsViewModelTests
{
    private static InstrumentsViewModel CreateViewModel(FakeInstrumentStore store, FakeServiceLookup services, string role = Roles.QuanTri)
    {
        var user = new UserInfo(Guid.NewGuid(), "qt.thu", "Quản trị Thử", null, [role], true);
        return new InstrumentsViewModel(store, services, new CurrentUser(user));
    }

    [Fact]
    public async Task LoadAsync_PopulatesInstruments()
    {
        var instrumentId = Guid.NewGuid();
        var store = new FakeInstrumentStore { Instruments = [new InstrumentInfo(instrumentId, "Máy thử XN-1000", "XN-1000", "astm", null, null, "XN-01", true)] };
        var viewModel = CreateViewModel(store, new FakeServiceLookup());

        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.Instruments.Single().Name.ShouldBe("Máy thử XN-1000");
    }

    [Fact]
    public async Task SelectInstrument_LoadsEditFieldsAndChannels()
    {
        var instrumentId = Guid.NewGuid();
        var store = new FakeInstrumentStore
        {
            Instruments = [new InstrumentInfo(instrumentId, "Máy thử XN-1000", "XN-1000", "astm", """{"a":1}""", null, "XN-01", true)],
        };
        store.ChannelsByInstrument[instrumentId] =
            [new ChannelMapItem(new ChannelMap(Guid.NewGuid(), instrumentId, "HGB", Guid.NewGuid(), null, null, false, false), "HGB", "Huyết sắc tố")];
        var viewModel = CreateViewModel(store, new FakeServiceLookup());
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.SelectedInstrument = viewModel.Instruments.Single();
        // OnSelectedInstrumentChanged nạp ChannelEditor không đồng bộ (fire-and-forget) — gọi lại
        // trực tiếp để kiểm tra không phụ thuộc thời điểm continuation chạy.
        await viewModel.ChannelEditor.LoadAsync(instrumentId);

        viewModel.EditName.ShouldBe("Máy thử XN-1000");
        viewModel.EditDriverConfig.ShouldBe("""{"a":1}""");
        viewModel.ChannelEditor.Channels.Single().ChannelCode.ShouldBe("HGB");
    }

    [Fact]
    public async Task SaveInstrument_RequiresName()
    {
        var store = new FakeInstrumentStore();
        var viewModel = CreateViewModel(store, new FakeServiceLookup());

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Nhập tên máy");
    }

    [Fact]
    public async Task WithoutQuanTriPermission_CannotEdit()
    {
        var viewModel = CreateViewModel(new FakeInstrumentStore(), new FakeServiceLookup(), Roles.KyThuatVien);
        viewModel.CanEdit.ShouldBeFalse();
    }
}
