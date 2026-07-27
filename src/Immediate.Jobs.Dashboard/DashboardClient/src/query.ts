import { computed, type MaybeRefOrGetter, toValue } from 'vue';
import {
	useQuery,
	type QueryClient,
	type UseQueryReturnType,
} from '@tanstack/vue-query';

import {
	ApiError,
	getBatch,
	getBatchGraph,
	getBatches,
	getJob,
	getJobs,
	getOverview,
	getRecentJobs,
	getRecurring,
	getServers,
} from '@/api';
import type {
	BatchGraph,
	BatchStatus,
	DashboardJobPage,
	DashboardState,
	JobFilters,
	JobMonitoringSnapshot,
	JobRecord,
	JobServerSnapshot,
	RecurringJobSchedule,
} from '@/contracts';
import { connectionStatus, recordSnapshot } from '@/stream-state';

export const queryKeys = {
	overview: ['overview'] as const,
	recentJobs: ['jobs', 'recent'] as const,
	jobPages: ['jobs', 'page'] as const,
	jobs: (filters: JobFilters) => ['jobs', 'page', filters] as const,
	job: (jobId: string) => ['jobs', 'detail', jobId] as const,
	batches: ['batches', 'list'] as const,
	batch: (batchId: string) => ['batches', 'detail', batchId] as const,
	batchGraph: (batchId: string) => ['batches', 'graph', batchId] as const,
	recurring: ['recurring'] as const,
	servers: ['servers'] as const,
};

function shouldRetry(failureCount: number, error: Error): boolean {
	return failureCount < 1 && (!(error instanceof ApiError) || error.status >= 500);
}

const liveRefetchInterval = computed(() => connectionStatus.value === 'live' ? false : 5_000);

export function useOverviewQuery(): UseQueryReturnType<JobMonitoringSnapshot, Error> {
	return useQuery({
		queryKey: queryKeys.overview,
		queryFn: ({ signal }) => getOverview(signal),
		retry: shouldRetry,
		refetchInterval: liveRefetchInterval,
	});
}

export function useRecentJobsQuery(): UseQueryReturnType<JobRecord[], Error> {
	return useQuery({
		queryKey: queryKeys.recentJobs,
		queryFn: ({ signal }) => getRecentJobs(signal),
		retry: shouldRetry,
		refetchInterval: liveRefetchInterval,
	});
}

export function useJobsQuery(filters: MaybeRefOrGetter<JobFilters>): UseQueryReturnType<DashboardJobPage, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.jobs(toValue(filters))),
		queryFn: ({ signal }) => getJobs(toValue(filters), signal),
		placeholderData: (previous) => previous,
		retry: shouldRetry,
		refetchInterval: liveRefetchInterval,
	});
}

export function useJobQuery(jobId: MaybeRefOrGetter<string | undefined>): UseQueryReturnType<JobRecord, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.job(toValue(jobId) ?? '')),
		queryFn: ({ signal }) => getJob(toValue(jobId) ?? '', signal),
		enabled: computed(() => Boolean(toValue(jobId))),
		retry: shouldRetry,
	});
}

export function useBatchesQuery(): UseQueryReturnType<BatchStatus[], Error> {
	return useQuery({
		queryKey: queryKeys.batches,
		queryFn: ({ signal }) => getBatches(signal),
		retry: shouldRetry,
		refetchInterval: liveRefetchInterval,
	});
}

export function useBatchQuery(batchId: MaybeRefOrGetter<string | undefined>): UseQueryReturnType<BatchStatus, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.batch(toValue(batchId) ?? '')),
		queryFn: ({ signal }) => getBatch(toValue(batchId) ?? '', signal),
		enabled: computed(() => Boolean(toValue(batchId))),
		retry: shouldRetry,
	});
}

export function useBatchGraphQuery(batchId: MaybeRefOrGetter<string | undefined>): UseQueryReturnType<BatchGraph, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.batchGraph(toValue(batchId) ?? '')),
		queryFn: ({ signal }) => getBatchGraph(toValue(batchId) ?? '', signal),
		enabled: computed(() => Boolean(toValue(batchId))),
		retry: shouldRetry,
	});
}

export function useRecurringQuery(): UseQueryReturnType<RecurringJobSchedule[], Error> {
	return useQuery({
		queryKey: queryKeys.recurring,
		queryFn: ({ signal }) => getRecurring(signal),
		retry: shouldRetry,
		refetchInterval: liveRefetchInterval,
	});
}

export function useServersQuery(): UseQueryReturnType<JobServerSnapshot[], Error> {
	return useQuery({
		queryKey: queryKeys.servers,
		queryFn: ({ signal }) => getServers(signal),
		retry: shouldRetry,
		refetchInterval: liveRefetchInterval,
	});
}

export function applyDashboardState(queryClient: QueryClient, state: DashboardState): void {
	recordSnapshot(state.snapshot);
	queryClient.setQueryData(queryKeys.overview, state.snapshot);
	queryClient.setQueryData(queryKeys.recentJobs, state.jobs.slice(0, 8));
	queryClient.setQueryData(queryKeys.batches, state.batches);
	queryClient.setQueryData(queryKeys.recurring, state.snapshot.recurring);
	queryClient.setQueryData(queryKeys.servers, state.snapshot.servers);

	for (const job of state.jobs) {
		queryClient.setQueryData(queryKeys.job(job.id), job);
	}

	void queryClient.invalidateQueries({ queryKey: queryKeys.jobPages, refetchType: 'active' });
}

export async function refreshDashboardQueries(queryClient: QueryClient): Promise<void> {
	await Promise.all([
		queryClient.invalidateQueries({ queryKey: queryKeys.overview }),
		queryClient.invalidateQueries({ queryKey: queryKeys.recentJobs }),
		queryClient.invalidateQueries({ queryKey: queryKeys.jobPages }),
		queryClient.invalidateQueries({ queryKey: queryKeys.batches }),
		queryClient.invalidateQueries({ queryKey: queryKeys.recurring }),
		queryClient.invalidateQueries({ queryKey: queryKeys.servers }),
	]);
}
