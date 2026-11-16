// Mã phòng khám và tên đăng nhập của lần đăng nhập gần nhất — không bao giờ nhớ mật khẩu.
export interface LastLogin {
  clinicCode?: string;
  username?: string;
}

const LAST_KEY = 'lis-last-login';

export const readLastLogin = (): LastLogin => {
  try {
    return JSON.parse(localStorage.getItem(LAST_KEY) ?? '{}') as LastLogin;
  } catch {
    return {};
  }
};

export const saveLastLogin = (clinicCode: string, username: string) => {
  try {
    localStorage.setItem(LAST_KEY, JSON.stringify({ clinicCode, username }));
  } catch {
    /* bỏ qua */
  }
};
