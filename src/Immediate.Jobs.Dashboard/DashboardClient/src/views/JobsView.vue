<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useRouteQuery } from '@vueuse/router';
import { refDebounced } from '@vueuse/core';
import { ChevronLeft, ChevronRight, Search } from '@lucide/vue';

import ConfirmDialog from '@/components/ConfirmDialog.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import JobTable from '@/components/JobTable.vue';
import PageHeader from '@/components/PageHeader.vue';
import { jobStates, type JobFilters, type JobRecord, type JobState } from '@/contracts';
import { errorText } from '@/notifications';
import { useJobsQuery } from '@/query';
import { useJobMutations } from '@/use-dashboard-mutations';

const route = useRoute();
const router = useRouter();
const searchQuery = useRouteQuery<string>('search', '');
const queueQuery = useRouteQuery<string>('queue', '');
const stateQuery = useRouteQuery<string>('state', '');
const pageQuery = useRouteQuery<string>('page', '1');
const debouncedSearch = refDebounced(searchQuery, 250);
const debouncedQueue = refDebounced(queueQuery, 250);
const cancelCandidate = ref<JobRecord>();

const selectedState = computed<JobState | ''>(() => {
	return jobStates.includes(stateQuery.value as JobState) ? stateQuery.value as JobState : '';
});
const page = computed(() => {
	const value = Number.parseInt(pageQuery.value, 10);
	return Number.isSafeInteger(value) && value > 0 ? value : 1;
});
const filters = computed<JobFilters>(() => ({
	search: debouncedSearch.value.trim(),
	queue: debouncedQueue.value.trim(),
	state: selectedState.value,
	page: page.value,
}));

const jobsQuery = useJobsQuery(filters);
const jobMutations = useJobMutations();

watch([searchQuery, queueQuery, stateQuery], () => {
	pageQuery.value = '1';
});
watch(stateQuery, (value) => {
	if (value && !jobStates.includes(value as JobState)) {
		stateQuery.value = '';
	}
}, { immediate: true });
watch(pageQuery, (value) => {
	if (value !== String(page.value)) {
		pageQuery.value = String(page.value);
	}
}, { immediate: true });

function openJob(job: JobRecord): void {
	void router.push({
		name: 'job-detail',
		params: { jobHandle: job.jobHandle },
		query: route.query,
	});
}

function openBatch(batchHandle: string): void {
	void router.push({ name: 'batch-detail', params: { batchHandle } });
}

function changePage(nextPage: number): void {
	pageQuery.value = String(Math.max(1, nextPage));
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
			title="Jobs"
			description="Search and inspect invocation history across every queue."
			:meta="`${jobsQuery.data.value?.items.length ?? 0} shown`"
		/>

		<div class="filter-bar">
			<label class="search-field">
				<Search :size="16" aria-hidden="true" />
				<span class="sr-only">Search by job name</span>
				<input v-model="searchQuery" type="search" placeholder="Search job name" />
			</label>
			<label>
				<span class="sr-only">Filter by queue</span>
				<input v-model="queueQuery" type="search" placeholder="Queue name" />
			</label>
			<label>
				<span class="sr-only">Filter by state</span>
				<select v-model="stateQuery">
					<option value="">All states</option>
					<option v-for="state in jobStates" :key="state" :value="state">{{ state }}</option>
				</select>
			</label>
		</div>

		<FeedbackState v-if="jobsQuery.error.value && !jobsQuery.data.value" type="error" title="Jobs could not be loaded" :description="errorText(jobsQuery.error.value)" />
		<FeedbackState v-else-if="jobsQuery.isPending.value" type="loading" title="Loading jobs" />
		<div v-else>
			<JobTable
				:rows="jobsQuery.data.value?.items ?? []"
				:busy-job-id="jobMutations.busyJobHandle.value"
				@select="openJob"
				@open-batch="openBatch"
				@cancel="cancelCandidate = $event"
				@retry="(job) => jobMutations.retryJob(job.jobHandle)"
			/>
			<div class="pagination" aria-label="Jobs pagination">
				<button class="button button-secondary" type="button" :disabled="page === 1" @click="changePage(page - 1)">
					<ChevronLeft :size="15" aria-hidden="true" />
					Previous
				</button>
				<span>Page {{ page }}</span>
				<button class="button button-secondary" type="button" :disabled="!jobsQuery.data.value?.hasNext" @click="changePage(page + 1)">
					Next
					<ChevronRight :size="15" aria-hidden="true" />
				</button>
			</div>
		</div>

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
