<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ArrowLeft, Ban, Trash2 } from '@lucide/vue';

import ConfirmDialog from '@/components/ConfirmDialog.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import JobDetail from '@/components/JobDetail.vue';
import PageHeader from '@/components/PageHeader.vue';
import StateBadge from '@/components/StateBadge.vue';
import WorkflowGraph from '@/components/WorkflowGraph.vue';
import { formatDate } from '@/format';
import { errorText } from '@/notifications';
import { useBatchGraphQuery, useBatchQuery, useJobQuery } from '@/query';
import { useBatchMutations, useJobMutations } from '@/use-dashboard-mutations';
import { useBatchStream } from '@/use-dashboard-stream';

const route = useRoute();
const router = useRouter();
const pendingAction = ref<'cancel' | 'delete'>();
const graphJobDetail = ref<HTMLElement>();

const batchId = computed(() => {
	const value = route.params.batchId;
	return typeof value === 'string' ? value : undefined;
});
const selectedJobId = computed(() => {
	const value = route.params.jobId;
	return typeof value === 'string' ? value : undefined;
});
const batchQuery = useBatchQuery(batchId);
const graphQuery = useBatchGraphQuery(batchId);
const jobQuery = useJobQuery(selectedJobId);
const batchMutations = useBatchMutations();
const jobMutations = useJobMutations();
useBatchStream(batchId);

watch([selectedJobId, () => jobQuery.isPending.value], ([jobId, isPending]) => {
	if (jobId && !isPending) {
		void scrollGraphJobDetailIntoView();
	}
}, { immediate: true, flush: 'post' });

async function scrollGraphJobDetailIntoView(): Promise<void> {
	await nextTick();
	const detail = graphJobDetail.value;
	if (!detail) {
		return;
	}
	const availableHeight = window.innerHeight - 40;
	const block = detail.getBoundingClientRect().height <= availableHeight ? 'end' : 'start';
	detail.scrollIntoView({ behavior: 'smooth', block, inline: 'nearest' });
}

function backToBatches(): void {
	void router.push({ name: 'batches', query: route.query });
}

function openGraphJob(jobId: string): void {
	if (!batchId.value) {
		return;
	}
	if (selectedJobId.value === jobId) {
		void scrollGraphJobDetailIntoView();
		return;
	}
	void router.push({
		name: 'batch-job',
		params: { batchId: batchId.value, jobId },
		query: route.query,
	});
}

function closeGraphJob(): void {
	if (batchId.value) {
		void router.push({ name: 'batch-detail', params: { batchId: batchId.value }, query: route.query });
	}
}

async function confirmAction(): Promise<void> {
	if (!pendingAction.value || !batchId.value) {
		return;
	}
	try {
		if (pendingAction.value === 'cancel') {
			await batchMutations.cancelBatch(batchId.value);
			pendingAction.value = undefined;
		} else {
			await batchMutations.deleteBatch(batchId.value);
			backToBatches();
		}
	} catch {
		// The mutation displays the API error and leaves the confirmation open.
	}
}
</script>

<template>
	<section>
		<button class="button button-secondary batch-back" type="button" @click="backToBatches">
			<ArrowLeft :size="15" aria-hidden="true" />
			All batches
		</button>

		<PageHeader
			title="Batch workflow"
			description="Inspect dependencies, execution progress, and individual invocations."
			:meta="batchQuery.data.value?.state"
		/>

		<FeedbackState
			v-if="batchQuery.error.value || graphQuery.error.value"
			type="error"
			title="Batch workflow could not be loaded"
			:description="errorText(batchQuery.error.value ?? graphQuery.error.value)"
		/>
		<FeedbackState
			v-else-if="batchQuery.isPending.value || graphQuery.isPending.value"
			type="loading"
			title="Loading batch workflow"
		/>
		<template v-else-if="batchQuery.data.value">
			<section class="batch-summary panel" :aria-label="`Summary for batch ${batchId}`">
				<div class="batch-summary-heading">
					<div class="min-w-0">
						<span class="eyebrow">Batch</span>
						<h2 class="truncate" :title="batchId"><code>{{ batchId }}</code></h2>
					</div>
					<div class="batch-summary-actions">
						<StateBadge :state="batchQuery.data.value.state" />
						<button
							v-if="batchQuery.data.value.state === 'Executing'"
							class="button button-secondary danger-text"
							type="button"
							:disabled="batchMutations.mutating.value"
							@click="pendingAction = 'cancel'"
						>
							<Ban :size="14" aria-hidden="true" />
							Cancel batch
						</button>
						<button
							v-else
							class="button button-secondary danger-text"
							type="button"
							:disabled="batchMutations.mutating.value"
							@click="pendingAction = 'delete'"
						>
							<Trash2 :size="14" aria-hidden="true" />
							Delete batch
						</button>
					</div>
				</div>
				<div class="batch-summary-progress">
					<span :style="{ width: `${batchQuery.data.value.fractionSettled * 100}%` }" />
				</div>
				<dl class="batch-summary-metrics">
					<div><dt>Members</dt><dd>{{ batchQuery.data.value.total }}</dd></div>
					<div><dt>Settled</dt><dd>{{ batchQuery.data.value.total - batchQuery.data.value.remaining }}</dd></div>
					<div><dt>Created</dt><dd>{{ formatDate(batchQuery.data.value.createdAt) }}</dd></div>
					<div><dt>Completed</dt><dd>{{ formatDate(batchQuery.data.value.completedAt) }}</dd></div>
				</dl>
			</section>

			<section class="workflow-section">
				<div class="section-heading">
					<div>
						<h2>Workflow</h2>
						<p>Select an invocation to inspect its payload and execution details.</p>
					</div>
				</div>
				<div class="workflow-stack">
					<WorkflowGraph :graph="graphQuery.data.value" @select="openGraphJob" />
					<div v-if="selectedJobId" ref="graphJobDetail" class="workflow-job-detail">
						<FeedbackState v-if="jobQuery.isPending.value" type="loading" title="Loading job details" />
						<FeedbackState
							v-else-if="jobQuery.error.value"
							type="error"
							title="Job details could not be loaded"
							:description="errorText(jobQuery.error.value)"
						/>
						<JobDetail
							v-else-if="jobQuery.data.value"
							:job="jobQuery.data.value"
							:pending="jobMutations.busyJobId.value === selectedJobId"
							@close="closeGraphJob"
							@retry="(job) => jobMutations.retryJob(job.id)"
						/>
					</div>
				</div>
			</section>
		</template>

		<ConfirmDialog
			:open="Boolean(pendingAction)"
			:title="pendingAction === 'cancel' ? 'Cancel executing batch?' : 'Delete settled batch?'"
			:description="pendingAction === 'cancel'
				? 'All non-terminal members will be cancelled. Work already running may finish its current attempt.'
				: 'This permanently removes the batch header, members, and dependency graph.'"
			:confirm-label="pendingAction === 'cancel' ? 'Cancel batch' : 'Delete batch'"
			:pending="batchMutations.mutating.value"
			@cancel="pendingAction = undefined"
			@confirm="confirmAction"
		/>
	</section>
</template>
