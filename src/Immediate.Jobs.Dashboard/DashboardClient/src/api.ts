import type {
	BatchGraph,
	BatchState,
	BatchStatus,
	DashboardJobPage,
	DashboardJobExecutionPage,
	JobFilters,
	JobMonitoringSnapshot,
	JobRecord,
	JobServerSnapshot,
	JobTelemetryLink,
	RecurringJobSchedule,
} from '@/contracts';

export class ApiError extends Error {
	public constructor(
		message: string,
		public readonly status: number,
	) {
		super(message);
		this.name = 'ApiError';
	}
}

export function apiUrl(path: string): URL {
	const normalizedPath = path.replace(/^\/+/, '');
	return new URL(`api/${normalizedPath}`, document.baseURI);
}

async function errorMessage(response: Response): Promise<string> {
	const text = await response.text();
	if (!text) {
		return `${response.status} ${response.statusText}`;
	}

	try {
		const problem = JSON.parse(text) as { detail?: string; title?: string };
		return problem.detail || problem.title || text;
	} catch {
		return text;
	}
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
	const headers = new Headers(init?.headers);
	headers.set('accept', 'application/json');

	const response = await fetch(apiUrl(path), { ...init, headers });
	if (!response.ok) {
		throw new ApiError(await errorMessage(response), response.status);
	}

	if (response.status === 202 || response.status === 204) {
		return undefined as T;
	}

	return response.json() as Promise<T>;
}

export function getOverview(signal?: AbortSignal): Promise<JobMonitoringSnapshot> {
	return request('overview', { signal });
}

export function getJobs(filters: JobFilters, signal?: AbortSignal): Promise<DashboardJobPage> {
	const parameters = new URLSearchParams({
		skip: String((filters.page - 1) * 50),
		take: '50',
	});
	if (filters.search) {
		parameters.set('search', filters.search);
	}
	if (filters.queue) {
		parameters.set('queue', filters.queue);
	}
	if (filters.state) {
		parameters.set('state', filters.state);
	}

	return request(`jobs?${parameters}`, { signal });
}

export async function getRecentJobs(signal?: AbortSignal): Promise<JobRecord[]> {
	const page = await request<DashboardJobPage>('jobs?skip=0&take=8', { signal });
	return page.items;
}

export function getJob(jobId: string, signal?: AbortSignal): Promise<JobRecord> {
	return request(`jobs/${encodeURIComponent(jobId)}`, { signal });
}

export function getJobTelemetryLinks(jobId: string, signal?: AbortSignal): Promise<JobTelemetryLink[]> {
	return request(`jobs/${encodeURIComponent(jobId)}/telemetry-links`, { signal });
}

export function getJobExecutions(
	jobId: string,
	skip = 0,
	take = 20,
	signal?: AbortSignal,
): Promise<DashboardJobExecutionPage> {
	const parameters = new URLSearchParams({ skip: String(skip), take: String(take) });
	return request(`jobs/${encodeURIComponent(jobId)}/executions?${parameters}`, { signal });
}

export function getJobExecutionTelemetryLinks(
	jobId: string,
	executionNumber: number,
	signal?: AbortSignal,
): Promise<JobTelemetryLink[]> {
	return request(
		`jobs/${encodeURIComponent(jobId)}/executions/${executionNumber}/telemetry-links`,
		{ signal },
	);
}

export function getBatches(signal?: AbortSignal): Promise<BatchStatus[]> {
	return request('batches?skip=0&take=100', { signal });
}

export function getBatch(batchId: string, signal?: AbortSignal): Promise<BatchStatus> {
	return request(`batches/${encodeURIComponent(batchId)}`, { signal });
}

export function getBatchGraph(batchId: string, signal?: AbortSignal): Promise<BatchGraph> {
	return request(`batches/${encodeURIComponent(batchId)}/graph`, { signal });
}

export function getRecurring(signal?: AbortSignal): Promise<RecurringJobSchedule[]> {
	return request('recurring', { signal });
}

export function getServers(signal?: AbortSignal): Promise<JobServerSnapshot[]> {
	return request('servers', { signal });
}

export function retryJob(jobId: string): Promise<void> {
	return request(`jobs/${encodeURIComponent(jobId)}/retry`, { method: 'POST' });
}

export function cancelJob(jobId: string): Promise<void> {
	return request(`jobs/${encodeURIComponent(jobId)}`, { method: 'POST' });
}

export function cancelBatch(batchId: string): Promise<void> {
	return request(`batches/${encodeURIComponent(batchId)}/cancel`, { method: 'POST' });
}

export function deleteBatch(batchId: string): Promise<void> {
	return request(`batches/${encodeURIComponent(batchId)}`, { method: 'DELETE' });
}

export function triggerRecurring(name: string): Promise<void> {
	return request(`recurring/${encodeURIComponent(name)}/trigger`, { method: 'POST' });
}

export function setRecurringPaused(name: string, paused: boolean): Promise<void> {
	const action = paused ? 'pause' : 'resume';
	return request(`recurring/${encodeURIComponent(name)}/${action}`, { method: 'POST' });
}

export function isBatchState(value: string): value is BatchState {
	return ['Executing', 'Succeeded', 'Failed', 'Cancelled'].includes(value);
}
