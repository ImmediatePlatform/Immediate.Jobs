import { createApp } from 'vue';
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';

import App from '@/App.vue';
import { router } from '@/router';
import '@/styles.css';

const queryClient = new QueryClient({
	defaultOptions: {
		queries: {
			refetchOnWindowFocus: true,
			staleTime: 1_500,
		},
		mutations: {
			retry: false,
		},
	},
});

createApp(App)
	.use(router)
	.use(VueQueryPlugin, { queryClient })
	.mount('#app');
