<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { Eye, Layers3, RotateCcw, Trash2 } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import StateBadge from '@/components/StateBadge.vue';
import type { JobRecord } from '@/contracts';
import { formatDate } from '@/format';

const props = withDefaults(defineProps<{
	rows: JobRecord[];
	selectedId?: string;
	busyJobId?: string;
}>(), {
	selectedId: undefined,
	busyJobId: undefined,
});

const emit = defineEmits<{
	select: [job: JobRecord];
	openBatch: [batchId: string];
	retry: [job: JobRecord];
	delete: [job: JobRecord];
}>();

const tableCard = ref<HTMLElement>();
const selectedRowIsVisible = computed(() => props.rows.some((job) => job.id === props.selectedId));

async function scrollJobIntoView(jobId: string): Promise<void> {
	await nextTick();
	const jobRows = tableCard.value?.querySelectorAll<HTMLTableRowElement>('[data-job-id]') ?? [];
	const selectedRow = [...jobRows]
		.find((row) => row.dataset.jobId === jobId);
	const target = selectedRow ?? tableCard.value?.querySelector<HTMLElement>('.job-detail-row');
	target?.scrollIntoView({ behavior: 'smooth', block: 'start', inline: 'nearest' });
}

function selectJob(job: JobRecord): void {
	emit('select', job);
	if (props.selectedId === job.id) {
		void scrollJobIntoView(job.id);
	}
}

watch(() => props.selectedId, (jobId) => {
	if (jobId) {
		void scrollJobIntoView(jobId);
	}
}, { immediate: true });
</script>

<template>
	<div ref="tableCard" class="table-card">
		<FeedbackState v-if="rows.length === 0 && !selectedId" title="Nothing to show yet" description="Jobs will appear here as they are scheduled." />
		<div v-else class="table-scroll">
			<table>
				<thead>
					<tr>
						<th>Job</th>
						<th>Queue</th>
						<th>Batch</th>
						<th>State</th>
						<th>Attempt</th>
						<th>Due</th>
						<th>Completed</th>
						<th><span class="sr-only">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					<template v-for="job in rows" :key="job.id">
						<tr :data-job-id="job.id" :data-selected="selectedId === job.id">
							<td>
								<button
									class="table-link max-w-72 truncate"
									type="button"
									:title="job.jobName"
									:aria-controls="`job-details-${job.id}`"
									:aria-expanded="selectedId === job.id"
									@click="selectJob(job)"
								>
									{{ job.jobName }}
								</button>
							</td>
							<td><code>{{ job.queueName }}</code></td>
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
										:aria-controls="`job-details-${job.id}`"
										:aria-expanded="selectedId === job.id"
										@click="selectJob(job)"
									>
										<Eye :size="15" aria-hidden="true" />
									</button>
									<button
										v-if="job.state === 'Failed'"
										class="icon-button"
										type="button"
										:disabled="busyJobId === job.id"
										:aria-label="`Retry ${job.jobName}`"
										@click="emit('retry', job)"
									>
										<RotateCcw :size="15" aria-hidden="true" />
									</button>
									<button
										v-if="job.state === 'Failed'"
										class="icon-button danger"
										type="button"
										:disabled="busyJobId === job.id"
										:aria-label="`Delete ${job.jobName}`"
										@click="emit('delete', job)"
									>
										<Trash2 :size="15" aria-hidden="true" />
									</button>
								</div>
							</td>
						</tr>
						<tr v-if="selectedId === job.id" :id="`job-details-${job.id}`" class="job-detail-row">
							<td colspan="8"><slot name="details" /></td>
						</tr>
					</template>
					<tr v-if="selectedId && !selectedRowIsVisible" class="job-detail-row">
						<td colspan="8"><slot name="details" /></td>
					</tr>
				</tbody>
			</table>
		</div>
	</div>
</template>
