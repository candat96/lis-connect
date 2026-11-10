import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export interface SessionUser {
  id: string;
  username: string;
  fullName: string;
  title: string | null;
  roles: string[];
}

export interface SessionClinic {
  code: string;
  name: string;
}

interface SessionState {
  token: string | null;
  user: SessionUser | null;
  clinic: SessionClinic | null;
  setSession: (token: string, user: SessionUser, clinic: SessionClinic) => void;
  clear: () => void;
}

// Phiên đăng nhập giữ ở localStorage: mở lại trình duyệt không phải đăng nhập lại cho tới khi
// token hết hạn (12 giờ) hoặc bấm Đăng xuất.
export const useSessionStore = create<SessionState>()(
  persist(
    (set) => ({
      token: null,
      user: null,
      clinic: null,
      setSession: (token, user, clinic) => set({ token, user, clinic }),
      clear: () => set({ token: null, user: null, clinic: null }),
    }),
    { name: 'lis-session' },
  ),
);
