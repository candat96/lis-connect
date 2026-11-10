using CommunityToolkit.Mvvm.Input;
using Lis.Data.Instruments;

namespace Lis.App.ViewModels.Instruments;

/// <summary>Một dịch vụ đã khai là máy chạy được: mã, tên, mã kênh (nếu đã dò ra / đã map).</summary>
public sealed class DeclaredRowViewModel
{
    public DeclaredRowViewModel(DeclaredService service, Func<DeclaredRowViewModel, Task> remove)
    {
        Service = service;
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    public DeclaredService Service { get; }

    public string Code => Service.Code;

    public string Name => Service.Name;

    public bool HasChannel => !string.IsNullOrEmpty(Service.ChannelCode);

    public string ChannelText => Service.ChannelCode ?? "chờ dò";

    public IAsyncRelayCommand RemoveCommand { get; }
}
