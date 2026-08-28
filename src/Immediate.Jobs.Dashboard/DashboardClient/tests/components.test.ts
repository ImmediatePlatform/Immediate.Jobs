import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getJobExecutions, getJobExecutionTelemetryLinks } from '@/api';
import BatchTable from '@/components/BatchTable.vue';
import HistoryChart from '@/components/HistoryChart.vue';
import JobDetail from '@/components/JobDetail.vue';
import JobTable from '@/components/JobTable.vue';
import MetricCard from '@/components/MetricCard.vue';
import WorkflowGraph from '@/components/WorkflowGraph.vue';
import type { BatchGraph } from '@/contracts';
import { completedJob, executingBatch, workflowGraph } from './fixtures';

const dashboardStyles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8');

vi.mock('@/api', async importOriginal => ({
	...await importOriginal<typeof import('@/api')>(),
	getJobExecutions: vi.fn(),
	getJobExecutionTelemetryLinks: vi.fn(),
}));

const getJobExecutionsMock = vi.mocked(getJobExecutions);
const getJobExecutionTelemetryLinksMock = vi.mocked(getJobExecutionTelemetryLinks);

describe('dashboard components', () => {
	beforeEach(() => {
		getJobExecutionsMock.mockReset().mockResolvedValue({ items: [], skip: 0, take: 20, hasNext: false });
		getJobExecutionTelemetryLinksMock.mockReset().mockResolvedValue([]);
	});

	it('renders multi-word metric labels as readable text', () => {
		const wrapper = mount(MetricCard, {
			props: { label: 'AwaitingContinuation', value: 3 },
		});

		expect(wrapper.get('.metric-label').text()).toBe('Awaiting continuation');
		expect(dashboardStyles).not.toMatch(/\.metric-label\s*\{[^}]*text-transform:\s*uppercase/s);
	});

	it('renders job rows, retained executions, and complete payload/context details', async () => {
		const attemptTraceId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
		const attemptSpanId = 'bbbbbbbbbbbbbbbb';
		const table = mount(JobTable, { props: { rows: [completedJob] } });
		expect(table.text()).toContain('SendGreeting');
		expect(table.text()).toContain('Succeeded');
		expect(table.text()).toContain('batch-42');
		expect(table.text()).toContain('tenant-a');

		getJobExecutionsMock.mockResolvedValue({
			items: [{
				jobHandle: completedJob.jobHandle,
				attempt: completedJob.attempt,
				state: 'Succeeded',
				workerId: 'worker-1',
				acquiredAt: '2026-07-21T12:01:00Z',
				executionStartedAt: completedJob.executionStartedAt,
				completedAt: completedJob.completedAt,
				executionTraceId: attemptTraceId,
				executionSpanId: attemptSpanId,
				error: null,
				isSynthetic: false,
			}],
			skip: 0,
			take: 20,
			hasNext: false,
		});
		getJobExecutionTelemetryLinksMock.mockResolvedValue([
			{ label: 'View execution trace', kind: 'Trace', url: `https://telemetry.example/traces/${attemptTraceId}` },
		]);
		const detail = mount(JobDetail, {
			props: {
				job: completedJob,
				telemetryLinks: [
					{ label: 'View trace', kind: 'Trace', url: 'https://telemetry.example/traces/4bf92f' },
					{ label: 'View all retry logs', kind: 'Logs', url: 'https://telemetry.example/logs?job=86bf8c31' },
				],
			},
		});
		await flushPromises();
		expect(detail.attributes('aria-label')).toBe('Details for SendGreeting');
		expect(detail.text()).toContain('Payload');
		expect(detail.text()).toContain('Duke');
		expect(detail.text()).toContain('Context envelope');
		expect(detail.text()).toContain('curl/8.7.1');
		expect(detail.text()).toContain('Group');
		expect(detail.text()).toContain('tenant-a');
		expect(detail.text()).toContain('Trace ID');
		expect(detail.text()).toContain(attemptTraceId);
		expect(detail.text()).toContain('Span ID');
		expect(detail.text()).toContain(attemptSpanId);
		expect(detail.text()).not.toContain(completedJob.executionTraceId);
		expect(getJobExecutionTelemetryLinksMock).toHaveBeenCalledWith(completedJob.jobHandle, completedJob.attempt, expect.any(AbortSignal));
		expect(detail.get('a[href="https://telemetry.example/traces/4bf92f"]').attributes('target')).toBe('_blank');
		expect(detail.get(`a[href="https://telemetry.example/traces/${attemptTraceId}"]`).attributes('target')).toBe('_blank');
		expect(detail.get('a[aria-label="View all retry logs"]').text()).toContain('retry logs');
		expect(detail.text()).not.toContain('Observability');
	});

	it('copies an execution trace ID from the property grid', async () => {
		const traceId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
		const writeText = vi.fn().mockResolvedValue(undefined);
		getJobExecutionsMock.mockResolvedValue({
			items: [{
				jobHandle: completedJob.jobHandle,
				attempt: 3,
				state: 'Succeeded',
				workerId: 'worker-1',
				acquiredAt: '2026-07-21T12:01:00Z',
				executionStartedAt: '2026-07-21T12:01:01Z',
				completedAt: '2026-07-21T12:01:02Z',
				executionTraceId: traceId,
				executionSpanId: 'bbbbbbbbbbbbbbbb',
				error: null,
				isSynthetic: false,
			}],
			skip: 0,
			take: 20,
			hasNext: false,
		});

		const detail = mount(JobDetail, { props: { job: completedJob } });
		await flushPromises();
		vi.stubGlobal('navigator', { clipboard: { writeText } });
		const copyTrace = detail.get('button[aria-label="Copy trace ID for attempt 3"]');

		await copyTrace.trigger('click');
		await flushPromises();

		expect(writeText).toHaveBeenCalledWith(traceId);
		expect(copyTrace.attributes('aria-label')).toBe('Copied trace ID for attempt 3');
	});

	it('expands the newest execution and collapses older executions by default', async () => {
		const execution = {
			jobHandle: completedJob.jobHandle,
			state: 'Failed' as const,
			workerId: 'worker-1',
			acquiredAt: '2026-07-21T12:01:00Z',
			executionStartedAt: '2026-07-21T12:01:01Z',
			completedAt: '2026-07-21T12:01:02Z',
			executionTraceId: null,
			executionSpanId: null,
			error: 'failed',
			isSynthetic: false,
		};
		getJobExecutionsMock.mockResolvedValue({
			items: [
				{ ...execution, attempt: 3, state: 'Succeeded', error: null },
				{ ...execution, attempt: 2 },
				{ ...execution, attempt: 1 },
			],
			skip: 0,
			take: 20,
			hasNext: false,
		});

		const detail = mount(JobDetail, { props: { job: completedJob } });
		await flushPromises();
		const cards = detail.findAll('details.execution-card');

		expect(cards).toHaveLength(3);
		expect(cards[0]?.attributes('open')).toBeDefined();
		expect(cards[1]?.attributes('open')).toBeUndefined();
		expect(cards[2]?.attributes('open')).toBeUndefined();
		expect(cards[1]?.get('summary').text()).toContain('Attempt 2');
		expect(cards[1]?.get('summary').text()).toContain('Failed');
	});

	it('preserves loaded executions when the cached job object is replaced', async () => {
		const execution = {
			jobHandle: completedJob.jobHandle,
			state: 'Succeeded' as const,
			workerId: 'worker-1',
			acquiredAt: '2026-07-21T12:01:00Z',
			executionStartedAt: '2026-07-21T12:01:01Z',
			completedAt: '2026-07-21T12:01:02Z',
			executionTraceId: null,
			executionSpanId: null,
			error: null,
			isSynthetic: false,
		};
		getJobExecutionsMock
			.mockResolvedValueOnce({
				items: [{ ...execution, attempt: 3 }],
				skip: 0,
				take: 20,
				hasNext: true,
			})
			.mockResolvedValueOnce({
				items: [{ ...execution, attempt: 2 }],
				skip: 1,
				take: 20,
				hasNext: false,
			})
			.mockResolvedValueOnce({
				items: [{ ...execution, attempt: 4 }],
				skip: 0,
				take: 20,
				hasNext: false,
			});

		const detail = mount(JobDetail, { props: { job: completedJob } });
		await flushPromises();
		await detail.get('.execution-history > button').trigger('click');
		await flushPromises();

		await detail.setProps({ job: { ...completedJob, lastError: 'cache refresh' } });
		await flushPromises();
		expect(getJobExecutionsMock).toHaveBeenCalledTimes(2);
		expect(detail.findAll('details.execution-card')).toHaveLength(2);

		await detail.setProps({ job: { ...completedJob, attempt: completedJob.attempt + 1 } });
		await flushPromises();
		expect(getJobExecutionsMock).toHaveBeenCalledTimes(3);
		expect(detail.findAll('details.execution-card')).toHaveLength(1);
	});

	it('keeps execution history visible when a telemetry link fails', async () => {
		getJobExecutionsMock.mockResolvedValue({
			items: [{
				jobHandle: completedJob.jobHandle,
				attempt: completedJob.attempt,
				state: 'Succeeded',
				workerId: 'worker-1',
				acquiredAt: '2026-07-21T12:01:00Z',
				executionStartedAt: completedJob.executionStartedAt,
				completedAt: completedJob.completedAt,
				executionTraceId: null,
				executionSpanId: null,
				error: null,
				isSynthetic: false,
			}],
			skip: 0,
			take: 20,
			hasNext: false,
		});
		getJobExecutionTelemetryLinksMock.mockRejectedValue(new Error('telemetry unavailable'));

		const detail = mount(JobDetail, { props: { job: completedJob } });
		await flushPromises();

		expect(detail.text()).toContain(`Attempt ${completedJob.attempt}`);
		expect(detail.text()).not.toContain('telemetry unavailable');
	});

	it('can fast-forward scheduled jobs', async () => {
		const scheduled = {
			...completedJob,
			jobHandle: 'scheduled-retry',
			jobName: 'retry-test',
			state: 'Scheduled' as const,
			attempt: 1,
			completedAt: null,
		};
		const firstRun = { ...scheduled, jobHandle: 'scheduled-first-run', jobName: 'first-run', attempt: 0 };
		const table = mount(JobTable, { props: { rows: [scheduled, firstRun] } });

		expect(table.find('button[aria-label="Run first-run now"]').exists()).toBe(true);
		const runNow = table.get('button[aria-label="Run retry-test now"]');
		await runNow.trigger('click');
		expect(table.emitted('retry')?.[0]).toEqual([scheduled]);
		const cancel = table.get('button[aria-label="Cancel retry-test"]');
		await cancel.trigger('click');
		expect(table.emitted('cancel')?.[0]).toEqual([scheduled]);

		const detail = mount(JobDetail, { props: { job: scheduled } });
		expect(detail.findAll('button.button-secondary').some(button => button.text().includes('Run now'))).toBe(true);
		const cancelDetail = detail.findAll('button.button-secondary').find(button => button.text().includes('Cancel job'));
		expect(cancelDetail).toBeDefined();
		await cancelDetail?.trigger('click');
		expect(detail.emitted('cancel')?.[0]).toEqual([scheduled]);
	});

	it('shows skipped branches', () => {
		const skipped = { ...completedJob, jobHandle: 'skipped', state: 'Skipped' as const };
		const table = mount(JobTable, { props: { rows: [skipped] } });

		expect(table.text()).toContain('Skipped');
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

	it('keeps one table row per job and emits navigation without inserting details', async () => {
		const wrapper = mount(JobTable, { props: { rows: [completedJob] } });

		await wrapper.get('button[aria-label="View SendGreeting"]').trigger('click');

		expect(wrapper.findAll('tbody tr')).toHaveLength(1);
		expect(wrapper.find('.job-detail-row').exists()).toBe(false);
		expect(wrapper.emitted('select')?.[0]).toEqual([completedJob]);
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
			batchHandle: 'release',
			nodes: [
				{ jobHandle: 'approved', jobName: 'Approved', state: 'Succeeded' },
				{ jobHandle: 'client', jobName: 'Build client', state: 'Succeeded' },
				{ jobHandle: 'services', jobName: 'Provision services', state: 'Succeeded' },
				{ jobHandle: 'compatibility', jobName: 'Test compatibility', state: 'Succeeded' },
				{ jobHandle: 'signing', jobName: 'Sign binaries', state: 'Succeeded' },
				{ jobHandle: 'migration', jobName: 'Migrate data', state: 'Succeeded' },
				{ jobHandle: 'load-test', jobName: 'Load-test services', state: 'Succeeded' },
				{ jobHandle: 'client-ready', jobName: 'Certify client', state: 'Succeeded' },
				{ jobHandle: 'services-ready', jobName: 'Certify services', state: 'Succeeded' },
				{ jobHandle: 'candidate', jobName: 'Assemble candidate', state: 'Succeeded' },
			],
			edges: [
				{ childJobHandle: 'client', parentJobHandle: 'approved', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'services', parentJobHandle: 'approved', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'compatibility', parentJobHandle: 'client', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'signing', parentJobHandle: 'client', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'migration', parentJobHandle: 'services', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'load-test', parentJobHandle: 'services', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'client-ready', parentJobHandle: 'compatibility', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'client-ready', parentJobHandle: 'signing', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'services-ready', parentJobHandle: 'migration', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'services-ready', parentJobHandle: 'load-test', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'candidate', parentJobHandle: 'client-ready', parentBatchHandle: null, trigger: 'Success' },
				{ childJobHandle: 'candidate', parentJobHandle: 'services-ready', parentBatchHandle: null, trigger: 'Success' },
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });
		const nodeY = (jobHandle: string): number => {
			const transform = wrapper.get(`[data-job-id="${jobHandle}"]`).attributes('transform');
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
			batchHandle: 'dynamic-workflow',
			nodes: [
				{ jobHandle: 'fraud-check', jobName: 'order-fraud-check', state: 'Succeeded' as const },
				{ jobHandle: 'assessment', jobName: 'order-record-fraud-assessment', state: 'Succeeded' as const },
				{ jobHandle: 'fulfillment', jobName: 'order-prepare-fulfillment', state: 'Succeeded' as const },
			],
			edges: [
				{ childJobHandle: 'assessment', parentJobHandle: 'fraud-check', parentBatchHandle: null, trigger: 'Success' as const },
				{ childJobHandle: 'fulfillment', parentJobHandle: 'fraud-check', parentBatchHandle: null, trigger: 'Success' as const },
				{ childJobHandle: 'fulfillment', parentJobHandle: 'assessment', parentBatchHandle: null, trigger: 'Success' as const },
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
			batchHandle: 'mixed-triggers',
			nodes: [
				{ jobHandle: 'current', jobName: 'Current', state: 'Failed' as const },
				{ jobHandle: 'inserted', jobName: 'Inserted', state: 'Succeeded' as const },
				{ jobHandle: 'waiter', jobName: 'Waiter', state: 'Cancelled' as const },
			],
			edges: [
				{ childJobHandle: 'inserted', parentJobHandle: 'current', parentBatchHandle: null, trigger: 'Complete' as const },
				{ childJobHandle: 'waiter', parentJobHandle: 'current', parentBatchHandle: null, trigger: 'Success' as const },
				{ childJobHandle: 'waiter', parentJobHandle: 'inserted', parentBatchHandle: null, trigger: 'Success' as const },
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });

		expect(wrapper.findAll('.workflow-edge')).toHaveLength(3);
		expect(wrapper.find('.workflow-toolbar').exists()).toBe(false);
	});
});
