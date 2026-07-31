export const jobStates = [
	'Scheduled',
	'Pending',
	'AwaitingContinuation',
	'AwaitingParameters',
	'Active',
	'Succeeded',
	'Failed',
	'Cancelled',
] as const;

export type JobState = (typeof jobStates)[number];

export const batchStates = ['Executing', 'Succeeded', 'Failed', 'Cancelled'] as const;

export type BatchState = (typeof batchStates)[number];
export type ContinuationTrigger = 'Success' | 'Failure' | 'Complete';
export type IsoDateTime = string;

export interface JobRecord {
	queueName: string;
	id: string;
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
	batchId: string | null;
	remainingDependencies: number;
	failedDependencies: number;
}

export function canRetryJob(job: Pick<JobRecord, 'state'>): boolean {
	return job.state === 'Failed' || job.state === 'Scheduled';
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

export interface JobServerSnapshot {
	workerId: string;
	lastHeartbeat: IsoDateTime;
	activeWorkers: number;
	maxWorkers: number;
}

export interface JobMonitoringSnapshot {
	capturedAt: IsoDateTime;
	counts: Partial<Record<JobState, number>>;
	recurring: RecurringJobSchedule[];
	servers: JobServerSnapshot[];
	capabilities?: string;
}

export interface DashboardJobPage {
	items: JobRecord[];
	skip: number;
	take: number;
	hasNext: boolean;
}

export interface BatchStatus {
	id: string;
	state: BatchState;
	total: number;
	succeeded: number;
	failed: number;
	cancelled: number;
	remaining: number;
	createdAt: IsoDateTime;
	startedAt: IsoDateTime | null;
	completedAt: IsoDateTime | null;
	fractionSettled: number;
}

export interface BatchGraphNode {
	jobId: string;
	jobName: string;
	state: JobState;
}

export interface BatchGraphEdge {
	childJobId: string;
	parentJobId: string | null;
	parentBatchId: string | null;
	trigger: ContinuationTrigger;
}

export interface BatchGraph {
	batchId: string;
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
