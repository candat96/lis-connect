import { lazy, Suspense } from 'react';
import { Spin } from 'antd';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppProviders } from '@/app/providers/AppProviders';
import { MainLayout } from '@/layouts/MainLayout';

const LoginPage = lazy(() => import('@/modules/dang-nhap/LoginPage'));
const ResultsPage = lazy(() => import('@/modules/ket-qua/ResultsPage'));
const CatalogPage = lazy(() => import('@/modules/danh-muc/CatalogPage'));
const InstrumentsPage = lazy(() => import('@/modules/may/InstrumentsPage'));

export default function App() {
  return (
    <AppProviders>
      <BrowserRouter>
        <Suspense fallback={<Spin style={{ margin: 48 }} />}>
          <Routes>
            <Route path="/dang-nhap" element={<LoginPage />} />
            <Route element={<MainLayout />}>
              <Route path="/ket-qua" element={<ResultsPage />} />
              <Route path="/danh-muc" element={<CatalogPage />} />
              <Route path="/may" element={<InstrumentsPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/ket-qua" replace />} />
          </Routes>
        </Suspense>
      </BrowserRouter>
    </AppProviders>
  );
}
