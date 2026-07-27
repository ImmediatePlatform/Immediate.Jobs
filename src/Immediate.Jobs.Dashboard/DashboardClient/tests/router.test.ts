import { createMemoryHistory, createRouter } from 'vue-router';
import { describe, expect, it } from 'vitest';

import { router as dashboardRouter, routes } from '@/router';

describe('dashboard routes', () => {
	it('resolves shareable job and batch detail routes', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		await router.push('/invocations/redis:jobs:opaque?state=Failed&page=3');
		expect(router.currentRoute.value.name).toBe('jobs');
		expect(router.currentRoute.value.params.jobId).toBe('redis:jobs:opaque');
		expect(router.currentRoute.value.query).toMatchObject({ state: 'Failed', page: '3' });

		await router.push('/batches/batch-42/jobs/job-7?search=batch');
		expect(router.currentRoute.value.name).toBe('batch-job');
		expect(router.currentRoute.value.params).toMatchObject({ batchId: 'batch-42', jobId: 'job-7' });

		await router.push('/batches/batch-42?search=batch');
		expect(router.currentRoute.value.name).toBe('batch-detail');
		expect(router.currentRoute.value.params.batchId).toBe('batch-42');
	});

	it('redirects unknown client routes to the overview', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		await router.push('/not-a-dashboard-route');
		expect(router.currentRoute.value.name).toBe('overview');
	});

	it('lets inline job details control scrolling within their current view', async () => {
		const scrollBehavior = dashboardRouter.options.scrollBehavior;
		expect(scrollBehavior).toBeDefined();
		if (!scrollBehavior) {
			return;
		}

		const jobsScroll = await scrollBehavior(
			dashboardRouter.resolve('/invocations/job-7'),
			dashboardRouter.resolve('/invocations'),
			null,
		);
		const batchScroll = await scrollBehavior(
			dashboardRouter.resolve('/batches/batch-42/jobs/job-7'),
			dashboardRouter.resolve('/batches/batch-42'),
			null,
		);

		expect(jobsScroll).toBe(false);
		expect(batchScroll).toBe(false);
	});
});
