import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getJobExecutions, getJobExecutionTelemetryLinks } from '@/api';
import BatchTable from '@/components/BatchTable.vue';
import DefinitionTable from '@/components/DefinitionTable.vue';
import HistoryChart from '@/components/HistoryChart.vue';
import JobDetail from '@/components/JobDetail.vue';
import JobStateSummary from '@/components/JobStateSummary.vue';
import JobTable from '@/components/JobTable.vue';
import ServerCard from '@/components/ServerCard.vue';
import WorkflowGraph from '@/components/WorkflowGraph.vue';
import type { BatchGraph, JobServerSnapshot } from '@/contracts';
import { routes } from '@/router';
import { completedJob, executingBatch, pausedDefinition, storedDefinition, workflowGraph } from './fixtures';

const dashboardStyles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8');

vi.mock('@/api', async importOriginal => ({
	...await importOriginal<typeof import('@/api')>(),
	getJobExecutions: vi.fn(),
	getJobExecutionTelemetryLinks: vi.fn(),
}));

const getJobExecutionsMock = vi.mocked(getJobExecutions);
const getJobExecutionTelemetryLinksMock = vi.mocked(getJobExecutionTelemetryLinks);

function mountJobDetail(props: InstanceType<typeof JobDetail>['$props']) {
	const router = createRouter({ history: createMemoryHistory(), routes });
	return mount(JobDetail, { props, global: { plugins: [router] } });
}

