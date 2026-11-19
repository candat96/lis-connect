/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

// Dev: /api đi qua proxy tới server .NET (cổng mặc định 5080) để web và API chung origin (như nginx ở môi trường thật).
const BACKEND = process.env.LIS_BACKEND_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@': path.resolve(__dirname, 'src') } },
  // /agent/v1 cho agent chạy dev qua cổng Vite; /agent (không có /v1) là trang web nên không proxy.
  server: {
    proxy: {
      '/api': { target: BACKEND, changeOrigin: false, xfwd: true },
      '/agent/v1': { target: BACKEND, changeOrigin: false, xfwd: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
    testTimeout: 90_000,
  },
});
