import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  base: './',
  plugins: [react()],
  server: {
    port: 4173,
    proxy: {
      '/api': 'http://127.0.0.1:5089',
      '/ws': { target: 'ws://127.0.0.1:5089', ws: true },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      input: {
        app: resolve(import.meta.dirname, 'index.html'),
        callback: resolve(import.meta.dirname, 'callback.html'),
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/testSetup.ts',
  },
});
