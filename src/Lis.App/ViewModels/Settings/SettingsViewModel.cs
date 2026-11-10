using CommunityToolkit.Mvvm.ComponentModel;
using Lis.Data.Local;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// ViewModel của mục "Cài đặt": 6 tab (kể cả Log thô — <see cref="RawLogSettingsViewModel"/>) — Thông tin cơ sở, Barcode &amp; tem (dùng chung
/// <see cref="FacilitySettingsViewModel"/>), Máy in (<see cref="PrinterSettingsViewModel"/>,
/// không nhân bản logic), Kết nối CSDL (<see cref="DbConnectionViewModel"/>) và Kết nối HIS
/// (<see cref="HisSettingsViewModel"/>) — hai tab sau chỉ cho vai trò <c>quan-tri</c>, null với
/// vai trò khác nên tab không hiện.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(
        FacilitySettingsViewModel facility,
        PrinterSettingsViewModel printer,
        DbConnectionViewModel? dbConnection,
        HisSettingsViewModel? hisConnection,
        RawLogSettingsViewModel? rawLog = null)
    {
        RawLog = rawLog;
        Facility = facility;
        Printer = printer;
        DbConnection = dbConnection;
        HisConnection = hisConnection;
    }

    public FacilitySettingsViewModel Facility { get; }

    public PrinterSettingsViewModel Printer { get; }

    /// <summary>null khi người dùng hiện tại không có vai trò <c>quan-tri</c> — tab "Kết nối CSDL" ẩn.</summary>
    public DbConnectionViewModel? DbConnection { get; }

    /// <summary>null khi người dùng hiện tại không có vai trò <c>quan-tri</c> — tab "Kết nối HIS" ẩn.</summary>
    public HisSettingsViewModel? HisConnection { get; }

    /// <summary>Tab "Log thô" — null thì ẩn (vd màn chụp ảnh không cần).</summary>
    public RawLogSettingsViewModel? RawLog { get; }

    public bool ShowRawLog => RawLog is not null;

    public bool ShowDbConnection => DbConnection is not null;

    public bool ShowHisConnection => HisConnection is not null;

    private bool _loaded;

    public async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await Facility.LoadAsync();
        await Printer.LoadAsync();

        if (HisConnection is not null)
        {
            await HisConnection.LoadAsync();
        }

        if (RawLog is not null)
        {
            await RawLog.LoadAsync();
        }
    }
}
