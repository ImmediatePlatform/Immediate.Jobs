<script setup lang="ts">
import { Ban, Eye, Trash2 } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import StateBadge from '@/components/StateBadge.vue';
import type { BatchStatus } from '@/contracts';
import { formatDate } from '@/format';

withDefaults(defineProps<{
	rows: BatchStatus[];
	selectedId?: string;
	busyBatchId?: string;
}>(), {
	selectedId: undefined,
	busyBatchId: undefined,
});

const emit = defineEmits<{
	select: [batch: BatchStatus];
	cancel: [batch: BatchStatus];
	delete: [batch: BatchStatus];
}>();

function width(value: number, total: number): string {
	return total ? `${value / total * 100}%` : '0%';
}
</script>

<template>
	<div class="table-card">
		<FeedbackState v-if="rows.length === 0" title="No batches yet" description="Committed workflows will appear here." />
		<div v-else class="table-scroll">
			<table>
				<thead>
					<tr>
						<th>Batch</th>
						<th>State</th>
						<th>Progress</th>
						<th>Members</th>
						<th>Created</th>
						<th><span class="sr-only">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					<tr v-for="batch in rows" :key="batch.id" :data-selected="selectedId === batch.id">
						<td>
							<button class="table-link max-w-64 truncate" type="button" :title="batch.id" @click="emit('select', batch)">
								{{ batch.id }}
							</button>
						</td>
						<td><StateBadge :state="batch.state" /></td>
						<td class="min-w-56">
							<div class="batch-progress" :aria-label="`${batch.total - batch.remaining} of ${batch.total} settled`">
								<span data-state="succeeded" :style="{ width: width(batch.succeeded, batch.total) }" />
								<span data-state="failed" :style="{ width: width(batch.failed, batch.total) }" />
								<span data-state="cancelled" :style="{ width: width(batch.cancelled, batch.total) }" />
								<span data-state="skipped" :style="{ width: width(batch.skipped, batch.total) }" />
								<span data-state="remaining" :style="{ width: width(batch.remaining, batch.total) }" />
							</div>
						</td>
						<td class="numeric">{{ batch.total - batch.remaining }} / {{ batch.total }}</td>
						<td>{{ formatDate(batch.createdAt) }}</td>
						<td>
							<div class="row-actions">
								<button class="icon-button" type="button" :aria-label="`View batch ${batch.id}`" @click="emit('select', batch)">
									<Eye :size="15" aria-hidden="true" />
								</button>
								<button
									v-if="batch.state === 'Executing'"
									class="icon-button danger"
									type="button"
									:disabled="busyBatchId === batch.id"
									:aria-label="`Cancel batch ${batch.id}`"
									@click="emit('cancel', batch)"
								>
									<Ban :size="15" aria-hidden="true" />
								</button>
								<button
									v-else
									class="icon-button danger"
									type="button"
									:disabled="busyBatchId === batch.id"
									:aria-label="`Delete batch ${batch.id}`"
									@click="emit('delete', batch)"
								>
									<Trash2 :size="15" aria-hidden="true" />
								</button>
							</div>
						</td>
					</tr>
				</tbody>
			</table>
		</div>
	</div>
</template>
