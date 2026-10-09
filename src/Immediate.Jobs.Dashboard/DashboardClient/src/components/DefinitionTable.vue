<script setup lang="ts">
import { RouterLink } from 'vue-router';
import { CirclePause, CirclePlay } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import StateBadge from '@/components/StateBadge.vue';
import type { JobAcquisitionStatus, JobDefinitionStatus } from '@/contracts';
import { formatDate } from '@/format';

defineProps<{
	definitions: JobDefinitionStatus[];
	busyName?: string | undefined;
}>();

defineEmits<{
	setPaused: [variables: { name: string; paused: boolean }];
}>();

const statusLabels: Record<JobAcquisitionStatus, string> = {
	Ready: 'Ready',
	Paused: 'Paused',
	RateLimited: 'Rate limited',
	ConcurrencyLimited: 'Concurrency limited',
};

function windowLimit(maximum: number, period: string | null): string {
	return maximum > 0 && period ? `${maximum} / ${period}` : 'No limit';
}
</script>

<template>
	<div class="table-card">
		<FeedbackState v-if="definitions.length === 0" title="No job definitions" description="Definitions provided at application startup will appear here." />
		<div v-else class="table-scroll">
			<table>
				<thead>
					<tr>
						<th>Definition</th>
						<th>Actions</th>
						<th>Queue</th>
						<th>Tags</th>
						<th>Schedule</th>
						<th>Attempts</th>
						<th>Timeout</th>
						<th>Acquisition</th>
						<th>Concurrency</th>
						<th>Sliding window</th>
						<th>Fixed window</th>
						<th>Next eligible</th>
					</tr>
				</thead>
				<tbody>
					<tr v-for="definition in definitions" :key="definition.name">
						<td>
							<RouterLink :to="{ name: 'jobs', query: { search: definition.name, queue: definition.queueName } }">
								<code>{{ definition.name }}</code>
							</RouterLink>
						</td>
						<td>
							<div class="row-actions">
								<button
									class="button button-secondary"
									type="button"
									:aria-label="`${definition.acquisition?.isPaused ? 'Resume' : 'Pause'} ${definition.name}`"
									:aria-busy="busyName === definition.name"
									:disabled="busyName !== undefined || !definition.acquisition"
									@click="$emit('setPaused', { name: definition.name, paused: !definition.acquisition?.isPaused })"
								>
									<CirclePlay v-if="definition.acquisition?.isPaused" :size="14" aria-hidden="true" />
									<CirclePause v-else :size="14" aria-hidden="true" />
									{{ busyName === definition.name ? 'Updating…' : definition.acquisition?.isPaused ? 'Resume' : 'Pause' }}
								</button>
							</div>
						</td>
						<td>{{ definition.queueName }}</td>
						<td><span v-for="tag in definition.tags" :key="tag" class="tag">{{ tag }}</span></td>
						<td>
							<div v-if="definition.cron" class="schedule-cell"><code>{{ definition.cron }}</code><span>{{ definition.timeZone }}</span></div>
							<span v-else>On demand</span>
						</td>
						<td>{{ definition.maxAttempts }}</td>
						<td>{{ definition.timeout ?? 'No timeout' }}</td>
						<td><StateBadge v-if="definition.acquisition" :state="statusLabels[definition.acquisition.acquisitionStatus]" /><span v-else>Status unavailable</span></td>
						<td>
							<div v-if="definition.acquisition" class="schedule-cell">
								<span v-if="definition.limits.maxConcurrency > 0">
									{{ definition.acquisition.activeCount }} / {{ definition.limits.maxConcurrency }} active
								</span>
								<span v-else>{{ definition.acquisition.activeCount }} active · no limit</span>
								<StateBadge v-if="definition.acquisition.isConcurrencyLimited" state="At capacity" />
							</div>
						</td>
						<td>{{ windowLimit(definition.limits.slidingWindowMax, definition.limits.slidingWindowPeriod) }}</td>
						<td>{{ windowLimit(definition.limits.fixedWindowMax, definition.limits.fixedWindowPeriod) }}</td>
						<td>{{ formatDate(definition.acquisition?.nextEligibleAt ?? null) }}</td>
					</tr>
				</tbody>
			</table>
		</div>
	</div>
</template>
