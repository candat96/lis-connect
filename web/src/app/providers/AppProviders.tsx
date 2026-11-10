import { useState, type ReactNode } from 'react';
import { App as AntApp, ConfigProvider } from 'antd';
import viVN from 'antd/locale/vi_VN';
import { QueryClientProvider } from '@tanstack/react-query';
import dayjs from 'dayjs';
import 'dayjs/locale/vi';
import { theme } from '@/app/theme/tokens';
import { createQueryClient } from '@/services';

dayjs.locale('vi');

export function AppProviders({ children }: { children: ReactNode }) {
  // Mỗi cây App một QueryClient riêng để test không dùng chung cache
  const [client] = useState(createQueryClient);
  return (
    // Cấu hình như clinic-web: theme từ tokens, ô chọn thả xuống rộng theo nội dung
    <ConfigProvider theme={theme} locale={viVN} popupMatchSelectWidth={false}>
      <AntApp style={{ height: '100%' }}>
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  );
}
