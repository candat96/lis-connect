using CommunityToolkit.Mvvm.ComponentModel;
using Lis.Data.Local;

namespace Lis.App.ViewModels.Settings;

/// <summary>
/// ViewModel của mục "Cài đặt": 4 tab — Thông tin cơ sở, Barcode &amp; tem (dùng chung
/// <see cref="FacilitySettingsViewModel"/>), Máy in (<see cref="PrinterSettingsViewModel"/>,
/// không nhân bản logic) và Kết nối CSDL (<see cref="DbConnectionViewModel"/>, chỉ cho vai trò
/// <c>quan-tri</c> — null với vai trò khác, tab không hiện).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(
        FacilitySettingsViewModel facility,
        PrinterSettingsViewModel printer,
        DbConnectionViewModel? dbConnection)
    {
        Facility = facility;
        Printer = printer;
        DbConnection = dbConnection;
    }

    public FacilitySettingsViewModel Facility { get; }

    public PrinterSettingsViewModel Printer { get; }

    /// <summary>null khi người dùng hiện tại không có vai trò <c>quan-tri</c> — tab "Kết nối CSDL" ẩn.</summary>
    public DbConnectionViewModel? DbConnection { get; }

    public bool ShowDbConnection => DbConnection is not null;

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
    }
}
