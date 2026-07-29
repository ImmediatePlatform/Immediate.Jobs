import { fileURLToPath, URL } from 'node:url';

import tailwindcss from '@tailwindcss/vite';
import vue from '@vitejs/plugin-vue';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ command, mode }) => {
	const environment = loadEnv(mode, process.cwd(), '');
	const developmentBase = '/jobs/';

	return {
		plugins: [vue(), tailwindcss()],
		base: command === 'serve' ? developmentBase : './',
		resolve: {
			alias: {
				'@': fileURLToPath(new URL('./src', import.meta.url)),
			},
		},
		server: {
			proxy: {
				[`${developmentBase}api`]: {
					target: environment.DASHBOARD_API_ORIGIN || 'http://localhost:5188',
					changeOrigin: true,
				},
			},
		},
		build: {
			outDir: '../Assets',
			emptyOutDir: true,
			rollupOptions: {
				output: {
					entryFileNames: 'app.js',
					assetFileNames: 'app.[ext]',
					codeSplitting: false,
				},
			},
		},
		test: {
			environment: 'happy-dom',
			setupFiles: ['./tests/setup.ts'],
		},
	};
});
