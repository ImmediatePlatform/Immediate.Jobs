import { createMemoryHistory, createRouter } from 'vue-router';
import { describe, expect, it } from 'vitest';

import { router as dashboardRouter, routes } from '@/router';

describe('dashboard routes', () => {
	it('resolves shareable job and batch detail routes', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		await router.push('/definitions');
		expect(router.currentRoute.value.name).toBe('definitions');

		await router.push('/invocations?state=Failed');
		expect(router.currentRoute.value.name).toBe('jobs');

		await router.push('/invocations/redis:jobs:opaque?state=Failed&page=3');
		expect(router.currentRoute.value.name).toBe('job-detail');
		expect(router.currentRoute.value.params.jobHandle).toBe('redis:jobs:opaque');
		expect(router.currentRoute.value.query).toMatchObject({ state: 'Failed', page: '3' });

		await router.push('/batches/batch-42/jobs/job-7?search=batch');
		expect(router.currentRoute.value.name).toBe('batch-job');
		expect(router.currentRoute.value.params).toMatchObject({ batchHandle: 'batch-42', jobHandle: 'job-7' });

		await router.push('/batches/batch-42?search=batch');
		expect(router.currentRoute.value.name).toBe('batch-detail');
		expect(router.currentRoute.value.params.batchHandle).toBe('batch-42');
	});

	it('redirects unknown client routes to the overview', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		await router.push('/not-a-dashboard-route');
		expect(router.currentRoute.value.name).toBe('overview');
	});

	it('starts dedicated job pages at the top and preserves inline batch scrolling', async () => {
		const scrollBehavior = dashboardRouter.options.scrollBehavior;
		expect(scrollBehavior).toBeDefined();
		if (!scrollBehavior) {
			return;
		}

		const router = createRouter({ history: createMemoryHistory(), routes });
		await router.push('/invocations');
		const jobsFrom = router.currentRoute.value;
		await router.push('/invocations/job-7');
		const jobsScroll = await scrollBehavior(router.currentRoute.value, jobsFrom, null);

		await router.push('/batches/batch-42');
		const batchFrom = router.currentRoute.value;
		await router.push('/batches/batch-42/jobs/job-7');
		const batchScroll = await scrollBehavior(router.currentRoute.value, batchFrom, null);

		expect(jobsScroll).toEqual({ top: 0 });
		expect(batchScroll).toBe(false);
	});
});
