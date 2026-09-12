import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Слушаем все интерфейсы (в т.ч. 127.0.0.1 и LAN-IP), а не только ::1.
    // На Windows дефолтный `localhost` резолвится в IPv6 ::1, из-за чего
    // сервер "запущен", но не открывается по 127.0.0.1 / с телефона.
    host: true,
    port: 5173,
    strictPort: true,
  },
})
