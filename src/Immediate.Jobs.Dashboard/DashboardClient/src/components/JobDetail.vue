<script setup lang="ts">
import { Activity, ExternalLink, RotateCcw, ScrollText, X } from '@lucide/vue';

import StateBadge from '@/components/StateBadge.vue';
import type { JobRecord, JobTelemetryLink } from '@/contracts';
import { formatDate, formatJson } from '@/format';

withDefaults(defineProps<{
	job: JobRecord;
	telemetryLinks?: JobTelemetryLink[];
	pending?: boolean;
	showClose?: boolean;
}>(), {
	telemetryLinks: () => [],
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
				<div v-if="job.executionStartedAt">
					<dt>Attempt started</dt>
					<dd>{{ formatDate(job.executionStartedAt) }}</dd>
				</div>
				<div v-if="job.executionTraceId || telemetryLinks.length > 0">
					<dt>Execution trace</dt>
					<dd v-if="job.executionTraceId"><code>{{ job.executionTraceId }}</code></dd>
					<div v-if="telemetryLinks.length > 0" class="telemetry-links">
						<a
							v-for="link in telemetryLinks"
							:key="`${link.kind}-${link.label}`"
							class="button button-secondary"
							:href="link.url"
							target="_blank"
							rel="noopener noreferrer"
							:aria-label="link.label"
							:title="link.label"
						>
							<Activity v-if="link.kind === 'Trace'" :size="12" aria-hidden="true" />
							<ScrollText v-else :size="12" aria-hidden="true" />
							{{ link.kind }}
							<ExternalLink :size="11" aria-hidden="true" />
						</a>
					</div>
				</div>
				<div v-if="job.executionSpanId">
					<dt>Execution span</dt>
					<dd><code>{{ job.executionSpanId }}</code></dd>
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
