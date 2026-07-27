import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import BatchTable from '@/components/BatchTable.vue';
import HistoryChart from '@/components/HistoryChart.vue';
import JobDetail from '@/components/JobDetail.vue';
import JobTable from '@/components/JobTable.vue';
import WorkflowGraph from '@/components/WorkflowGraph.vue';
import { completedJob, executingBatch, workflowGraph } from './fixtures';

describe('dashboard components', () => {
	it('renders job rows and complete payload/context details', () => {
		const table = mount(JobTable, { props: { rows: [completedJob] } });
		expect(table.text()).toContain('SendGreeting');
		expect(table.text()).toContain('Succeeded');
		expect(table.text()).toContain('batch-42');

		const detail = mount(JobDetail, { props: { job: completedJob } });
		expect(detail.attributes('aria-label')).toBe('Details for SendGreeting');
		expect(detail.text()).toContain('Payload');
		expect(detail.text()).toContain('Duke');
		expect(detail.text()).toContain('Context envelope');
		expect(detail.text()).toContain('curl/8.7.1');
	});

	it('expands selected job details immediately below its row and scrolls the row into view', async () => {
		const scrollIntoView = vi.spyOn(HTMLElement.prototype, 'scrollIntoView');
		const wrapper = mount(JobTable, {
			props: { rows: [completedJob], selectedId: completedJob.id },
			slots: { details: '<div data-testid="inline-details">Selected details</div>' },
		});
		await flushPromises();

		const bodyRows = wrapper.findAll('tbody tr');
		expect(bodyRows).toHaveLength(2);
		expect(bodyRows[0]?.attributes('data-job-id')).toBe(completedJob.id);
		expect(bodyRows[1]?.classes()).toContain('job-detail-row');
		expect(bodyRows[1]?.find('[data-testid="inline-details"]').exists()).toBe(true);
		expect(wrapper.get(`button[aria-controls="job-details-${completedJob.id}"]`).attributes('aria-expanded')).toBe('true');
		expect(scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'start', inline: 'nearest' });
	});

	it('keeps deep-linked details visible when the selected job is outside the current page', () => {
		const wrapper = mount(JobTable, {
			props: { rows: [], selectedId: 'job-on-another-page' },
			slots: { details: '<div data-testid="inline-details">Deep-linked details</div>' },
		});

		expect(wrapper.find('.job-detail-row [data-testid="inline-details"]').exists()).toBe(true);
	});

	it('renders segmented batch progress and lifecycle actions', () => {
		const wrapper = mount(BatchTable, { props: { rows: [executingBatch] } });
		expect(wrapper.text()).toContain('campaign-batch');
		expect(wrapper.text()).toContain('8 / 10');
		expect(wrapper.find('[data-state="succeeded"]').attributes('style')).toContain('width: 60%');
		expect(wrapper.find('button[aria-label="Cancel batch campaign-batch"]').exists()).toBe(true);
	});

	it('renders accessible exact chart values', () => {
		const wrapper = mount(HistoryChart, {
			props: {
				points: [
					{ capturedAt: '2026-07-21T12:00:00Z', complete: 0, queued: 3, throughput: 0 },
					{ capturedAt: '2026-07-21T12:00:05Z', complete: 0, queued: 17, throughput: 0 },
				],
				valueKey: 'queued',
				label: 'Queue depth',
			},
		});
		expect(wrapper.find('svg').attributes('aria-label')).toBe('Recent queue depth history');
		expect(wrapper.find('button[aria-label^="Queue depth: 17 at"]').exists()).toBe(true);
		expect(wrapper.find('.chart-line').exists()).toBe(true);
	});

	it('lays out fan-out and join dependencies and follows active work', async () => {
		vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
		const scrollTo = vi.spyOn(HTMLElement.prototype, 'scrollTo');
		const wrapper = mount(WorkflowGraph, { props: { graph: workflowGraph } });
		await flushPromises();
		expect(wrapper.find('svg').attributes('aria-label')).toBe('Batch dependency graph');
		expect(wrapper.text()).toContain('order-record-fraud-assessment');
		expect(wrapper.findAll('.workflow-edge.dashed')).toHaveLength(2);
		expect(wrapper.findAll('.workflow-join')).toHaveLength(1);
		const longNodeWidth = Number(wrapper.find('[data-job-id="smoke"] rect').attributes('width'));
		expect(longNodeWidth).toBeGreaterThan(170);
		const paths = wrapper.findAll('.workflow-edge').map((edge) => edge.attributes('d'));
		expect(paths[2]).not.toBe(paths[3]);
		expect(scrollTo).toHaveBeenCalled();
	});

	it('simplifies additive splice constraints and can reveal the persisted edges', async () => {
		const graph = {
			batchId: 'dynamic-workflow',
			nodes: [
				{ jobId: 'fraud-check', jobName: 'order-fraud-check', state: 'Succeeded' as const },
				{ jobId: 'assessment', jobName: 'order-record-fraud-assessment', state: 'Succeeded' as const },
				{ jobId: 'fulfillment', jobName: 'order-prepare-fulfillment', state: 'Succeeded' as const },
			],
			edges: [
				{ childJobId: 'assessment', parentJobId: 'fraud-check', parentBatchId: null, trigger: 'AllSucceeded' as const },
				{ childJobId: 'fulfillment', parentJobId: 'fraud-check', parentBatchId: null, trigger: 'AllSucceeded' as const },
				{ childJobId: 'fulfillment', parentJobId: 'assessment', parentBatchId: null, trigger: 'AllSucceeded' as const },
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });

		expect(wrapper.findAll('.workflow-edge')).toHaveLength(2);
		expect(wrapper.find('[data-parent-job-id="fraud-check"][data-child-job-id="fulfillment"]').exists()).toBe(false);
		expect(wrapper.text()).toContain('1 transitive constraint simplified');

		await wrapper.get('.workflow-toggle').trigger('click');
		expect(wrapper.findAll('.workflow-edge')).toHaveLength(3);
		expect(wrapper.find('[data-parent-job-id="fraud-check"][data-child-job-id="fulfillment"]').exists()).toBe(true);
		expect(wrapper.get('.workflow-toggle').attributes('aria-pressed')).toBe('true');
	});

	it('keeps a success constraint when its alternate path allows failed parents', () => {
		const graph = {
			batchId: 'mixed-triggers',
			nodes: [
				{ jobId: 'current', jobName: 'Current', state: 'Failed' as const },
				{ jobId: 'inserted', jobName: 'Inserted', state: 'Succeeded' as const },
				{ jobId: 'waiter', jobName: 'Waiter', state: 'Cancelled' as const },
			],
			edges: [
				{ childJobId: 'inserted', parentJobId: 'current', parentBatchId: null, trigger: 'AllComplete' as const },
				{ childJobId: 'waiter', parentJobId: 'current', parentBatchId: null, trigger: 'AllSucceeded' as const },
				{ childJobId: 'waiter', parentJobId: 'inserted', parentBatchId: null, trigger: 'AllSucceeded' as const },
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });

		expect(wrapper.findAll('.workflow-edge')).toHaveLength(3);
		expect(wrapper.find('.workflow-toolbar').exists()).toBe(false);
	});
});
