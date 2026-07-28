import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';

import BatchTable from '@/components/BatchTable.vue';
import HistoryChart from '@/components/HistoryChart.vue';
import JobDetail from '@/components/JobDetail.vue';
import JobTable from '@/components/JobTable.vue';
import WorkflowGraph from '@/components/WorkflowGraph.vue';
import type { BatchGraph } from '@/contracts';
import { completedJob, executingBatch, workflowGraph } from './fixtures';

describe('dashboard components', () => {
	it('renders job rows and complete payload/context details', () => {
		const table = mount(JobTable, { props: { rows: [completedJob] } });
		expect(table.text()).toContain('SendGreeting');
		expect(table.text()).toContain('Succeeded');
		expect(table.text()).toContain('batch-42');
		expect(table.text()).toContain('tenant-a');

		const detail = mount(JobDetail, {
			props: {
				job: completedJob,
				telemetryLinks: [
					{ label: 'View trace', kind: 'Trace', url: 'https://telemetry.example/traces/4bf92f' },
					{ label: 'View all retry logs', kind: 'Logs', url: 'https://telemetry.example/logs?job=86bf8c31' },
				],
			},
		});
		expect(detail.attributes('aria-label')).toBe('Details for SendGreeting');
		expect(detail.text()).toContain('Payload');
		expect(detail.text()).toContain('Duke');
		expect(detail.text()).toContain('Context envelope');
		expect(detail.text()).toContain('curl/8.7.1');
		expect(detail.text()).toContain('Group');
		expect(detail.text()).toContain('tenant-a');
		expect(detail.text()).toContain('4bf92f3577b34da6a3ce929d0e0e4736');
		expect(detail.get('a[href="https://telemetry.example/traces/4bf92f"]').attributes('target')).toBe('_blank');
		expect(detail.get('a[aria-label="View all retry logs"]').text()).toContain('Logs');
		expect(detail.text()).not.toContain('Observability');
	});

	it.each([
		{ groupId: null, rendersGroup: false },
		{ groupId: '', rendersGroup: true },
	])('renders job group details according to the nullable contract for $groupId', ({ groupId, rendersGroup }) => {
		const detail = mount(JobDetail, {
			props: {
				job: { ...completedJob, groupId },
			},
		});

		expect(detail.findAll('dt').some((term) => term.text() === 'Group')).toBe(rendersGroup);
	});

	it.each([
		{ groupId: null, rendersGroup: false },
		{ groupId: '', rendersGroup: true },
	])('renders job table groups according to the nullable contract for $groupId', ({ groupId, rendersGroup }) => {
		const table = mount(JobTable, {
			props: {
				rows: [{ ...completedJob, groupId }],
			},
		});
		const groupCell = table.get('tbody tr[data-job-id] td:nth-child(3)');

		expect(groupCell.find('code').exists()).toBe(rendersGroup);
		expect(groupCell.find('.text-muted').exists()).toBe(!rendersGroup);
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
		expect(wrapper.findAll('.workflow-fork')).toHaveLength(1);
		expect(wrapper.findAll('.workflow-join')).toHaveLength(1);
		const longNodeWidth = Number(wrapper.find('[data-job-id="smoke"] rect').attributes('width'));
		expect(longNodeWidth).toBeGreaterThan(170);
		const paths = wrapper.findAll('.workflow-edge').map((edge) => edge.attributes('d'));
		expect(paths[2]).not.toBe(paths[3]);
		expect(scrollTo).toHaveBeenCalled();
	});

	it('orders connected workstreams together and vertically centers shorter ranks', () => {
		const graph: BatchGraph = {
			batchId: 'release',
			nodes: [
				{ jobId: 'approved', jobName: 'Approved', state: 'Succeeded' },
				{ jobId: 'client', jobName: 'Build client', state: 'Succeeded' },
				{ jobId: 'services', jobName: 'Provision services', state: 'Succeeded' },
				{ jobId: 'compatibility', jobName: 'Test compatibility', state: 'Succeeded' },
				{ jobId: 'signing', jobName: 'Sign binaries', state: 'Succeeded' },
				{ jobId: 'migration', jobName: 'Migrate data', state: 'Succeeded' },
				{ jobId: 'load-test', jobName: 'Load-test services', state: 'Succeeded' },
				{ jobId: 'client-ready', jobName: 'Certify client', state: 'Succeeded' },
				{ jobId: 'services-ready', jobName: 'Certify services', state: 'Succeeded' },
				{ jobId: 'candidate', jobName: 'Assemble candidate', state: 'Succeeded' },
			],
			edges: [
				{ childJobId: 'client', parentJobId: 'approved', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'services', parentJobId: 'approved', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'compatibility', parentJobId: 'client', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'signing', parentJobId: 'client', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'migration', parentJobId: 'services', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'load-test', parentJobId: 'services', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'client-ready', parentJobId: 'compatibility', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'client-ready', parentJobId: 'signing', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'services-ready', parentJobId: 'migration', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'services-ready', parentJobId: 'load-test', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'candidate', parentJobId: 'client-ready', parentBatchId: null, trigger: 'Success' },
				{ childJobId: 'candidate', parentJobId: 'services-ready', parentBatchId: null, trigger: 'Success' },
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });
		const nodeY = (jobId: string): number => {
			const transform = wrapper.get(`[data-job-id="${jobId}"]`).attributes('transform');
			return Number(/translate\([^ ]+ ([^)]+)\)/.exec(transform)?.[1]);
		};

		const approvedY = nodeY('approved');
		expect(approvedY).toBeGreaterThan(nodeY('client'));
		expect(approvedY).toBeLessThan(nodeY('services'));
		expect(Math.max(nodeY('compatibility'), nodeY('signing')))
			.toBeLessThan(Math.min(nodeY('migration'), nodeY('load-test')));
		expect(nodeY('candidate')).toBe(approvedY);

		const clientWidth = wrapper.get('[data-job-id="client"] rect').attributes('width');
		const servicesWidth = wrapper.get('[data-job-id="services"] rect').attributes('width');
		expect(clientWidth).toBe(servicesWidth);
		const edgePaths = wrapper.findAll('.workflow-edge').map((edge) => edge.attributes('d'));
		expect(edgePaths.some((path) => /\bQ\b.*\bV\b.*\bQ\b/.test(path))).toBe(true);
		for (const path of edgePaths) {
			expect(path).not.toContain('C');
		}

		expect(wrapper.findAll('.workflow-fork')).toHaveLength(3);
		const approvedBranches = wrapper.findAll(
			'.workflow-edge[data-parent-job-id="approved"]',
		).map((edge) => edge.attributes('d').match(/^M [^ ]+ [^ ]+/)?.[0]);
		expect(new Set(approvedBranches).size).toBe(1);

		const candidateInputs = wrapper.findAll(
			'.workflow-edge[data-child-job-id="candidate"]',
		).map((edge) => edge.attributes('d').match(/\bQ ([^ ]+)/)?.[1]);
		expect(new Set(candidateInputs).size).toBe(1);
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
				{ childJobId: 'assessment', parentJobId: 'fraud-check', parentBatchId: null, trigger: 'Success' as const },
				{ childJobId: 'fulfillment', parentJobId: 'fraud-check', parentBatchId: null, trigger: 'Success' as const },
				{ childJobId: 'fulfillment', parentJobId: 'assessment', parentBatchId: null, trigger: 'Success' as const },
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
				{ childJobId: 'inserted', parentJobId: 'current', parentBatchId: null, trigger: 'Complete' as const },
				{ childJobId: 'waiter', parentJobId: 'current', parentBatchId: null, trigger: 'Success' as const },
				{ childJobId: 'waiter', parentJobId: 'inserted', parentBatchId: null, trigger: 'Success' as const },
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });

		expect(wrapper.findAll('.workflow-edge')).toHaveLength(3);
		expect(wrapper.find('.workflow-toolbar').exists()).toBe(false);
	});
});
