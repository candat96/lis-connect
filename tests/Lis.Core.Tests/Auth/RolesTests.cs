using Lis.Core.Auth;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Auth;

public class RolesTests
{
    [Fact]
    public void TruongKhoa_CanUnapprove_KyThuatVienCannot()
    {
        Permissions.Has([Roles.TruongKhoa], Permission.HuyDuyet).ShouldBeTrue();
        Permissions.Has([Roles.KyThuatVien], Permission.HuyDuyet).ShouldBeFalse();
    }
}
