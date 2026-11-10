// Roboto (variable, có bộ chữ tiếng Việt) — cùng font với clinic-web
import '@fontsource-variable/roboto/index.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { applyCssVars } from './app/theme/tokens';
import './app/theme/global.css';
import './app/theme/lis.css';

applyCssVars();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
