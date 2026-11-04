using Lis.Data.Settings;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Settings;

/// <summary>Kiểm tra bảng <c>setting</c> (khóa/giá trị): ghi, đọc lại, ghi đè và xóa giá trị (null).</summary>
[Collection(PostgresCollection.Name)]
public class SettingStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Setting_RoundTrip_AndOverwrite()
    {
        var db = Db.Create(await fixture.NewDatabaseAsync());
        var store = new SettingStore(db);

        (await store.GetAsync(SettingKeys.LabelSize)).ShouldBeNull();

        await store.SetAsync(SettingKeys.LabelSize, "50x25");
        (await store.GetAsync(SettingKeys.LabelSize)).ShouldBe("50x25");

        await store.SetAsync(SettingKeys.LabelSize, "40x20");
        (await store.GetAsync(SettingKeys.LabelSize)).ShouldBe("40x20");

        await store.SetAsync(SettingKeys.LabelPrinter, "Xprinter XP-350B");
        await store.SetAsync(SettingKeys.LabelPrinter, null);
        (await store.GetAsync(SettingKeys.LabelPrinter)).ShouldBeNull();
    }
}
