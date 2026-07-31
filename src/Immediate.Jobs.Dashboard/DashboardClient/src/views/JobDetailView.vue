<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ArrowLeft } from '@lucide/vue';

import ConfirmDialog from '@/components/ConfirmDialog.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import JobDetail from '@/components/JobDetail.vue';
import type { JobRecord } from '@/contracts';
import { errorText } from '@/notifications';
import { useJobQuery, useJobTelemetryLinksQuery } from '@/query';
import { useJobMutations } from '@/use-dashboard-mutations';

const route = useRoute();
const router = useRouter();
const jobId = computed(() => {
	const value = route.params.jobId;
	return typeof value === 'string' ? value : undefined;
});
const jobQuery = useJobQuery(jobId);
const telemetryLinksQuery = useJobTelemetryLinksQuery(jobId);
const jobMutations = useJobMutations();
const cancelCandidate = ref<JobRecord>();

function backToJobs(): void {
	void router.push({ name: 'jobs', query: route.query });
}

async function confirmCancel(): Promise<void> {
	if (!cancelCandidate.value) {
		return;
	}
	try {
		await jobMutations.cancelJob(cancelCandidate.value.id);
		cancelCandidate.value = undefined;
	} catch {
		// The mutation displays the API error and leaves the confirmation open.
	}
}
</script>

<template>
	<section>
		<button class="button button-secondary job-back" type="button" @click="backToJobs">
			<ArrowLeft :size="15" aria-hidden="true" />
			All jobs
		</button>

		<FeedbackState
			v-if="!jobId"
			title="Job not found"
			description="The requested job does not exist."
		/>
		<FeedbackState
			v-else-if="jobQuery.error.value"
			type="error"
			title="Job details could not be loaded"
			:description="errorText(jobQuery.error.value)"
		/>
		<FeedbackState v-else-if="jobQuery.isPending.value" type="loading" title="Loading job details" />
		<JobDetail
			v-else-if="jobQuery.data.value"
			:job="jobQuery.data.value"
			:telemetry-links="telemetryLinksQuery.data.value ?? []"
			:pending="jobMutations.busyJobId.value === jobId"
			:show-close="false"
			@cancel="cancelCandidate = $event"
			@retry="(job) => jobMutations.retryJob(job.id)"
		/>
		<FeedbackState
			v-else
			title="Job not found"
			description="The requested job does not exist."
		/>

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
