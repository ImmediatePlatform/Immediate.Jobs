import { computed } from 'vue';
import { useMutation, useQueryClient } from '@tanstack/vue-query';

import {
	cancelBatch,
	cancelJob,
	deleteBatch,
	retryJob,
	setRecurringPaused,
	triggerRecurring,
} from '@/api';
import { errorText, notify } from '@/notifications';
import { queryKeys, refreshDashboardQueries } from '@/query';

export function useJobMutations() {
	const queryClient = useQueryClient();
	const cancelMutation = useMutation({
		mutationFn: cancelJob,
		onSuccess: async (_, jobHandle) => {
			notify('Job cancelled.');
			await queryClient.invalidateQueries({ queryKey: queryKeys.job(jobHandle) });
			await queryClient.invalidateQueries({ queryKey: queryKeys.batchRoot });
			await refreshDashboardQueries(queryClient);
		},
		onError: (reason) => notify(errorText(reason), 'error'),
	});
	const retryMutation = useMutation({
		mutationFn: retryJob,
		onSuccess: async (_, jobHandle) => {
			notify('Job queued for retry.');
			await queryClient.invalidateQueries({ queryKey: queryKeys.job(jobHandle) });
			await refreshDashboardQueries(queryClient);
		},
		onError: (reason) => notify(errorText(reason), 'error'),
	});
	return {
		cancelJob: cancelMutation.mutateAsync,
		retryJob: retryMutation.mutate,
		busyJobHandle: computed(() => {
			if (cancelMutation.isPending.value) {
				return cancelMutation.variables.value;
			}
			if (retryMutation.isPending.value) {
				return retryMutation.variables.value;
			}
			return undefined;
		}),
		cancelling: cancelMutation.isPending,
	};
}

export function useBatchMutations() {
	const queryClient = useQueryClient();
	const cancelMutation = useMutation({
		mutationFn: cancelBatch,
		onSuccess: async (_, batchHandle) => {
			notify('Batch cancellation requested.');
			await queryClient.invalidateQueries({ queryKey: queryKeys.batch(batchHandle) });
			await refreshDashboardQueries(queryClient);
		},
		onError: (reason) => notify(errorText(reason), 'error'),
	});
	const deleteMutation = useMutation({
		mutationFn: deleteBatch,
		onSuccess: async (_, batchHandle) => {
			notify('Batch deleted.');
			queryClient.removeQueries({ queryKey: queryKeys.batch(batchHandle) });
			queryClient.removeQueries({ queryKey: queryKeys.batchGraph(batchHandle) });
			await refreshDashboardQueries(queryClient);
		},
		onError: (reason) => notify(errorText(reason), 'error'),
	});

	return {
		cancelBatch: cancelMutation.mutateAsync,
		deleteBatch: deleteMutation.mutateAsync,
		busyBatchHandle: computed(() => {
			if (cancelMutation.isPending.value) {
				return cancelMutation.variables.value;
			}
			return deleteMutation.isPending.value ? deleteMutation.variables.value : undefined;
		}),
		mutating: computed(() => cancelMutation.isPending.value || deleteMutation.isPending.value),
	};
}

export function useRecurringMutations() {
	const queryClient = useQueryClient();
	const triggerMutation = useMutation({
		mutationFn: triggerRecurring,
		onSuccess: async () => {
			notify('Recurring job triggered.');
			await refreshDashboardQueries(queryClient);
		},
		onError: (reason) => notify(errorText(reason), 'error'),
	});
	const pauseMutation = useMutation({
		mutationFn: ({ name, paused }: { name: string; paused: boolean }) => setRecurringPaused(name, paused),
		onSuccess: async (_, variables) => {
			notify(variables.paused ? 'Schedule paused.' : 'Schedule resumed.');
			await queryClient.invalidateQueries({ queryKey: queryKeys.recurring });
			await queryClient.invalidateQueries({ queryKey: queryKeys.overview });
		},
		onError: (reason) => notify(errorText(reason), 'error'),
	});

	return {
		trigger: triggerMutation.mutate,
		setPaused: pauseMutation.mutate,
		busyName: computed(() => {
			if (triggerMutation.isPending.value) {
				return triggerMutation.variables.value;
			}
			return pauseMutation.isPending.value ? pauseMutation.variables.value?.name : undefined;
		}),
	};
}
