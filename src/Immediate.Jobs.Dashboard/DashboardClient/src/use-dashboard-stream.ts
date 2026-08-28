import { onBeforeUnmount, onMounted, watch, type MaybeRefOrGetter, toValue } from 'vue';
import { useQueryClient } from '@tanstack/vue-query';

import { apiUrl } from '@/api';
import type { BatchGraph, BatchStatus, DashboardState } from '@/contracts';
import { errorText, notify } from '@/notifications';
import { applyDashboardState, queryKeys } from '@/query';
import { connectionStatus } from '@/stream-state';

function readEvent<T>(event: MessageEvent<string>, apply: (value: T) => void): void {
	try {
		apply(JSON.parse(event.data) as T);
	} catch (reason) {
		notify(errorText(reason), 'error');
	}
}

export function useDashboardStream(): void {
	const queryClient = useQueryClient();
	let events: EventSource | undefined;

	function connect(): void {
		connectionStatus.value = 'connecting';
		events = new EventSource(apiUrl('events'));
		events.onopen = () => {
			connectionStatus.value = 'live';
		};
		events.onerror = () => {
			connectionStatus.value = 'reconnecting';
		};
		events.addEventListener('state', (event) => {
			readEvent<DashboardState>(event, (state) => applyDashboardState(queryClient, state));
		});
	}

	onMounted(connect);
	onBeforeUnmount(() => events?.close());
}

export function useBatchStream(batchHandle: MaybeRefOrGetter<string | undefined>): void {
	const queryClient = useQueryClient();
	let events: EventSource | undefined;

	function close(): void {
		events?.close();
		events = undefined;
	}

	function connect(id: string | undefined): void {
		close();
		if (!id) {
			return;
		}

		events = new EventSource(apiUrl(`batches/${encodeURIComponent(id)}/stream`));
		events.addEventListener('status', (event) => {
			readEvent<BatchStatus>(event, (status) => {
				queryClient.setQueryData(queryKeys.batch(id), status);
				queryClient.setQueryData<BatchStatus[]>(queryKeys.batches, (current = []) => {
					const exists = current.some((batch) => batch.batchHandle === status.batchHandle);
					return exists
						? current.map((batch) => batch.batchHandle === status.batchHandle ? status : batch)
						: [status, ...current];
				});
			});
		});
		events.addEventListener('graph', (event) => {
			readEvent<BatchGraph>(event, (graph) => {
				queryClient.setQueryData(queryKeys.batchGraph(id), graph);
				for (const node of graph.nodes) {
					queryClient.setQueryData(queryKeys.job(node.jobHandle), (current: unknown) => {
						if (!current || typeof current !== 'object') {
							return current;
						}
						return { ...current, state: node.state };
					});
				}
			});
		});
	}

	watch(() => toValue(batchHandle), connect, { immediate: true });
	onBeforeUnmount(close);
}
