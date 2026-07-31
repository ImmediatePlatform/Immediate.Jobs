<script setup lang="ts">
import { computed } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ArrowLeft } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import JobDetail from '@/components/JobDetail.vue';
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

function backToJobs(): void {
	void router.push({ name: 'jobs', query: route.query });
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
			@retry="(job) => jobMutations.retryJob(job.id)"
		/>
		<FeedbackState
			v-else
			title="Job not found"
			description="The requested job does not exist."
		/>
	</section>
</template>
