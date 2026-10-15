import type { JobAcquisitionStatus, JobState } from '@/contracts';

export function formatDate(value: string | null | undefined): string {
	return value ? new Date(value).toLocaleString() : '—';
}

export function formatJson(value: string): string {
	try {
		return JSON.stringify(JSON.parse(value), null, 2);
	} catch {
		return value;
	}
}

export function formatJobState(state: JobState, definitionPaused: boolean): string {
	return state === 'Pending' && definitionPaused ? 'Paused' : state;
}

const acquisitionStatusLabels: Record<JobAcquisitionStatus, string> = {
	Ready: 'Ready',
	Paused: 'Paused',
	RateLimited: 'Rate limited',
	ConcurrencyLimited: 'Concurrency limited',
};

export function formatAcquisitionStatus(status: JobAcquisitionStatus): string {
	return acquisitionStatusLabels[status];
}
