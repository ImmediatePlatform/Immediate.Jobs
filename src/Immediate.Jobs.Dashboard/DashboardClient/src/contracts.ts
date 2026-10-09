export const jobStates = [
	'Scheduled',
	'Pending',
	'AwaitingContinuation',
	'WaitingForTrigger',
	'Active',
	'Succeeded',
	'Failed',
	'Cancelled',
	'Skipped',
] as const;

export type JobState = (typeof jobStates)[number];

export const batchStates = ['WaitingForTrigger', 'Executing', 'Succeeded', 'Failed', 'Cancelled'] as const;

export type BatchState = (typeof batchStates)[number];
export type ContinuationTrigger = 'Success' | 'Failure' | 'Complete';
export type IsoDateTime = string;

export interface JobRecord {
	queueName: string;
	jobHandle: string;
	jobName: string;
	groupId: string | null;
	payload: string;
	context: string | null;
	state: JobState;
	dueAt: IsoDateTime;
	createdAt: IsoDateTime;
	attempt: number;
	workerId: string | null;
	leaseExpiresAt: IsoDateTime | null;
	lastError: string | null;
	completedAt: IsoDateTime | null;
	recurringKey: string | null;
	traceParent: string | null;
	traceState: string | null;
	executionTraceId: string | null;
	executionSpanId: string | null;
	executionStartedAt: IsoDateTime | null;
	batchHandle: string | null;
	remainingDependencies: number;
	failedDependencies: number;
}

export function canRetryJob(job: Pick<JobRecord, 'state'>): boolean {
	return job.state === 'Failed' || job.state === 'Scheduled';
}

export function canCancelJob(job: Pick<JobRecord, 'state'>): boolean {
	return job.state !== 'Succeeded'
		&& job.state !== 'Failed'
		&& job.state !== 'Cancelled'
		&& job.state !== 'Skipped';
}

export interface RecurringJobSchedule {
	name: string;
	jobName: string;
	cron: string;
	timeZone: string;
	isCodeDefined: boolean;
	isPaused: boolean;
	nextRunAt: IsoDateTime;
	lastRunAt: IsoDateTime | null;
}

export interface JobDefinitionRecord {
	name: string;
	tags: string[];
	queueName: string;
	queuePriority: number;
	queueConcurrency: number;
	cron: string | null;
	timeZone: string;
	maxAttempts: number;
	timeout: string | null;
	maxConcurrency: number;
	slidingWindowMax: number;
	slidingWindowPeriod: string | null;
	fixedWindowMax: number;
	fixedWindowPeriod: string | null;
	overlapPolicy: 'Skip' | 'Queue' | 'Concurrent';
	misfireHandlingMode: 'EnqueueAll' | 'EnqueueOne' | 'EnqueueNone';
	backoff: 'Fixed' | 'Exponential' | 'ExponentialJitter';
	backoffBase: string;
}

export interface JobServerSnapshot {
	tags: string[];
	workerId: string;
	lastHeartbeat: IsoDateTime;
	activeWorkers: number;
	maxWorkers: number;
	serverTimeout: string;
	workers: JobWorkerSnapshot[];
	acquisition: JobLoopSnapshot;
	leaseRenewal: JobLoopSnapshot;
}

export interface JobLoopSnapshot {
	isRunning: boolean;
	lastAttemptedAt: IsoDateTime | null;
	lastSucceededAt: IsoDateTime | null;
	lastFailedAt: IsoDateTime | null;
	consecutiveFailures: number;
	itemsExamined: number;
	itemsSucceeded: number;
	itemsFailed: number;
}

export interface JobWorkerSnapshot {
	workerId: number;
	jobHandle: string | null;
	attempt: number | null;
	startedAt: IsoDateTime | null;
}

export interface JobMonitoringSnapshot {
	capturedAt: IsoDateTime;
	counts: Partial<Record<JobState, number>>;
	recurring: RecurringJobStatus[];
	servers: JobServerSnapshot[];
	definitionStatuses: JobAcquisitionState[];
	capabilities?: string;
}

export type JobAcquisitionStatus = 'Ready' | 'Paused' | 'RateLimited' | 'ConcurrencyLimited';

export interface JobAcquisitionLimits {
	slidingWindowMax: number;
	slidingWindowPeriod: string | null;
	fixedWindowMax: number;
	fixedWindowPeriod: string | null;
	maxConcurrency: number;
}

export interface JobAcquisitionState {
	jobName: string;
	isPaused: boolean;
	acquisitionStatus: JobAcquisitionStatus;
	nextEligibleAt: IsoDateTime | null;
	activeCount: number;
	isConcurrencyLimited: boolean;
}

export interface JobDefinitionStatus extends JobDefinitionRecord {
	limits: JobAcquisitionLimits;
	acquisition?: JobAcquisitionState | undefined;
}

export interface DashboardJobPage {
	items: JobRecord[];
	skip: number;
	take: number;
	hasNext: boolean;
}

export type JobExecutionState = 'Active' | 'Succeeded' | 'Failed' | 'Cancelled' | 'Interrupted';

export interface JobExecutionRecord {
	jobHandle: string;
	attempt: number;
	state: JobExecutionState;
	workerId: string | null;
	acquiredAt: IsoDateTime | null;
	executionStartedAt: IsoDateTime | null;
	completedAt: IsoDateTime | null;
	executionTraceId: string | null;
	executionSpanId: string | null;
	error: string | null;
	isSynthetic: boolean;
}

export interface DashboardJobExecutionPage {
	items: JobExecutionRecord[];
	skip: number;
	take: number;
	hasNext: boolean;
}

export interface BatchStatus {
	batchHandle: string;
	state: BatchState;
	total: number;
	succeeded: number;
	failed: number;
	cancelled: number;
	skipped: number;
	remaining: number;
	createdAt: IsoDateTime;
	startedAt: IsoDateTime | null;
	completedAt: IsoDateTime | null;
	fractionSettled: number;
}

export interface BatchGraphNode {
	jobHandle: string;
	jobName: string;
	state: JobState;
}

export interface BatchGraphEdge {
	childJobHandle: string;
	parentJobHandle: string | null;
	parentBatchHandle: string | null;
	trigger: ContinuationTrigger;
}

export interface BatchGraph {
	batchHandle: string;
	nodes: BatchGraphNode[];
	edges: BatchGraphEdge[];
}

export type JobTelemetryLinkKind = 'Trace' | 'Logs';

export interface JobTelemetryLink {
	label: string;
	kind: JobTelemetryLinkKind;
	url: string;
}

export interface DashboardState {
	snapshot: JobMonitoringSnapshot;
	jobs: JobRecord[];
	batches: BatchStatus[];
}

export interface ProblemDetails {
	type?: string;
	title?: string;
	status?: number;
	detail?: string;
	instance?: string;
}

export interface JobFilters {
	search: string;
	queue: string;
	state: JobState | '';
	page: number;
}

export interface HistoryPoint {
	capturedAt: IsoDateTime;
	complete: number;
	throughput: number;
	queued: number;
}

export type RecurringJobDefinition = Omit<RecurringJobSchedule, 'isPaused' | 'nextRunAt' | 'lastRunAt'>;
export type RecurringJobStatus = Pick<RecurringJobSchedule, 'name' | 'isPaused' | 'nextRunAt' | 'lastRunAt'>;

export interface JobMonitoringDefinitions {
	jobs: JobDefinitionRecord[];
	recurring: RecurringJobDefinition[];
}
