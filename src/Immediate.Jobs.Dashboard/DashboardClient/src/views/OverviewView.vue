<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRouter } from 'vue-router';

import ConfirmDialog from '@/components/ConfirmDialog.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import HistoryChart from '@/components/HistoryChart.vue';
import JobStateSummary from '@/components/JobStateSummary.vue';
import JobTable from '@/components/JobTable.vue';
import PageHeader from '@/components/PageHeader.vue';
import type { JobRecord } from '@/contracts';
import { formatDate } from '@/format';
import { errorText } from '@/notifications';
import { useOverviewQuery, useRecentJobsQuery } from '@/query';
import { dashboardHistory } from '@/stream-state';
import { useJobMutations } from '@/use-dashboard-mutations';

const router = useRouter();
const overviewQuery = useOverviewQuery();
const recentJobsQuery = useRecentJobsQuery();
const jobMutations = useJobMutations();
const cancelCandidate = ref<JobRecord>();

const snapshot = computed(() => overviewQuery.data.value);
const error = computed(() => overviewQuery.error.value ?? recentJobsQuery.error.value);

watch(snapshot, (value) => {
	if (value && dashboardHistory.value.length === 0) {
		const complete = (value.counts.Succeeded ?? 0) + (value.counts.Failed ?? 0)
			+ (value.counts.Cancelled ?? 0) + (value.counts.Skipped ?? 0);
		dashboardHistory.value = [{
			capturedAt: value.capturedAt,
			complete,
			throughput: 0,
			queued: (value.counts.Pending ?? 0) + (value.counts.Scheduled ?? 0),
		}];
	}
}, { immediate: true });

function openJob(job: JobRecord): void {
	void router.push({ name: 'job-detail', params: { jobHandle: job.jobHandle } });
}

function openBatch(batchHandle: string): void {
	void router.push({ name: 'batch-detail', params: { batchHandle } });
}

async function confirmCancel(): Promise<void> {
	if (!cancelCandidate.value) {
		return;
	}
	try {
		await jobMutations.cancelJob(cancelCandidate.value.jobHandle);
		cancelCandidate.value = undefined;
	} catch {
		// The mutation displays the API error and leaves the confirmation open.
	}
}
</script>

<template>
	<section>
		<PageHeader
			title="Overview"
			description="A live view of queues, throughput, and recent work."
			:meta="snapshot ? `Captured ${formatDate(snapshot.capturedAt)}` : undefined"
		/>

		<FeedbackState v-if="error" type="error" title="Dashboard data is unavailable" :description="errorText(error)" />
		<FeedbackState v-else-if="!snapshot" type="loading" title="Loading dashboard" />
		<template v-else>
			<JobStateSummary :counts="snapshot.counts" />

			<div class="mt-4 grid gap-4 xl:grid-cols-2">
				<article class="panel chart-card">
					<div>
						<span class="eyebrow">Throughput</span>
						<strong>Completed per update</strong>
					</div>
					<HistoryChart :points="dashboardHistory" value-key="throughput" label="Throughput" />
				</article>
				<article class="panel chart-card">
					<div>
						<span class="eyebrow">Queue depth</span>
						<strong>Pending and scheduled</strong>
					</div>
					<HistoryChart :points="dashboardHistory" value-key="queued" label="Queue depth" color="var(--color-info)" />
				</article>
			</div>

			<div class="section-heading">
				<div>
					<h2>Recent jobs</h2>
					<p>The latest invocations across every queue.</p>
				</div>
				<span>{{ recentJobsQuery.data.value?.length ?? 0 }} shown</span>
			</div>
			<JobTable
				:rows="recentJobsQuery.data.value ?? []"
				:busy-job-id="jobMutations.busyJobHandle.value"
				@select="openJob"
				@open-batch="openBatch"
				@cancel="cancelCandidate = $event"
				@retry="(job) => jobMutations.retryJob(job.jobHandle)"
			/>
		</template>

		<ConfirmDialog
			:open="Boolean(cancelCandidate)"
			title="Cancel job?"
			:description="`This marks ${cancelCandidate?.jobName ?? 'this job'} as cancelled. Work already running may finish its current attempt.`"
			confirm-label="Cancel job"
			:pending="jobMutations.cancelling.value"
			@cancel="cancelCandidate = undefined"
			@confirm="confirmCancel"
		/>
	</section>
</template>
