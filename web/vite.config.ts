import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue()],
  server: {
    // 默认只绑定回环地址；npm run dev:host 会覆盖为所有网卡，供其他电脑访问调试页面。
    host: '127.0.0.1',
    proxy: {
      // xfwd 让后端拿到真实客户端 IP，与生产反向代理行为一致。
      '/api': { target: 'http://localhost:5000', xfwd: true },
      '/health': { target: 'http://localhost:5000', xfwd: true },
      '/hubs': {
        target: 'http://localhost:5000',
        ws: true,
        xfwd: true,
      },
    },
  },
})
