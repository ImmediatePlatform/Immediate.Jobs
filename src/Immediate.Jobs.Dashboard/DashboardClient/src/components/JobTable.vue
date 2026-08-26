<script setup lang="ts">
import { Ban, Eye, Layers3, RotateCcw } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import StateBadge from '@/components/StateBadge.vue';
import { canCancelJob, canRetryJob, type JobRecord } from '@/contracts';
import { formatDate } from '@/format';

withDefaults(defineProps<{
	rows: JobRecord[];
	busyJobId?: string;
}>(), {
	busyJobId: undefined,
});

const emit = defineEmits<{
	select: [job: JobRecord];
	openBatch: [batchId: string];
	cancel: [job: JobRecord];
	retry: [job: JobRecord];
}>();

function retryLabel(job: JobRecord): string {
	return job.state === 'Scheduled' ? `Run ${job.jobName} now` : `Retry ${job.jobName}`;
}
</script>

<template>
	<div class="table-card">
		<FeedbackState v-if="rows.length === 0" title="Nothing to show yet" description="Jobs will appear here as they are scheduled." />
		<div v-else class="table-scroll">
			<table>
				<thead>
					<tr>
						<th>Job</th>
						<th>Queue</th>
						<th>Group</th>
						<th>Batch</th>
						<th>State</th>
						<th>Attempt</th>
						<th>Due</th>
						<th>Completed</th>
						<th><span class="sr-only">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					<tr v-for="job in rows" :key="job.jobId" :data-job-id="job.jobId">
						<td>
							<button
								class="table-link max-w-72 truncate"
								type="button"
								:title="job.jobName"
								@click="emit('select', job)"
							>
								{{ job.jobName }}
							</button>
						</td>
						<td><code>{{ job.queueName }}</code></td>
						<td>
							<code v-if="job.groupId !== null" class="block max-w-48 truncate" :title="job.groupId">{{ job.groupId }}</code>
							<span v-else class="text-muted">—</span>
						</td>
						<td>
							<button v-if="job.batchId" class="table-link" type="button" @click="emit('openBatch', job.batchId)">
								<Layers3 :size="13" aria-hidden="true" />
								{{ job.batchId }}
							</button>
							<span v-else class="text-muted">—</span>
						</td>
						<td><StateBadge :state="job.state" /></td>
						<td class="numeric">{{ job.attempt }}</td>
						<td>{{ formatDate(job.dueAt) }}</td>
						<td>{{ formatDate(job.completedAt) }}</td>
						<td>
							<div class="row-actions">
								<button
									class="icon-button"
									type="button"
									:aria-label="`View ${job.jobName}`"
									@click="emit('select', job)"
								>
									<Eye :size="15" aria-hidden="true" />
								</button>
								<button
									v-if="canRetryJob(job)"
									class="icon-button"
									type="button"
									:disabled="busyJobId === job.jobId"
									:aria-label="retryLabel(job)"
									@click="emit('retry', job)"
								>
									<RotateCcw :size="15" aria-hidden="true" />
								</button>
								<button
									v-if="canCancelJob(job)"
									class="icon-button danger"
									type="button"
									:disabled="busyJobId === job.jobId"
									:aria-label="`Cancel ${job.jobName}`"
									@click="emit('cancel', job)"
								>
									<Ban :size="15" aria-hidden="true" />
								</button>
							</div>
						</td>
					</tr>
				</tbody>
			</table>
		</div>
	</div>
</template>
