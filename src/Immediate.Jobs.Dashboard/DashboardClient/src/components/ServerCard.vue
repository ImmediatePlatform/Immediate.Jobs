<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink } from 'vue-router';
import { Cpu, Radio, Server } from '@lucide/vue';

import type { JobServerSnapshot, JobWorkerSnapshot } from '@/contracts';
import { formatDate } from '@/format';

const props = defineProps<{
	server: JobServerSnapshot;
}>();

const workers = computed(() => [...props.server.workers].sort((left, right) => left.workerId - right.workerId));
const busyWorkers = computed(() => workers.value.filter(worker => worker.jobHandle));

function jobRoute(jobHandle: string) {
	return { name: 'job-detail', params: { jobHandle } };
}

function slotLabel(worker: JobWorkerSnapshot): string {
	return worker.jobHandle
		? `Worker #${worker.workerId}: ${worker.jobHandle}`
		: `Worker #${worker.workerId}: idle`;
}
</script>

<template>
	<article class="panel server-card">
		<header>
			<span class="server-icon"><Server :size="18" aria-hidden="true" /></span>
			<div class="min-w-0">
				<span class="eyebrow">Worker</span>
				<code class="block truncate" :title="server.workerId">{{ server.workerId }}</code>
			</div>
		</header>
		<div class="server-tags" aria-label="Server tags"><span v-for="tag in server.tags" :key="tag" class="tag">{{ tag }}</span></div>
		<dl>
			<div>
				<dt><Radio :size="14" aria-hidden="true" /> Last heartbeat</dt>
				<dd>{{ formatDate(server.lastHeartbeat) }}</dd>
			</div>
			<div>
				<dt><Cpu :size="14" aria-hidden="true" /> Active workers</dt>
				<dd>{{ server.activeWorkers }} <span>/ {{ server.maxWorkers }}</span></dd>
			</div>
		</dl>
		<template v-if="workers.length">
			<ul class="server-slots" aria-label="Worker slots">
				<li v-for="worker in workers" :key="worker.workerId">
					<RouterLink
						v-if="worker.jobHandle"
						class="server-slot busy"
						:to="jobRoute(worker.jobHandle)"
						:title="slotLabel(worker)"
						:aria-label="slotLabel(worker)"
					/>
					<span v-else class="server-slot" :title="slotLabel(worker)" role="img" :aria-label="slotLabel(worker)" />
				</li>
			</ul>
			<ol v-if="busyWorkers.length" class="server-workers" aria-label="Busy workers">
				<li v-for="worker in busyWorkers" :key="worker.workerId">
					<span class="server-worker-id">#{{ worker.workerId }}</span>
					<RouterLink
						v-if="worker.jobHandle"
						class="server-worker-job"
						:to="jobRoute(worker.jobHandle)"
						:title="worker.jobHandle"
					>
						<code>{{ worker.jobHandle }}</code>
					</RouterLink>
					<span class="server-worker-meta">
						<template v-if="worker.attempt !== null">Attempt {{ worker.attempt }} · </template>{{ formatDate(worker.startedAt) }}
					</span>
				</li>
			</ol>
			<p v-else class="server-workers-idle">All workers idle</p>
		</template>
	</article>
</template>
