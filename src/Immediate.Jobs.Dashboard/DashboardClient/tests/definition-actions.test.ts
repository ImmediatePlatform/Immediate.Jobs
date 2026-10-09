import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getDefinitions, getOverview, setDefinitionPaused } from '@/api';
import { notifications } from '@/notifications';
import { queryKeys } from '@/query';
import { routes } from '@/router';
import DefinitionsView from '@/views/DefinitionsView.vue';
import { pausedDefinition } from './fixtures';

vi.mock('@/api', async importOriginal => ({
	...await importOriginal<typeof import('@/api')>(),
	getDefinitions: vi.fn(),
	getOverview: vi.fn(),
	setDefinitionPaused: vi.fn(),
}));

const getDefinitionsMock = vi.mocked(getDefinitions);
const getOverviewMock = vi.mocked(getOverview);
function snapshot(definition = pausedDefinition) {
	return { capturedAt: '2026-07-21T12:00:00Z', counts: {}, recurring: [], servers: [], definitionStatuses: [definition.acquisition] };
}
const setDefinitionPausedMock = vi.mocked(setDefinitionPaused);
const limitedDefinition = {
	...pausedDefinition,
	acquisition: { ...pausedDefinition.acquisition, isPaused: false, acquisitionStatus: 'RateLimited' as const },
};

async function mountView() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
	const router = createRouter({ history: createMemoryHistory(), routes });
	await router.push('/definitions');
	const wrapper = mount(DefinitionsView, {
		global: { plugins: [router, [VueQueryPlugin, { queryClient }]] },
	});
	await vi.waitFor(() => expect(wrapper.find('tbody tr').exists()).toBe(true));
	return { wrapper, queryClient };
}

describe('definition actions', () => {
	beforeEach(() => {
		getDefinitionsMock.mockReset().mockResolvedValue({ jobs: [pausedDefinition], recurring: [] });
		getOverviewMock.mockReset().mockResolvedValue(snapshot());
		setDefinitionPausedMock.mockReset().mockResolvedValue(undefined);
		notifications.value = [];
	});

	it('resumes a paused definition, prevents duplicate requests, and refreshes its actual limits', async () => {
		let finish!: () => void;
		setDefinitionPausedMock.mockImplementation(() => new Promise<void>(resolve => { finish = resolve; }));
		const { wrapper, queryClient } = await mountView();
		const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
		await wrapper.get('button').trigger('click');
		await flushPromises();
		expect(setDefinitionPausedMock).toHaveBeenCalledWith(pausedDefinition.name, false);
		expect(wrapper.get('button').attributes('disabled')).toBeDefined();
		expect(wrapper.get('button').text()).toBe('Updating…');
		await wrapper.get('button').trigger('click');
		expect(setDefinitionPausedMock).toHaveBeenCalledTimes(1);

		getOverviewMock.mockResolvedValue(snapshot(limitedDefinition));
		finish();
		await vi.waitFor(() => expect(wrapper.get('button').text()).toBe('Pause'));
		expect(wrapper.text()).toContain('Rate limited');
		expect(invalidate).not.toHaveBeenCalledWith({ queryKey: queryKeys.definitions });
		expect(getDefinitionsMock).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.overview });
		expect(notifications.value.at(-1)?.message).toBe('Definition resumed.');
		wrapper.unmount();
		queryClient.clear();
	});

	it('allows pausing a rate-limited definition and shows the refreshed paused status', async () => {
		getOverviewMock.mockResolvedValue(snapshot(limitedDefinition));
		const { wrapper, queryClient } = await mountView();
		getOverviewMock.mockResolvedValue(snapshot());
		await wrapper.get('button').trigger('click');
		await vi.waitFor(() => expect(wrapper.get('button').text()).toBe('Resume'));
		expect(setDefinitionPausedMock).toHaveBeenCalledWith(pausedDefinition.name, true);
		expect(wrapper.text()).toContain('Paused');
		expect(notifications.value.at(-1)?.message).toBe('Definition paused.');
		wrapper.unmount();
		queryClient.clear();
	});

	it('reports mutation failures without changing the displayed pause state', async () => {
		setDefinitionPausedMock.mockRejectedValue(new Error('Definition could not be resumed.'));
		const { wrapper, queryClient } = await mountView();
		await wrapper.get('button').trigger('click');
		await vi.waitFor(() => expect(notifications.value.at(-1)?.tone).toBe('error'));
		expect(notifications.value.at(-1)?.message).toBe('Definition could not be resumed.');
		expect(wrapper.get('button').text()).toBe('Resume');
		expect(wrapper.get('button').attributes('disabled')).toBeUndefined();
		wrapper.unmount();
		queryClient.clear();
	});
});
