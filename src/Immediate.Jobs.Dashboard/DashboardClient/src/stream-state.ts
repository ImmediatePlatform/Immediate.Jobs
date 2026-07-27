import { ref } from 'vue';

import type { HistoryPoint, JobMonitoringSnapshot } from '@/contracts';

export type ConnectionStatus = 'connecting' | 'live' | 'reconnecting';

export const connectionStatus = ref<ConnectionStatus>('connecting');
export const dashboardHistory = ref<HistoryPoint[]>([]);

function count(snapshot: JobMonitoringSnapshot, state: 'Succeeded' | 'Failed' | 'Pending' | 'Scheduled'): number {
	return snapshot.counts[state] ?? 0;
}

export function recordSnapshot(snapshot: JobMonitoringSnapshot): void {
	if (dashboardHistory.value.at(-1)?.capturedAt === snapshot.capturedAt) {
		return;
	}

	const complete = count(snapshot, 'Succeeded') + count(snapshot, 'Failed');
	const previousComplete = dashboardHistory.value.at(-1)?.complete ?? complete;
	const nextPoint: HistoryPoint = {
		capturedAt: snapshot.capturedAt,
		complete,
		throughput: Math.max(0, complete - previousComplete),
		queued: count(snapshot, 'Pending') + count(snapshot, 'Scheduled'),
	};
	dashboardHistory.value = [...dashboardHistory.value.slice(-29), nextPoint];
}
