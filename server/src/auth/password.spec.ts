import { DUMMY_HASH, verifyPassword } from './password';

describe('kiểm mật khẩu Argon2id', () => {
  // Chuỗi băm do app .NET sinh (Lis.Data.Auth.PasswordHasher) cho mật khẩu "Mật-khẩu thử 123" —
  // bảo đảm tài khoản tạo ở app Windows đăng nhập được trên web.
  const fromDotNet = 'argon2id$65536$3$1$kfjfxc/4Zn7RmyKC1PeZEA==$hY12jGS2rdPSauxWTLRWrQXoqVyy9eg29Um53AwWYw0=';

  it('khớp mật khẩu băm bởi app .NET', async () => {
    expect(await verifyPassword('Mật-khẩu thử 123', fromDotNet)).toBe(true);
  });

  it('sai mật khẩu → false', async () => {
    expect(await verifyPassword('Mật-khẩu thử 124', fromDotNet)).toBe(false);
    expect(await verifyPassword('bất kỳ', DUMMY_HASH)).toBe(false);
  });

  it('chuỗi băm hỏng hoặc tham số vô lý → false, không ném lỗi', async () => {
    expect(await verifyPassword('x', '')).toBe(false);
    expect(await verifyPassword('x', 'bcrypt$10$abc')).toBe(false);
    expect(await verifyPassword('x', 'argon2id$0$3$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAA')).toBe(false);
    expect(await verifyPassword('x', 'argon2id$99999999$3$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAA')).toBe(false);
    expect(await verifyPassword('x', 'argon2id$65536$3$1$!!$!!')).toBe(false);
  });
});
