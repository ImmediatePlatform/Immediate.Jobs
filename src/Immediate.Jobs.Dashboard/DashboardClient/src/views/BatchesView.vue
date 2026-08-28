<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useRouteQuery } from '@vueuse/router';
import { Search } from '@lucide/vue';

import BatchTable from '@/components/BatchTable.vue';
import ConfirmDialog from '@/components/ConfirmDialog.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import PageHeader from '@/components/PageHeader.vue';
import { batchStates, type BatchState, type BatchStatus } from '@/contracts';
import { errorText } from '@/notifications';
import { useBatchesQuery } from '@/query';
import { useBatchMutations } from '@/use-dashboard-mutations';

interface BatchAction {
	type: 'cancel' | 'delete';
	batch: BatchStatus;
}

const route = useRoute();
const router = useRouter();
const searchQuery = useRouteQuery<string>('search', '');
const stateQuery = useRouteQuery<string>('state', '');
const pendingAction = ref<BatchAction>();

const selectedState = computed<BatchState | ''>(() => {
	return batchStates.includes(stateQuery.value as BatchState) ? stateQuery.value as BatchState : '';
});
const batchesQuery = useBatchesQuery();
const batchMutations = useBatchMutations();
const filteredBatches = computed(() => {
	const search = searchQuery.value.trim().toLocaleLowerCase();
	return (batchesQuery.data.value ?? []).filter((batch) => {
		return (!selectedState.value || batch.state === selectedState.value)
			&& (!search || batch.batchHandle.toLocaleLowerCase().includes(search));
	});
});

watch(stateQuery, (value) => {
	if (value && !batchStates.includes(value as BatchState)) {
		stateQuery.value = '';
	}
}, { immediate: true });

function openBatch(batch: BatchStatus): void {
	void router.push({ name: 'batch-detail', params: { batchHandle: batch.batchHandle }, query: route.query });
}

async function confirmAction(): Promise<void> {
	if (!pendingAction.value) {
		return;
	}
	const action = pendingAction.value;
	try {
		if (action.type === 'cancel') {
			await batchMutations.cancelBatch(action.batch.batchHandle);
		} else {
			await batchMutations.deleteBatch(action.batch.batchHandle);
		}
		pendingAction.value = undefined;
	} catch {
		// The mutation displays the API error and leaves the confirmation open.
	}
}
</script>

<template>
	<section>
		<PageHeader
			title="Batches"
			description="Inspect atomic workflows, dependencies, and aggregate progress."
			:meta="`${filteredBatches.length} shown`"
		/>

		<div class="filter-bar">
			<label class="search-field">
				<Search :size="16" aria-hidden="true" />
				<span class="sr-only">Search by batch ID</span>
				<input v-model="searchQuery" type="search" placeholder="Search batch ID" />
			</label>
			<label>
				<span class="sr-only">Filter by batch state</span>
				<select v-model="stateQuery">
					<option value="">All states</option>
					<option v-for="state in batchStates" :key="state" :value="state">{{ state }}</option>
				</select>
			</label>
		</div>

		<FeedbackState
			v-if="batchesQuery.error.value && !batchesQuery.data.value"
			type="error"
			title="Batches could not be loaded"
			:description="errorText(batchesQuery.error.value)"
		/>
		<FeedbackState v-else-if="batchesQuery.isPending.value" type="loading" title="Loading batches" />
		<BatchTable
			v-else
			:rows="filteredBatches"
			:busy-batch-id="batchMutations.busyBatchHandle.value"
			@select="openBatch"
			@cancel="pendingAction = { type: 'cancel', batch: $event }"
			@delete="pendingAction = { type: 'delete', batch: $event }"
		/>

		<ConfirmDialog
			:open="Boolean(pendingAction)"
			:title="pendingAction?.type === 'cancel' ? 'Cancel executing batch?' : 'Delete settled batch?'"
			:description="pendingAction?.type === 'cancel'
				? 'All non-terminal members will be cancelled. Work already running may finish its current attempt.'
				: 'This permanently removes the batch header, members, and dependency graph.'"
			:confirm-label="pendingAction?.type === 'cancel' ? 'Cancel batch' : 'Delete batch'"
			:pending="batchMutations.mutating.value"
			@cancel="pendingAction = undefined"
			@confirm="confirmAction"
		/>
	</section>
</template>
