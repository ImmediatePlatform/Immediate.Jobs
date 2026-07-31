<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import { Activity, Ban, Check, ChevronDown, Copy, ExternalLink, RotateCcw, ScrollText, X } from '@lucide/vue';

import StateBadge from '@/components/StateBadge.vue';
import { getJobExecutions, getJobExecutionTelemetryLinks } from '@/api';
import {
	canCancelJob,
	canRetryJob,
	type JobExecutionRecord,
	type JobRecord,
	type JobTelemetryLink,
} from '@/contracts';
import { formatDate, formatJson } from '@/format';

const props = withDefaults(defineProps<{
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
	cancel: [job: JobRecord];
	close: [];
	retry: [job: JobRecord];
}>();

const executions = ref<JobExecutionRecord[]>([]);
const executionLinks = ref<Record<number, JobTelemetryLink[]>>({});
const hasOlderExecutions = ref(false);
const executionsLoading = ref(false);
const olderExecutionsLoading = ref(false);
const executionsError = ref<string>();
const copiedExecutionIdentifier = ref<string>();
let executionRequest: AbortController | undefined;
let copyResetTimer: number | undefined;
const executionPageSize = 20;

watch(
	[() => props.job.id, () => props.job.attempt, () => props.job.state],
	() => void loadExecutions(),
	{ immediate: true },
);

onBeforeUnmount(() => {
	executionRequest?.abort();
	if (copyResetTimer !== undefined) {
		window.clearTimeout(copyResetTimer);
	}
});

async function loadExecutions(): Promise<void> {
	executionRequest?.abort();
	const request = new AbortController();
	executionRequest = request;
	executionsLoading.value = true;
	executionsError.value = undefined;
	try {
		const page = await getJobExecutions(props.job.id, 0, executionPageSize, request.signal);
		executions.value = page.items;
		executionLinks.value = await loadExecutionLinks(page.items, request.signal);
		hasOlderExecutions.value = page.hasNext;
	} catch (error) {
		if (error instanceof DOMException && error.name === 'AbortError') {
			return;
		}
		executionsError.value = error instanceof Error ? error.message : 'Execution history could not be loaded.';
	} finally {
		if (executionRequest === request) {
			executionsLoading.value = false;
		}
	}
}

async function showOlderExecutions(): Promise<void> {
	if (!hasOlderExecutions.value || olderExecutionsLoading.value) {
		return;
	}
	executionRequest ??= new AbortController();
	olderExecutionsLoading.value = true;
	try {
		const page = await getJobExecutions(
			props.job.id,
			executions.value.length,
			executionPageSize,
			executionRequest.signal,
		);
		executions.value.push(...page.items);
		Object.assign(executionLinks.value, await loadExecutionLinks(page.items, executionRequest.signal));
		hasOlderExecutions.value = page.hasNext;
	} catch (error) {
		if (!(error instanceof DOMException && error.name === 'AbortError')) {
			executionsError.value = error instanceof Error ? error.message : 'Older executions could not be loaded.';
		}
	} finally {
		olderExecutionsLoading.value = false;
	}
}

async function loadExecutionLinks(
	items: JobExecutionRecord[],
	signal: AbortSignal,
): Promise<Record<number, JobTelemetryLink[]>> {
	const results = await Promise.allSettled(items.map(async execution => [
		execution.attempt,
		await getJobExecutionTelemetryLinks(execution.jobId, execution.attempt, signal),
	] as const));
	return Object.fromEntries(results
		.filter((result): result is PromiseFulfilledResult<readonly [number, JobTelemetryLink[]]> =>
			result.status === 'fulfilled')
		.map(result => result.value));
}

function executionDuration(execution: JobExecutionRecord): string | undefined {
	if (!execution.executionStartedAt || !execution.completedAt) {
		return undefined;
	}
	const milliseconds = Date.parse(execution.completedAt) - Date.parse(execution.executionStartedAt);
	if (!Number.isFinite(milliseconds) || milliseconds < 0) {
		return undefined;
	}
	if (milliseconds < 1_000) {
		return `${milliseconds} ms`;
	}
	return `${(milliseconds / 1_000).toFixed(milliseconds < 10_000 ? 2 : 1)} s`;
}

function executionIdentifierKey(attempt: number, kind: 'trace' | 'span'): string {
	return `${attempt}:${kind}`;
}

async function copyExecutionIdentifier(attempt: number, kind: 'trace' | 'span', value: string): Promise<void> {
	if (!navigator.clipboard) {
		return;
	}
	try {
		await navigator.clipboard.writeText(value);
	} catch {
		return;
	}
	copiedExecutionIdentifier.value = executionIdentifierKey(attempt, kind);
	if (copyResetTimer !== undefined) {
		window.clearTimeout(copyResetTimer);
	}
	copyResetTimer = window.setTimeout(() => {
		copiedExecutionIdentifier.value = undefined;
		copyResetTimer = undefined;
	}, 2_000);
}

function retryButtonLabel(job: JobRecord, pending: boolean): string {
	if (pending) {
		return 'Retrying…';
	}
	return job.state === 'Scheduled' ? 'Run now' : 'Retry job';
}
</script>

<template>
	<article class="inspector" :aria-label="`Details for ${job.jobName}`">
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
				<div class="flex items-center gap-2">
					<button
						v-if="canCancelJob(job)"
						class="button button-secondary danger-text"
						type="button"
						:disabled="pending"
						@click="emit('cancel', job)"
					>
						<Ban :size="14" aria-hidden="true" />
						Cancel job
					</button>
					<button
						v-if="canRetryJob(job)"
						class="button button-secondary"
						type="button"
						:disabled="pending"
						@click="emit('retry', job)"
					>
						<RotateCcw :size="14" aria-hidden="true" />
						{{ retryButtonLabel(job, pending) }}
					</button>
				</div>
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
				<div v-if="job.groupId !== null">
					<dt>Group</dt>
					<dd><code>{{ job.groupId }}</code></dd>
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

			<section v-if="telemetryLinks.length > 0" class="code-section">
				<h3>Job telemetry</h3>
				<div class="telemetry-links">
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
						{{ link.label }}
						<ExternalLink :size="11" aria-hidden="true" />
					</a>
				</div>
			</section>

			<section class="execution-history">
				<div class="execution-history-header">
					<div>
						<h3>Executions</h3>
						<p>Newest execution first</p>
					</div>
				</div>
				<p v-if="executionsLoading" class="execution-empty">Loading executions…</p>
				<p v-else-if="executionsError && executions.length === 0" class="execution-empty error-text">
					{{ executionsError }}
				</p>
				<p v-else-if="executions.length === 0" class="execution-empty">Not executed yet.</p>
				<div v-else class="execution-list">
					<details
						v-for="(execution, index) in executions"
						:key="execution.attempt"
						class="execution-card"
						:open="index === 0"
					>
						<summary class="execution-card-summary">
							<div class="flex items-center gap-2">
								<strong>Attempt {{ execution.attempt }}</strong>
								<StateBadge :state="execution.state" />
								<span v-if="execution.isSynthetic" class="imported-badge">Imported</span>
							</div>
							<div class="execution-card-summary-meta">
								<span v-if="executionDuration(execution)">{{ executionDuration(execution) }}</span>
								<ChevronDown class="execution-card-chevron" :size="14" aria-hidden="true" />
							</div>
						</summary>
						<div class="execution-card-content">
							<dl>
								<div v-if="execution.workerId"><dt>Worker</dt><dd><code>{{ execution.workerId }}</code></dd></div>
								<div><dt>Acquired</dt><dd>{{ formatDate(execution.acquiredAt) }}</dd></div>
								<div><dt>Started</dt><dd>{{ formatDate(execution.executionStartedAt) }}</dd></div>
								<div><dt>Completed</dt><dd>{{ formatDate(execution.completedAt) }}</dd></div>
								<div v-if="execution.executionTraceId">
									<dt>Trace ID</dt>
									<dd>
										<button
											class="execution-copy"
											type="button"
											:aria-label="`${copiedExecutionIdentifier === executionIdentifierKey(execution.attempt, 'trace') ? 'Copied' : 'Copy'} trace ID for attempt ${execution.attempt}`"
											:title="copiedExecutionIdentifier === executionIdentifierKey(execution.attempt, 'trace') ? 'Copied' : 'Copy trace ID'"
											@click="copyExecutionIdentifier(execution.attempt, 'trace', execution.executionTraceId)"
										>
											<code>{{ execution.executionTraceId }}</code>
											<Check
												v-if="copiedExecutionIdentifier === executionIdentifierKey(execution.attempt, 'trace')"
												:size="12"
												aria-hidden="true"
											/>
											<Copy v-else :size="12" aria-hidden="true" />
										</button>
									</dd>
								</div>
								<div v-if="execution.executionSpanId">
									<dt>Span ID</dt>
									<dd>
										<button
											class="execution-copy"
											type="button"
											:aria-label="`${copiedExecutionIdentifier === executionIdentifierKey(execution.attempt, 'span') ? 'Copied' : 'Copy'} span ID for attempt ${execution.attempt}`"
											:title="copiedExecutionIdentifier === executionIdentifierKey(execution.attempt, 'span') ? 'Copied' : 'Copy span ID'"
											@click="copyExecutionIdentifier(execution.attempt, 'span', execution.executionSpanId)"
										>
											<code>{{ execution.executionSpanId }}</code>
											<Check
												v-if="copiedExecutionIdentifier === executionIdentifierKey(execution.attempt, 'span')"
												:size="12"
												aria-hidden="true"
											/>
											<Copy v-else :size="12" aria-hidden="true" />
										</button>
									</dd>
								</div>
							</dl>
							<div v-if="executionLinks[execution.attempt]?.length" class="telemetry-links">
								<a
									v-for="link in executionLinks[execution.attempt]"
									:key="`${link.kind}-${link.label}`"
									class="button button-secondary"
									:href="link.url"
									target="_blank"
									rel="noopener noreferrer"
								>
									<Activity v-if="link.kind === 'Trace'" :size="12" aria-hidden="true" />
									<ScrollText v-else :size="12" aria-hidden="true" />
									{{ link.label }}
									<ExternalLink :size="11" aria-hidden="true" />
								</a>
							</div>
							<details v-if="execution.error" class="execution-error">
								<summary>Failure details</summary>
								<pre>{{ execution.error }}</pre>
							</details>
						</div>
					</details>
				</div>
				<p v-if="executions.some(execution => execution.isSynthetic && execution.attempt > 1)" class="execution-note">
					Earlier pre-upgrade executions were not recorded.
				</p>
				<p v-if="executionsError && executions.length > 0" class="execution-note error-text">{{ executionsError }}</p>
				<button
					v-if="hasOlderExecutions"
					class="button button-secondary"
					type="button"
					:disabled="olderExecutionsLoading"
					@click="showOlderExecutions"
				>
					{{ olderExecutionsLoading ? 'Loading…' : 'Show older executions' }}
				</button>
			</section>

			<section class="code-section">
				<h3>Payload</h3>
				<pre>{{ formatJson(job.payload) }}</pre>
			</section>
			<section v-if="job.context" class="code-section">
				<h3>Context envelope</h3>
				<pre>{{ formatJson(job.context) }}</pre>
			</section>
		</div>
	</article>
</template>
