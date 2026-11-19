using Lis.Server.RawMessages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Lis.Server.Tests;

/// <summary>Vòng nền bản tin thô: cô lập lỗi theo phòng khám, bỏ qua phòng khám nâng cấp lỗi, nhịp 60 giây và dọn mỗi giờ.</summary>
public sealed class RawMessageWorkerTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ListLogger : ILogger<RawMessageWorker>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            (DateTime.UtcNow < deadline).ShouldBeTrue("Hết thời gian chờ điều kiện");
            await Task.Delay(50, Ct);
        }
    }

    private static Func<Task<bool>> StatusIs(RawEnv env, Guid id, string status) =>
        async () => await env.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @id", new { id }) == status;

    [Fact]
    public async Task Phong_kham_dau_ne_loi_thi_phong_kham_sau_van_duoc_phuc_hoi()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        await using var first = await RawEnv.CreateAsync(fixture, "wk1", host);
        await using var second = await RawEnv.CreateAsync(fixture, "wk2", host);
        var waiting = await second.StoreAsync();
        // Phòng khám wk1 (đứng trước theo mã) hỏng: bảng bản tin biến mất → phục hồi ném lỗi.
        await first.Exec("ALTER TABLE raw_message RENAME TO raw_message_hong");
        var logger = new ListLogger();
        var worker = host.NewRawWorker(TimeProvider.System, logger);

        await worker.RecoverAllAsync(Ct);

        (await second.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @waiting", new { waiting })).ShouldBe("da-giai-ma");
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Text.Contains("wk1") && e.Text.Contains("phục hồi bản tin thô"));
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error && e.Text.Contains("wk2"));
    }

    [Fact]
    public async Task Phong_kham_nang_cap_loi_bi_bo_qua_khong_ghi_loi()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        await host.RegisterUnmigratableClinicAsync("wk0", "Phòng khám Thử hỏng");
        await using var healthy = await RawEnv.CreateAsync(fixture, "wk3", host);
        await host.Provisioner.UpgradeAllAsync();
        host.Provisioner.FailedUpgrades.Count.ShouldBeGreaterThan(0);
        var waiting = await healthy.StoreAsync();
        var logger = new ListLogger();
        var worker = host.NewRawWorker(TimeProvider.System, logger);

        await worker.RecoverAllAsync(Ct);
        await worker.PurgeAllAsync(Ct);

        (await healthy.Scalar<string>("SELECT decode_status FROM raw_message WHERE id = @waiting", new { waiting })).ShouldBe("da-giai-ma");
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Vong_nen_phuc_hoi_luc_khoi_dong_roi_moi_60_giay_va_don_moi_gio()
    {
        await using var host = await ApiHost.StartAsync(fixture);
        await using var env = await RawEnv.CreateAsync(fixture, "wk4", host);
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var worker = host.NewRawWorker(fake, new ListLogger());
        var startup = await env.StoreAsync(seq: 1);

        await worker.StartAsync(Ct);
        try
        {
            // Khởi động: phục hồi ngay.
            await WaitUntilAsync(StatusIs(env, startup, "da-giai-ma"));

            // Bản tin đã xong quá 90 ngày (chèn SAU lượt dọn lúc khởi động) + một bản tin chờ giải mã.
            var old = await env.StoreAsync(seq: 2);
            await env.Exec("UPDATE raw_message SET decode_status = 'da-giai-ma', stored_at = @at WHERE id = @old", new { old, at = fake.GetUtcNow().AddDays(-91).UtcDateTime });
            var waiting = await env.StoreAsync(RawEnv.Hl7("KHONGCO0040"), seq: 3);

            // Chưa đủ 60 giây: chưa phục hồi.
            fake.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(400, Ct);
            (await StatusIs(env, waiting, "cho-giai-ma")()).ShouldBeTrue();

            // Đủ 60 giây: phục hồi, nhưng chưa tới giờ dọn.
            fake.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(StatusIs(env, waiting, "da-giai-ma"));
            (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @old", new { old })).ShouldBe(1);

            // Qua một giờ: dọn bản tin quá 90 ngày.
            fake.Advance(TimeSpan.FromHours(1));
            await WaitUntilAsync(async () => await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @old", new { old }) == 0);
            (await env.Scalar<int>("SELECT count(*)::int FROM raw_message WHERE id = @startup", new { startup })).ShouldBe(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
