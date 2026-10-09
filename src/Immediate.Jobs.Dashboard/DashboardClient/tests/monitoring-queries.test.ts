import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { defineComponent } from 'vue';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiError, getDefinitions, getOverview } from '@/api';
import { applyDashboardState, queryKeys, useDefinitionStatusesQuery, useOverviewQuery, useRecurringQuery, useServersQuery } from '@/query';
import { connectionStatus } from '@/stream-state';
import { pausedDefinition } from './fixtures';

vi.mock('@/api', async importOriginal => ({
	...await importOriginal<typeof import('@/api')>(),
	getDefinitions: vi.fn(),
	getOverview: vi.fn(),
}));

const snapshot = {
	capturedAt: '2026-07-21T12:00:00Z', counts: {}, servers: [], recurring: [],
	definitionStatuses: [pausedDefinition.acquisition],
};
const metadata = { jobs: [pausedDefinition], recurring: [] };
const wrappers: ReturnType<typeof mount>[] = [];
const clients: QueryClient[] = [];
function mountQueries(client: QueryClient, setup: () => unknown) {
	const wrapper = mount(defineComponent({ setup, template: '<div />' }), {
		global: { plugins: [[VueQueryPlugin, { queryClient: client }]] },
	});
	wrappers.push(wrapper);
	return wrapper;
}
function client() {
	const result = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	clients.push(result);
	return result;
}

beforeEach(() => {
	connectionStatus.value = 'reconnecting';
	vi.mocked(getOverview).mockReset().mockResolvedValue(snapshot);
	vi.mocked(getDefinitions).mockReset().mockResolvedValue(metadata);
});
afterEach(() => {
	wrappers.splice(0).forEach(wrapper => wrapper.unmount());
	clients.splice(0).forEach(queryClient => queryClient.clear());
	vi.useRealTimers();
});

describe('shared monitoring queries', () => {
	it('deduplicates overview, servers, definitions status and recurring snapshot loading', async () => {
		const queryClient = client();
		mountQueries(queryClient, () => {
			useOverviewQuery(); useServersQuery(); useDefinitionStatusesQuery(); useRecurringQuery();
		});
		await flushPromises();
		expect(getOverview).toHaveBeenCalledTimes(1);
		expect(getDefinitions).toHaveBeenCalledTimes(1);
	});

	it('keeps metadata through navigation and updates only live status from SSE', async () => {
		const queryClient = client();
		let statuses!: ReturnType<typeof useDefinitionStatusesQuery>;
		const page = mountQueries(queryClient, () => { statuses = useDefinitionStatusesQuery(); });
		await flushPromises();
		expect(statuses.data.value[0]?.acquisition?.isPaused).toBe(true);
		connectionStatus.value = 'live';
		applyDashboardState(queryClient, {
			snapshot: { ...snapshot, definitionStatuses: [{ ...pausedDefinition.acquisition, isPaused: false }] }, jobs: [], batches: [],
		});
		await flushPromises();
		expect(statuses.data.value[0]?.acquisition?.isPaused).toBe(false);
		expect(queryClient.getQueryData(queryKeys.definitions)).toEqual(metadata);
		page.unmount();
		mountQueries(queryClient, () => { useRecurringQuery(); useServersQuery(); });
		await flushPromises();
		expect(getDefinitions).toHaveBeenCalledTimes(1);
		expect(getOverview).toHaveBeenCalledTimes(1);
	});

	it('polls status every five seconds on disconnect and metadata every five minutes', async () => {
		vi.useFakeTimers();
		const queryClient = client();
		mountQueries(queryClient, () => { useDefinitionStatusesQuery(); });
		await vi.advanceTimersByTimeAsync(0);
		expect(getOverview).toHaveBeenCalledTimes(1);
		expect(getDefinitions).toHaveBeenCalledTimes(1);
		await vi.advanceTimersByTimeAsync(5_000);
		expect(getOverview).toHaveBeenCalledTimes(2);
		expect(getDefinitions).toHaveBeenCalledTimes(1);
		connectionStatus.value = 'live';
		await vi.advanceTimersByTimeAsync(295_000);
		expect(getOverview).toHaveBeenCalledTimes(2);
		expect(getDefinitions).toHaveBeenCalledTimes(2);
	});

	it('does not invent status or enable actions when the snapshot is unavailable', async () => {
		vi.mocked(getOverview).mockRejectedValue(new ApiError('Snapshot unavailable', 400));
		let statuses!: ReturnType<typeof useDefinitionStatusesQuery>;
		mountQueries(client(), () => { statuses = useDefinitionStatusesQuery(); });
		await flushPromises();
		expect(statuses.data.value[0]?.acquisition).toBeUndefined();
		expect(statuses.error.value?.message).toBe('Snapshot unavailable');
	});
});
