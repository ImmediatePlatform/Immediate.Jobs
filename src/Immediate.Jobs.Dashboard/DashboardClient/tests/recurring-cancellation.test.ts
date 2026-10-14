import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { expect, it, vi } from 'vitest';

import { getDefinitions, getOverview, setRecurringPaused, triggerRecurring } from '@/api';
import { routes } from '@/router';
import RecurringView from '@/views/RecurringView.vue';

vi.mock('@/api', async importOriginal => ({
	...await importOriginal<typeof import('@/api')>(),
	getDefinitions: vi.fn(),
	getOverview: vi.fn(),
	setRecurringPaused: vi.fn(),
	triggerRecurring: vi.fn(),
}));

it('keeps canceled schedules visible with the reason and disables execution actions', async () => {
	vi.mocked(getDefinitions).mockResolvedValue({ jobs: [], recurring: [{
		name: 'removed', jobName: 'Remote', cron: '* * * * *', timeZone: 'UTC', isCodeDefined: false,
	}] });
	vi.mocked(getOverview).mockResolvedValue({
		capturedAt: '2026-07-21T12:00:00Z', counts: {}, servers: [], definitionStatuses: [],
		recurring: [{ name: 'removed', isPaused: true, cancellationReason: 'Job definition removed.', nextRunAt: '2026-07-21T12:00:00Z', lastRunAt: null }],
	});
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
	const router = createRouter({ history: createMemoryHistory(), routes });
	await router.push('/recurring');
	const wrapper = mount(RecurringView, { global: { plugins: [router, [VueQueryPlugin, { queryClient }]] } });
	await vi.waitFor(() => expect(wrapper.find('tbody tr').exists()).toBe(true));
	expect(wrapper.text()).toContain('Cancelled');
	expect(wrapper.text()).toContain('Job definition removed.');
	for (const button of wrapper.findAll('tbody button')) {
		expect(button.attributes('disabled')).toBeDefined();
		await button.trigger('click');
	}
	expect(setRecurringPaused).not.toHaveBeenCalled();
	expect(triggerRecurring).not.toHaveBeenCalled();
	wrapper.unmount();
	queryClient.clear();
});
