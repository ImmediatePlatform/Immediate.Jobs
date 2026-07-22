<script setup lang="ts">
import { RotateCcw, X } from '@lucide/vue';

import StateBadge from '@/components/StateBadge.vue';
import type { JobRecord } from '@/contracts';
import { formatDate, formatJson } from '@/format';

withDefaults(defineProps<{
	job: JobRecord;
	pending?: boolean;
	showClose?: boolean;
}>(), {
	pending: false,
	showClose: true,
});

const emit = defineEmits<{
	close: [];
	retry: [job: JobRecord];
}>();
</script>

<template>
	<aside class="inspector" :aria-label="`Details for ${job.jobName}`">
		<header class="inspector-header">
			<div class="min-w-0">
				<span class="eyebrow">Job details</span>
				<h2 class="truncate" :title="job.jobName">{{ job.jobName }}</h2>
			</div>
			<button v-if="showClose" class="icon-button" type="button" aria-label="Close job details" @click="emit('close')">
				<X :size="17" aria-hidden="true" />
			</button>
		</header>

		<div class="inspector-content">
			<div class="flex items-center justify-between gap-3">
				<StateBadge :state="job.state" />
				<button
					v-if="job.state === 'Failed'"
					class="button button-secondary"
					type="button"
					:disabled="pending"
					@click="emit('retry', job)"
				>
					<RotateCcw :size="14" aria-hidden="true" />
					{{ pending ? 'Retrying…' : 'Retry job' }}
				</button>
			</div>

			<dl class="detail-list">
				<div>
					<dt>Invocation</dt>
					<dd><code>{{ job.id }}</code></dd>
				</div>
				<div>
					<dt>Queue</dt>
					<dd><code>{{ job.queueName }}</code></dd>
				</div>
				<div v-if="job.batchId">
					<dt>Batch</dt>
					<dd><code>{{ job.batchId }}</code></dd>
				</div>
				<div>
					<dt>Attempt</dt>
					<dd>{{ job.attempt }}</dd>
				</div>
				<div>
					<dt>Created</dt>
					<dd>{{ formatDate(job.createdAt) }}</dd>
				</div>
				<div>
					<dt>Due</dt>
					<dd>{{ formatDate(job.dueAt) }}</dd>
				</div>
				<div>
					<dt>Completed</dt>
					<dd>{{ formatDate(job.completedAt) }}</dd>
				</div>
			</dl>

			<section class="code-section">
				<h3>Payload</h3>
				<pre>{{ formatJson(job.payload) }}</pre>
			</section>
			<section v-if="job.context" class="code-section">
				<h3>Context envelope</h3>
				<pre>{{ formatJson(job.context) }}</pre>
			</section>
			<section v-if="job.lastError" class="code-section error-code">
				<h3>Latest error</h3>
				<pre>{{ job.lastError }}</pre>
			</section>
		</div>
	</aside>
</template>
