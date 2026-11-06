using Lis.App.ViewModels.Settings;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.His;
using Lis.Data.Local;
using Shouldly;
using Xunit;

namespace Lis.App.Tests;

/// <summary>
/// Kiểm tra <see cref="HisSettingsViewModel"/>: nút "Thử kết nối" hiện thông báo, mẫu
/// "HIS-PROMAX" điền sẵn các trường, ẩn/hiện trường theo kiểu xác thực.
/// </summary>
public class HisSettingsViewModelTests
{
    private static HisSettingsViewModel CreateViewModel()
    {
        // Db không bao giờ thực sự mở kết nối trong các test này (không gọi LoadAsync/SaveAsync) —
        // NpgsqlDataSource chỉ tạo pool, không kết nối lúc khởi tạo.
        var db = Db.Create("Host=127.0.0.1;Port=5;Database=khong-dung;Username=x;Password=x;Timeout=1");
        var directory = Path.Combine(Path.GetTempPath(), "lis-connect-tests-his-vm-" + Guid.NewGuid());
        var store = new HisProfileStore(db, new DevKeySecretProtector(directory));
        var tester = new HisConnectionTester(new HttpClient());

        var user = new UserInfo(Guid.NewGuid(), "qt.thu", "Quản Trị Thử", null, [Roles.QuanTri], true);
        return new HisSettingsViewModel(store, tester, new CurrentUser(user));
    }

    [Fact]
    public async Task TestButton_Unreachable_ShowsVietnameseMessage()
    {
        var viewModel = CreateViewModel();
        viewModel.BaseUrl = "http://127.0.0.1:5"; // cổng gần như chắc chắn không có ai nghe
        viewModel.AuthType = "none";
        viewModel.OrdersPath = "/orders";

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        viewModel.TestOk.ShouldBeFalse();
        viewModel.TestMessage.ShouldNotBeNullOrEmpty();
        viewModel.TestMessage.ShouldContain("Không kết nối được máy chủ HIS");
    }

    [Fact]
    public async Task TestButton_MissingBaseUrl_ShowsVietnameseMessage_WithoutCallingNetwork()
    {
        var viewModel = CreateViewModel();
        viewModel.BaseUrl = "";

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        viewModel.TestOk.ShouldBeFalse();
        viewModel.TestMessage.ShouldBe("Nhập địa chỉ máy chủ HIS (base URL)");
    }

    [Fact]
    public void ApplyHisPromaxPreset_FillsAuthTypeAndOrdersPath()
    {
        var viewModel = CreateViewModel();

        viewModel.ApplyHisPromaxPresetCommand.Execute(null);

        viewModel.Name.ShouldBe("HIS-PROMAX");
        viewModel.AuthType.ShouldBe("api-key");
        viewModel.ApiKeyHeader.ShouldBe("X-Integration-Key");
        viewModel.OrdersPath.ShouldBe("/api/v1/system/info");
    }

    [Theory]
    [InlineData("none", false, false, false)]
    [InlineData("api-key", true, false, false)]
    [InlineData("basic", false, true, false)]
    [InlineData("bearer-login", false, false, true)]
    public void AuthType_TogglesVisibleFields(string authType, bool apiKey, bool basic, bool bearerLogin)
    {
        var viewModel = CreateViewModel();

        viewModel.AuthType = authType;

        viewModel.ShowApiKeyFields.ShouldBe(apiKey);
        viewModel.ShowBasicFields.ShouldBe(basic);
        viewModel.ShowBearerLoginFields.ShouldBe(bearerLogin);
    }

    [Fact]
    public async Task TestButton_BearerLoginWithoutLoginPath_ShowsVietnameseMessage()
    {
        var viewModel = CreateViewModel();
        viewModel.BaseUrl = "http://127.0.0.1:5";
        viewModel.AuthType = "bearer-login";
        viewModel.LoginPath = "";

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        viewModel.TestOk.ShouldBeFalse();
        viewModel.TestMessage.ShouldBe("Nhập đường đăng nhập (login_path) khi dùng xác thực bearer-login");
    }
}
