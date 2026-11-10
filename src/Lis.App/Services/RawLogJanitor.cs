using Lis.Data.Instruments;
using Lis.Data.Settings;

namespace Lis.App.Services;

/// <summary>
/// Dọn log thô cũ trên đĩa máy trạm theo cài đặt <see cref="SettingKeys.RawLogKeepDays"/>
/// (mặc định 30 ngày — thiết kế mục 10). Chạy khi mở ứng dụng rồi 6 giờ một lần.
/// </summary>
public static class RawLogJanitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Một lần dọn. CSDL không đọc được thì vẫn dọn theo mặc định — log đầy đĩa còn tệ hơn.</summary>
    public static async Task<int> RunOnceAsync(ISettingStore settings, IRawLogStore logs, TimeProvider clock)
    {
        string? value = null;
        try
        {
            value = await settings.GetAsync(SettingKeys.RawLogKeepDays);
        }
        catch (Exception)
        {
            // CSDL chưa lên — dùng mặc định.
        }

        var keepDays = RawLogDefaults.Parse(value, RawLogDefaults.KeepDays);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        return await Task.Run(() => logs.DeleteOlderThan(today, keepDays));
    }

    /// <summary>Vòng lặp nền: dọn ngay rồi mỗi <see cref="Interval"/>, dừng khi <paramref name="keepRunning"/> trả false.</summary>
    public static async Task RunLoopAsync(ISettingStore settings, IRawLogStore logs, TimeProvider clock, Func<bool> keepRunning)
    {
        while (keepRunning())
        {
            try
            {
                await RunOnceAsync(settings, logs, clock);
            }
            catch (Exception ex)
            {
                ErrorLog.Append("Dọn log thô cũ", ex);
            }

            await Task.Delay(Interval);
        }
    }
}
