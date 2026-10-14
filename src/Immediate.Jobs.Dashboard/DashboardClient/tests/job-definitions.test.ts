import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError, getDefinitions, getJob, getJobExecutions, getJobTelemetryLinks, getJobs, getOverview } from '@/api';
import { applyDashboardState } from '@/query';
import { routes } from '@/router';
import JobDetailView from '@/views/JobDetailView.vue';
import JobsView from '@/views/JobsView.vue';
import { completedJob, pausedDefinition } from './fixtures';

vi.mock('@/api', async importOriginal => ({
	...await importOriginal<typeof import('@/api')>(),
	getDefinitions: vi.fn(),
	getOverview: vi.fn(),
	getJobs: vi.fn(),
	getJob: vi.fn(),
	getJobTelemetryLinks: vi.fn(),
	getJobExecutions: vi.fn(),
}));

const job = { ...completedJob, state: 'Pending' as const, jobName: 'sendgreeting' };
const snapshot = { capturedAt: job.dueAt, counts: {}, servers: [], recurring: [], definitionStatuses: [pausedDefinition.acquisition] };
const wrappers: ReturnType<typeof mount>[] = [];
const clients: QueryClient[] = [];

beforeEach(() => {
	vi.mocked(getOverview).mockReset().mockResolvedValue(snapshot);
	vi.mocked(getDefinitions).mockReset().mockResolvedValue({ jobs: [pausedDefinition], recurring: [] });
	vi.mocked(getJobs).mockReset().mockResolvedValue({ items: [job], skip: 0, take: 50, hasNext: false });
	vi.mocked(getJob).mockReset().mockResolvedValue(job);
	vi.mocked(getJobTelemetryLinks).mockReset().mockResolvedValue([]);
	vi.mocked(getJobExecutions).mockReset().mockResolvedValue({ items: [], skip: 0, take: 20, hasNext: false });
});

afterEach(() => {
	wrappers.splice(0).forEach(wrapper => wrapper.unmount());
	clients.splice(0).forEach(client => client.clear());
});

async function mountView(detail: boolean) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
	clients.push(queryClient);
	const router = createRouter({ history: createMemoryHistory(), routes });
	await router.push(detail ? `/invocations/${job.jobHandle}` : '/invocations');
	const wrapper = mount(detail ? JobDetailView : JobsView, {
		global: { plugins: [router, [VueQueryPlugin, { queryClient }]] },
	});
	wrappers.push(wrapper);
	await flushPromises();
	return { wrapper, queryClient };
}

describe('job definition monitoring', () => {
	it('uses live definition status on the Jobs screen without loading configuration', async () => {
		const { wrapper, queryClient } = await mountView(false);
		expect(wrapper.get('tbody .state-badge').text()).toBe('Paused');
		expect(getDefinitions).not.toHaveBeenCalled();
		applyDashboardState(queryClient, { jobs: [job], batches: [], snapshot: { ...snapshot, definitionStatuses: [{ ...pausedDefinition.acquisition, isPaused: false, acquisitionStatus: 'Ready' }] } });
		await flushPromises();
		expect(wrapper.get('tbody .state-badge').text()).toBe('Pending');
	});

	it('shows the known pause even when definition configuration cannot be loaded', async () => {
		vi.mocked(getDefinitions).mockRejectedValue(new ApiError('Definition service unavailable', 400));
		const { wrapper } = await mountView(true);
		expect(wrapper.get('.inspector-content > div > .state-badge').text()).toBe('Paused');
		expect(wrapper.get('.definition-details').text()).toContain('Acquisition is paused');
		expect(wrapper.get('.definition-details').text()).toContain('Definition service unavailable');
	});

	it('combines current definition configuration and live status in job details', async () => {
		const { wrapper, queryClient } = await mountView(true);
		expect(wrapper.get('.inspector-content > div > .state-badge').text()).toBe('Paused');
		expect(wrapper.get('.definition-details').text()).toContain('SendGreeting');
		expect(wrapper.get('.definition-details').text()).toContain('Acquisition is paused');
		expect(getDefinitions).toHaveBeenCalledTimes(1);
		applyDashboardState(queryClient, { jobs: [job], batches: [], snapshot: { ...snapshot, definitionStatuses: [{ ...pausedDefinition.acquisition, isPaused: false, acquisitionStatus: 'Ready' }] } });
		await flushPromises();
		expect(wrapper.get('.inspector-content > div > .state-badge').text()).toBe('Pending');
		expect(wrapper.get('.definition-details').text()).not.toContain('Acquisition is paused');
		expect(getDefinitions).toHaveBeenCalledTimes(1);
		applyDashboardState(queryClient, { jobs: [{ ...job, state: 'Cancelled', cancellationReason: 'Job definition removed.' }], batches: [], snapshot: { ...snapshot, definitionStatuses: [] } });
		await flushPromises();
		expect(wrapper.get('.definition-details').text()).toContain('Definition information is unavailable.');
		expect(wrapper.get('.definition-details').find('dl').exists()).toBe(false);
		expect(wrapper.text()).toContain('Job definition removed.');
	});
});
