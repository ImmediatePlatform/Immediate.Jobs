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
	getDefinitions,
	getJob,
	getJobTelemetryLinks,
	getJobs,
	getOverview,
	getRecentJobs,
} from '@/api';
import type {
	BatchGraph,
	BatchStatus,
	DashboardJobPage,
	DashboardState,
	JobFilters,
	JobDefinitionStatus,
	JobMonitoringDefinitions,
	JobMonitoringSnapshot,
	JobRecord,
	JobTelemetryLink,
	RecurringJobSchedule,
} from '@/contracts';
import { connectionStatus, recordSnapshot } from '@/stream-state';

export const queryKeys = {
	overview: ['overview'] as const,
	definitions: ['definitions'] as const,
	recentJobs: ['jobs', 'recent'] as const,
	jobPages: ['jobs', 'page'] as const,
	jobs: (filters: JobFilters) => ['jobs', 'page', filters] as const,
	job: (jobHandle: string) => ['jobs', 'detail', jobHandle] as const,
	jobTelemetryLinks: (jobHandle: string) => ['jobs', 'telemetry-links', jobHandle] as const,
	batchRoot: ['batches'] as const,
	batches: ['batches', 'list'] as const,
	batch: (batchHandle: string) => ['batches', 'detail', batchHandle] as const,
	batchGraph: (batchHandle: string) => ['batches', 'graph', batchHandle] as const,
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
		staleTime: computed(() => connectionStatus.value === 'live' ? Infinity : 5_000),
	});
}

export function useDefinitionsQuery(): UseQueryReturnType<JobMonitoringDefinitions, Error> {
	return useQuery({
		queryKey: queryKeys.definitions,
		queryFn: ({ signal }) => getDefinitions(signal),
		retry: shouldRetry,
		staleTime: 300_000,
		refetchInterval: 300_000,
		refetchOnWindowFocus: false,
	});
}

export function useDefinitionStatusesQuery() {
	const definitions = useDefinitionsQuery();
	const snapshot = useOverviewQuery();
	return {
		data: computed<JobDefinitionStatus[]>(() => {
			const statuses = new Map(snapshot.data.value?.definitionStatuses.map(status => [status.jobName.toUpperCase(), status]));
			return (definitions.data.value?.jobs ?? []).filter(definition => !snapshot.data.value || statuses.has(definition.name.toUpperCase())).map(definition => ({
				...definition,
				limits: {
					maxConcurrency: definition.maxConcurrency,
					slidingWindowMax: definition.slidingWindowMax,
					slidingWindowPeriod: definition.slidingWindowPeriod,
					fixedWindowMax: definition.fixedWindowMax,
					fixedWindowPeriod: definition.fixedWindowPeriod,
				},
				acquisition: statuses.get(definition.name.toUpperCase()),
			}));
		}),
		error: computed(() => definitions.error.value ?? snapshot.error.value),
		isPending: definitions.isPending,
	};
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

export function useJobQuery(jobHandle: MaybeRefOrGetter<string | undefined>): UseQueryReturnType<JobRecord, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.job(toValue(jobHandle) ?? '')),
		queryFn: ({ signal }) => getJob(toValue(jobHandle) ?? '', signal),
		enabled: computed(() => Boolean(toValue(jobHandle))),
		retry: shouldRetry,
	});
}

export function useJobTelemetryLinksQuery(
	jobHandle: MaybeRefOrGetter<string | undefined>,
): UseQueryReturnType<JobTelemetryLink[], Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.jobTelemetryLinks(toValue(jobHandle) ?? '')),
		queryFn: ({ signal }) => getJobTelemetryLinks(toValue(jobHandle) ?? '', signal),
		enabled: computed(() => Boolean(toValue(jobHandle))),
		retry: shouldRetry,
		refetchInterval: 5_000,
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

export function useBatchQuery(batchHandle: MaybeRefOrGetter<string | undefined>): UseQueryReturnType<BatchStatus, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.batch(toValue(batchHandle) ?? '')),
		queryFn: ({ signal }) => getBatch(toValue(batchHandle) ?? '', signal),
		enabled: computed(() => Boolean(toValue(batchHandle))),
		retry: shouldRetry,
	});
}

export function useBatchGraphQuery(batchHandle: MaybeRefOrGetter<string | undefined>): UseQueryReturnType<BatchGraph, Error> {
	return useQuery({
		queryKey: computed(() => queryKeys.batchGraph(toValue(batchHandle) ?? '')),
		queryFn: ({ signal }) => getBatchGraph(toValue(batchHandle) ?? '', signal),
		enabled: computed(() => Boolean(toValue(batchHandle))),
		retry: shouldRetry,
	});
}

export function useRecurringQuery() {
	const definitions = useDefinitionsQuery();
	const snapshot = useOverviewQuery();
	return {
		data: computed<RecurringJobSchedule[]>(() => {
			const statuses = new Map(snapshot.data.value?.recurring.map(status => [status.name, status]));
			return (definitions.data.value?.recurring ?? []).flatMap(definition => {
				const status = statuses.get(definition.name);
				return status ? [{ ...definition, ...status }] : [];
			});
		}),
		error: computed(() => definitions.error.value ?? snapshot.error.value),
		isPending: computed(() => definitions.isPending.value || snapshot.isPending.value),
	};
}

export function useServersQuery() {
	return useQuery({
		queryKey: queryKeys.overview,
		queryFn: ({ signal }) => getOverview(signal),
		select: snapshot => snapshot.servers,
		retry: shouldRetry,
		staleTime: computed(() => connectionStatus.value === 'live' ? Infinity : 5_000),
		refetchInterval: liveRefetchInterval,
	});
}

export function applyDashboardState(queryClient: QueryClient, state: DashboardState): void {
	void queryClient.cancelQueries({ queryKey: queryKeys.overview });
	recordSnapshot(state.snapshot);
	queryClient.setQueryData(queryKeys.overview, state.snapshot);
	queryClient.setQueryData(queryKeys.recentJobs, state.jobs.slice(0, 8));
	queryClient.setQueryData(queryKeys.batches, state.batches);

	for (const job of state.jobs) {
		queryClient.setQueryData(queryKeys.job(job.jobHandle), job);
	}

	void queryClient.invalidateQueries({ queryKey: queryKeys.jobPages, refetchType: 'active' });
}

export async function refreshDashboardQueries(queryClient: QueryClient): Promise<void> {
	await Promise.all([
		queryClient.invalidateQueries({ queryKey: queryKeys.overview }),
		queryClient.invalidateQueries({ queryKey: queryKeys.definitions }),
		queryClient.invalidateQueries({ queryKey: queryKeys.recentJobs }),
		queryClient.invalidateQueries({ queryKey: queryKeys.jobPages }),
		queryClient.invalidateQueries({ queryKey: queryKeys.batches }),
	]);
}
