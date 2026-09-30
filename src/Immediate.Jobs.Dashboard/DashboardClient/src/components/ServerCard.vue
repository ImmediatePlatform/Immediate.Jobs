<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink } from 'vue-router';
import { Cpu, Radio, Server } from '@lucide/vue';

import type { JobServerSnapshot } from '@/contracts';
import { formatDate } from '@/format';

const props = defineProps<{
	server: JobServerSnapshot;
}>();

const workers = computed(() => [...props.server.workers].sort((left, right) => left.workerId - right.workerId));
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
		<ol v-if="workers.length" class="server-workers" aria-label="Workers">
			<li v-for="worker in workers" :key="worker.workerId" :class="{ busy: worker.jobHandle }">
				<span class="server-worker-id">#{{ worker.workerId }}</span>
				<template v-if="worker.jobHandle">
					<RouterLink
						class="server-worker-job"
						:to="{ name: 'job-detail', params: { jobHandle: worker.jobHandle } }"
						:title="worker.jobHandle"
					>
						<code>{{ worker.jobHandle }}</code>
					</RouterLink>
					<span class="server-worker-meta">
						<template v-if="worker.attempt !== null">Attempt {{ worker.attempt }} · </template>{{ formatDate(worker.startedAt) }}
					</span>
				</template>
				<span v-else class="server-worker-meta">Idle</span>
			</li>
		</ol>
	</article>
</template>
