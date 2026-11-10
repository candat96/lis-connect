import { useState, type ReactNode } from 'react';
import { App as AntApp, ConfigProvider } from 'antd';
import viVN from 'antd/locale/vi_VN';
import { QueryClientProvider } from '@tanstack/react-query';
import dayjs from 'dayjs';
import 'dayjs/locale/vi';
import { antdTheme } from '@/app/theme/antdTheme';
import { createQueryClient } from '@/services';

dayjs.locale('vi');

export function AppProviders({ children }: { children: ReactNode }) {
  // Mỗi cây App một QueryClient riêng để test không dùng chung cache
  const [client] = useState(createQueryClient);
  return (
    <ConfigProvider theme={antdTheme} locale={viVN} componentSize="small">
      <AntApp>
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      </AntApp>
    </ConfigProvider>
  );
}
