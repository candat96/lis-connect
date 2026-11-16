import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';

// findBy*/waitFor mặc định chỉ chờ 1 giây: kiểm dạng của antd chạy bất đồng bộ, trên máy chậm (runner CI, hay máy đang chạy
// test .NET song song) dễ quá 1 giây và test đỏ dù giao diện đúng. Chờ tới 10 giây — test đúng vẫn xong ngay khi chữ hiện ra.
configure({ asyncUtilTimeout: 10_000 });
