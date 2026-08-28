import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';

import BatchDetailView from '@/views/BatchDetailView.vue';
import BatchesView from '@/views/BatchesView.vue';
import JobDetailView from '@/views/JobDetailView.vue';
import JobsView from '@/views/JobsView.vue';
import OverviewView from '@/views/OverviewView.vue';
import RecurringView from '@/views/RecurringView.vue';
import ServersView from '@/views/ServersView.vue';

export const routes: RouteRecordRaw[] = [
	{
		path: '/',
		name: 'overview',
		component: OverviewView,
		meta: { title: 'Overview' },
	},
	{
		path: '/invocations',
		name: 'jobs',
		component: JobsView,
		meta: { title: 'Jobs' },
	},
	{
		path: '/invocations/:jobHandle',
		name: 'job-detail',
		component: JobDetailView,
		meta: { title: 'Job details' },
	},
	{
		path: '/batches/:batchHandle/jobs/:jobHandle',
		name: 'batch-job',
		component: BatchDetailView,
		meta: { title: 'Batch workflow' },
	},
	{
		path: '/batches/:batchHandle',
		name: 'batch-detail',
		component: BatchDetailView,
		meta: { title: 'Batch workflow' },
	},
	{
		path: '/batches',
		name: 'batches',
		component: BatchesView,
		meta: { title: 'Batches' },
	},
	{
		path: '/recurring',
		name: 'recurring',
		component: RecurringView,
		meta: { title: 'Recurring' },
	},
	{
		path: '/servers',
		name: 'servers',
		component: ServersView,
		meta: { title: 'Servers' },
	},
	{
		path: '/:pathMatch(.*)*',
		redirect: { name: 'overview' },
	},
];

const basePath = new URL(document.baseURI).pathname;
const batchDetailRouteNames = new Set(['batch-detail', 'batch-job']);

export const router = createRouter({
	history: createWebHistory(basePath),
	routes,
	scrollBehavior(to, from, savedPosition) {
		if (savedPosition) {
			return savedPosition;
		}
		if (batchDetailRouteNames.has(String(to.name)) && batchDetailRouteNames.has(String(from.name))) {
			return false;
		}
		if (to.path !== from.path) {
			return { top: 0 };
		}
		return undefined;
	},
});

router.afterEach((route) => {
	const title = typeof route.meta.title === 'string' ? route.meta.title : 'Dashboard';
	document.title = `${title} · Immediate.Jobs`;
});
