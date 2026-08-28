import { QueryClient } from '@tanstack/vue-query';
import { beforeEach, describe, expect, it } from 'vitest';

import type { DashboardState } from '@/contracts';
import { applyDashboardState, queryKeys } from '@/query';
import { dashboardHistory } from '@/stream-state';
import { completedJob, executingBatch } from './fixtures';

describe('live dashboard cache', () => {
	beforeEach(() => {
		dashboardHistory.value = [];
	});

	it('applies state events to the related TanStack Query caches', () => {
		const queryClient = new QueryClient();
		const state: DashboardState = {
			snapshot: {
				capturedAt: '2026-07-21T12:00:00Z',
				counts: { Pending: 2, Scheduled: 1, Succeeded: 8, Failed: 1 },
				recurring: [],
				servers: [],
			},
			jobs: [completedJob],
			batches: [executingBatch],
		};

		applyDashboardState(queryClient, state);

		expect(queryClient.getQueryData(queryKeys.overview)).toEqual(state.snapshot);
		expect(queryClient.getQueryData(queryKeys.recentJobs)).toEqual([completedJob]);
		expect(queryClient.getQueryData(queryKeys.batches)).toEqual([executingBatch]);
		expect(queryClient.getQueryData(queryKeys.job(completedJob.jobId))).toEqual(completedJob);
		expect(dashboardHistory.value).toEqual([{
			capturedAt: state.snapshot.capturedAt,
			complete: 9,
			throughput: 0,
			queued: 3,
		}]);
	});
});
