import { lazy, Suspense } from 'react';
import { Spin } from 'antd';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppProviders } from '@/app/providers/AppProviders';
import { MainLayout } from '@/layouts/MainLayout';

const LoginPage = lazy(() => import('@/modules/dang-nhap/LoginPage'));
const SetupPage = lazy(() => import('@/modules/thiet-lap/SetupPage'));
const ReceptionPage = lazy(() => import('@/modules/tiep-nhan/ReceptionPage'));
const ResultsPage = lazy(() => import('@/modules/ket-qua/ResultsPage'));
const CatalogPage = lazy(() => import('@/modules/danh-muc/CatalogPage'));
const InstrumentsPage = lazy(() => import('@/modules/may/InstrumentsPage'));
const AgentsPage = lazy(() => import('@/modules/agent/AgentsPage'));
const LogsPage = lazy(() => import('@/modules/agent/LogsPage'));
const RawMessagesPage = lazy(() => import('@/modules/ban-tin/RawMessagesPage'));
const UsersPage = lazy(() => import('@/modules/nhan-su/UsersPage'));

export default function App() {
  return (
    <AppProviders>
      <BrowserRouter>
        <Suspense fallback={<Spin style={{ margin: 48 }} />}>
          <Routes>
            <Route path="/dang-nhap" element={<LoginPage />} />
            <Route path="/thiet-lap" element={<SetupPage />} />
            <Route element={<MainLayout />}>
              <Route path="/tiep-nhan" element={<ReceptionPage />} />
              <Route path="/ket-qua" element={<ResultsPage />} />
              <Route path="/danh-muc" element={<CatalogPage />} />
              <Route path="/may" element={<InstrumentsPage />} />
              <Route path="/agent" element={<AgentsPage />} />
              <Route path="/log-agent" element={<LogsPage />} />
              <Route path="/ban-tin" element={<RawMessagesPage />} />
              <Route path="/nhan-su" element={<UsersPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/ket-qua" replace />} />
          </Routes>
        </Suspense>
      </BrowserRouter>
    </AppProviders>
  );
}
