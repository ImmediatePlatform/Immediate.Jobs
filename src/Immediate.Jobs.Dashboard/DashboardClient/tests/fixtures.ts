import type { BatchGraph, BatchStatus, JobRecord } from '@/contracts';

export const completedJob: JobRecord = {
	id: '86bf8c31-d8e6-415b-8e92-45587a09fc52',
	batchId: 'batch-42',
	jobName: 'SendGreeting',
	queueName: 'default',
	groupId: 'tenant-a',
	payload: '{"name":"Duke"}',
	context: '{"http-request":{"clientIpAddress":"127.0.0.1","userAgent":"curl/8.7.1"}}',
	state: 'Succeeded',
	attempt: 1,
	createdAt: '2026-07-21T11:59:59Z',
	dueAt: '2026-07-21T12:00:00Z',
	completedAt: '2026-07-21T12:00:01Z',
	workerId: null,
	leaseExpiresAt: null,
	lastError: null,
	recurringKey: null,
	traceParent: null,
	traceState: null,
	executionTraceId: '4bf92f3577b34da6a3ce929d0e0e4736',
	executionSpanId: '00f067aa0ba902b7',
	executionStartedAt: '2026-07-21T12:00:00Z',
	remainingDependencies: 0,
	failedDependencies: 0,
};

export const executingBatch: BatchStatus = {
	id: 'campaign-batch',
	state: 'Executing',
	total: 10,
	succeeded: 6,
	failed: 1,
	cancelled: 1,
	skipped: 0,
	remaining: 2,
	createdAt: '2026-07-21T12:00:00Z',
	startedAt: '2026-07-21T12:00:01Z',
	completedAt: null,
	fractionSettled: 0.8,
};

export const workflowGraph: BatchGraph = {
	batchId: 'deploy',
	nodes: [
		{ jobId: 'build', jobName: 'Build', state: 'Succeeded' },
		{ jobId: 'region-a', jobName: 'Deploy A', state: 'Active' },
		{ jobId: 'region-b', jobName: 'Deploy B', state: 'Pending' },
		{ jobId: 'smoke', jobName: 'order-record-fraud-assessment', state: 'AwaitingContinuation' },
	],
	edges: [
		{ childJobId: 'region-a', parentJobId: 'build', parentBatchId: null, trigger: 'Success' },
		{ childJobId: 'region-b', parentJobId: 'build', parentBatchId: null, trigger: 'Success' },
		{ childJobId: 'smoke', parentJobId: 'region-a', parentBatchId: null, trigger: 'Complete' },
		{ childJobId: 'smoke', parentJobId: 'region-b', parentBatchId: null, trigger: 'Complete' },
	],
};
