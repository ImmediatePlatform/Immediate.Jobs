import type { BatchGraph, BatchStatus, JobRecord } from '@/contracts';

export const completedJob: JobRecord = {
	jobHandle: '86bf8c31-d8e6-415b-8e92-45587a09fc52',
	batchHandle: 'batch-42',
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
	batchHandle: 'campaign-batch',
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
	batchHandle: 'deploy',
	nodes: [
		{ jobHandle: 'build', jobName: 'Build', state: 'Succeeded' },
		{ jobHandle: 'region-a', jobName: 'Deploy A', state: 'Active' },
		{ jobHandle: 'region-b', jobName: 'Deploy B', state: 'Pending' },
		{ jobHandle: 'smoke', jobName: 'order-record-fraud-assessment', state: 'AwaitingContinuation' },
	],
	edges: [
		{ childJobHandle: 'region-a', parentJobHandle: 'build', parentBatchHandle: null, trigger: 'Success' },
		{ childJobHandle: 'region-b', parentJobHandle: 'build', parentBatchHandle: null, trigger: 'Success' },
		{ childJobHandle: 'smoke', parentJobHandle: 'region-a', parentBatchHandle: null, trigger: 'Complete' },
		{ childJobHandle: 'smoke', parentJobHandle: 'region-b', parentBatchHandle: null, trigger: 'Complete' },
	],
};