describe('dashboard components', () => {
	it('renders persisted definition metadata and job links', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		const wrapper = mount(DefinitionTable, { props: { definitions: [storedDefinition] }, global: { plugins: [router] } });
		expect(wrapper.text()).toContain('RemoteEmail');
		expect(wrapper.text()).toContain('priority');
		expect(wrapper.findAll('.tag').map(tag => tag.text())).toEqual(['email', 'default']);
		expect(wrapper.text()).toContain('15 * * * *');
		await wrapper.get('a').trigger('click');
		await flushPromises();
		expect(router.currentRoute.value.query).toEqual({ search: 'RemoteEmail', queue: 'priority' });
	});

	it('shows the definitions empty state', () => {
		const wrapper = mount(DefinitionTable, { props: { definitions: [] } });
		expect(wrapper.text()).toContain('No job definitions');
	});

	beforeEach(() => {
		getJobExecutionsMock.mockReset().mockResolvedValue({ items: [], skip: 0, take: 20, hasNext: false });
		getJobExecutionTelemetryLinksMock.mockReset().mockResolvedValue([]);
	});

	it('shows definition pause and concurrency independently and distinguishes unbounded limits', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		const wrapper = mount(DefinitionTable, {
			props: { definitions: [pausedDefinition, {
				...storedDefinition,
				name: 'Unbounded', queueName: 'default',
				limits: { slidingWindowMax: 0, slidingWindowPeriod: null, fixedWindowMax: 0, fixedWindowPeriod: null, maxConcurrency: 0 },
				acquisition: { jobName: 'Unbounded', isPaused: false, acquisitionStatus: 'Ready', nextEligibleAt: null, activeCount: 3, isConcurrencyLimited: false },
			}] },
			global: { plugins: [router] },
		});
		await router.isReady();
		const rows = wrapper.findAll('tbody tr');
		expect(rows[0]?.text()).toContain('Paused');
		expect(rows[0]?.text()).toContain('2 / 2 active');
		expect(rows[0]?.text()).toContain('At capacity');
		expect(rows[0]?.text()).toContain('10 / 00:01:00');
		expect(rows[1]?.text()).toContain('3 active · no limit');
		expect(rows[1]?.text()).not.toContain('At capacity');
		expect(rows[1]?.text()).toContain('Ready');
		expect(rows[0]?.get('a').attributes('href')).toBe('/invocations?search=SendGreeting&queue=default');
	});

	it('groups job state counts by lifecycle stage and links to filtered jobs', async () => {
		const router = createRouter({ history: createMemoryHistory(), routes });
		const wrapper = mount(JobStateSummary, {
			props: { counts: { Pending: 2, Active: 1, AwaitingContinuation: 3, Succeeded: 5, Failed: 1 } },
			global: { plugins: [router] },
		});
		await router.isReady();

		const groups = wrapper.findAll('.state-group');
		expect(groups.map(group => group.get('.eyebrow').text())).toEqual(['Queue', 'Waiting', 'Finished']);
		expect(groups.map(group => group.get('header strong').text())).toEqual(['3', '3', '6']);

		const awaiting = wrapper.get('.state-row[data-state="awaitingcontinuation"]');
		expect(awaiting.get('.state-row-label').text()).toBe('Awaiting continuation');
		expect(awaiting.attributes('href')).toBe('/invocations?state=AwaitingContinuation');
		expect(awaiting.classes()).not.toContain('empty');
		expect(wrapper.get('.state-row[data-state="skipped"]').classes()).toContain('empty');
		expect(dashboardStyles).not.toMatch(/\.state-row-label\s*\{[^}]*text-transform:\s*uppercase/s);
	});

	it('links a batched job to its batch unless the batch is already shown', async () => {
		const detail = mountJobDetail({ job: completedJob });
		await flushPromises();
		const batchLink = detail.get('a[aria-label="Open batch batch-42"]');
		expect(batchLink.attributes('href')).toBe('/batches/batch-42');

		const inBatch = mountJobDetail({ job: completedJob, showBatchLink: false });
		await flushPromises();
		expect(inBatch.find('a[aria-label="Open batch batch-42"]').exists()).toBe(false);
		expect(inBatch.text()).toContain('batch-42');
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
		const detail = mountJobDetail({
			job: completedJob,
			telemetryLinks: [
				{ label: 'View trace', kind: 'Trace', url: 'https://telemetry.example/traces/4bf92f' },
				{ label: 'View all retry logs', kind: 'Logs', url: 'https://telemetry.example/logs?job=86bf8c31' },
			],
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

		const detail = mountJobDetail({ job: completedJob });
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

		const detail = mountJobDetail({ job: completedJob });
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

		const detail = mountJobDetail({ job: completedJob });
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

		const detail = mountJobDetail({ job: completedJob });
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

		const detail = mountJobDetail({ job: scheduled });
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

	it('shows a placeholder for a waiting job without parameters', () => {
		const waiting = { ...completedJob, jobHandle: 'waiting', state: 'WaitingForTrigger' as const, payload: '' };
		const detail = mountJobDetail({ job: waiting });

		const payload = detail.findAll('.code-section').find(section => section.find('h3').text() === 'Payload');
		expect(payload?.text()).toContain('Parameters not supplied');
		expect(payload?.find('pre').exists()).toBe(false);
	});

	it.each([
		{ groupId: null, rendersGroup: false },
		{ groupId: '', rendersGroup: true },
	])('renders job group details according to the nullable contract for $groupId', ({ groupId, rendersGroup }) => {
		const detail = mountJobDetail({ job: { ...completedJob, groupId } });

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

	it('renders a slot per worker and lists only busy workers', () => {
		const idleLoop = {
			isRunning: false,
			lastAttemptedAt: null,
			lastSucceededAt: null,
			lastFailedAt: null,
			consecutiveFailures: 0,
			itemsExamined: 0,
			itemsSucceeded: 0,
			itemsFailed: 0,
		};
		const workers = Array.from({ length: 32 }, (_, workerId) => ({ workerId, jobHandle: null, attempt: null, startedAt: null }))
			.reverse();
		const server: JobServerSnapshot = {
			tags: ['default'],
			workerId: 'node-1',
			lastHeartbeat: '2026-07-21T12:00:00Z',
			activeWorkers: 2,
			maxWorkers: 32,
			serverTimeout: '00:02:00',
			workers: workers.map(worker => worker.workerId === 3
				? { ...worker, jobHandle: 'redis:jobs:opaque', attempt: 2, startedAt: '2026-07-21T12:00:05Z' }
				: worker.workerId === 17
					? { ...worker, jobHandle: 'job-17', attempt: 1, startedAt: '2026-07-21T12:00:07Z' }
					: worker),
			acquisition: idleLoop,
			leaseRenewal: idleLoop,
		};
		const router = createRouter({ history: createMemoryHistory(), routes });

		const wrapper = mount(ServerCard, { props: { server }, global: { plugins: [router] } });
		expect(wrapper.get('[aria-label="Server tags"]').text()).toBe('default');
		const slots = wrapper.findAll('.server-slots .server-slot');
		const busyRows = wrapper.findAll('.server-workers li');

		expect(slots).toHaveLength(32);
		expect(slots[0].attributes('aria-label')).toBe('Worker #0: idle');
		expect(slots[3].classes()).toContain('busy');
		expect(slots[3].attributes('href')).toBe('/invocations/redis:jobs:opaque');
		expect(wrapper.findAll('.server-slot.busy')).toHaveLength(2);

		expect(busyRows).toHaveLength(2);
		expect(busyRows[0].text()).toContain('#3');
		expect(busyRows[0].text()).toContain('Attempt 2');
		expect(busyRows[0].get('a').attributes('href')).toBe('/invocations/redis:jobs:opaque');
		expect(busyRows[1].text()).toContain('#17');
		expect(wrapper.text()).not.toContain('All workers idle');

		const idleWrapper = mount(ServerCard, {
			props: { server: { ...server, activeWorkers: 0, workers } },
			global: { plugins: [router] },
		});
		expect(idleWrapper.find('.server-workers').exists()).toBe(false);
		expect(idleWrapper.text()).toContain('All workers idle');
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

	it('stacks interchangeable jobs and expands them in pages', async () => {
		const items = Array.from({ length: 25 }, (_, index) => ({
			jobHandle: `item-${index}`,
			jobName: 'process-item',
			state: index < 3 ? 'Succeeded' as const : index === 3 ? 'Active' as const : 'Pending' as const,
		}));
		const graph: BatchGraph = {
			batchHandle: 'fan-out',
			nodes: [
				{ jobHandle: 'split', jobName: 'split', state: 'Succeeded' },
				...items,
				{ jobHandle: 'report', jobName: 'report', state: 'AwaitingContinuation' },
				{ jobHandle: 'audit-a', jobName: 'audit', state: 'Pending' },
				{ jobHandle: 'audit-b', jobName: 'audit', state: 'Pending' },
			],
			edges: [
				...items.flatMap(item => [
					{ childJobHandle: item.jobHandle, parentJobHandle: 'split', parentBatchHandle: null, trigger: 'Success' as const },
					{ childJobHandle: 'report', parentJobHandle: item.jobHandle, parentBatchHandle: null, trigger: 'Success' as const },
				]),
			],
		};
		const wrapper = mount(WorkflowGraph, { props: { graph } });

		const stack = wrapper.get('.workflow-node.stack');
		expect(wrapper.findAll('.workflow-node')).toHaveLength(5);
		expect(stack.text()).toContain('25 jobs');
		expect(stack.text()).toContain('1 active · 21 pending · 3 succeeded');
		expect(stack.classes()).toContain('active');
		expect(wrapper.findAll('.workflow-edge')).toHaveLength(2);
		expect(wrapper.findAll('[data-job-id^="audit-"]')).toHaveLength(2);

		await stack.trigger('click');
		expect(wrapper.find('.workflow-node.stack').exists()).toBe(false);
		expect(wrapper.findAll('[data-job-id^="item-"]')).toHaveLength(20);
		expect(wrapper.get('.workflow-node.more').text()).toContain('5 more process-item');
		expect(wrapper.get('.workflow-group-chip').text()).toContain('process-item · 25 jobs');
		const nodeY = (selector: string): number =>
			Number(/translate\([^ ]+ ([^)]+)\)/.exec(wrapper.get(selector).attributes('transform'))?.[1]);
		expect(nodeY('.workflow-node.more')).toBeGreaterThan(nodeY('[data-job-id="item-19"]'));

		await wrapper.get('.workflow-node.more').trigger('keydown', { key: 'Enter' });
		expect(wrapper.findAll('[data-job-id^="item-"]')).toHaveLength(25);
		expect(wrapper.find('.workflow-node.more').exists()).toBe(false);

		await wrapper.get('[data-job-id="item-0"]').trigger('click');
		expect(wrapper.emitted('select')).toEqual([['item-0']]);

		await wrapper.get('.workflow-group-chip button').trigger('click');
		expect(wrapper.find('.workflow-node.stack').exists()).toBe(true);
		expect(wrapper.find('.workflow-toolbar').exists()).toBe(false);
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
