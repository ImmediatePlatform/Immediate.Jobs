<script setup lang="ts">
import { computed } from 'vue';
import { Cpu, Radio, Server } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import PageHeader from '@/components/PageHeader.vue';
import { formatDate } from '@/format';
import { errorText } from '@/notifications';
import { useServersQuery } from '@/query';

const serversQuery = useServersQuery();
const servers = computed(() => serversQuery.data.value ?? []);
</script>

<template>
	<section>
		<PageHeader
			title="Servers"
			description="Scheduler nodes currently reporting a heartbeat."
			:meta="`${servers.length} online`"
		/>

		<FeedbackState v-if="serversQuery.error.value" type="error" title="Servers could not be loaded" :description="errorText(serversQuery.error.value)" />
		<FeedbackState v-else-if="serversQuery.isPending.value" type="loading" title="Loading servers" />
		<div v-else-if="servers.length" class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
			<article v-for="server in servers" :key="server.workerId" class="panel server-card">
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
			</article>
		</div>
		<FeedbackState v-else title="No scheduler nodes" description="Nodes will appear after their first heartbeat." />
	</section>
</template>
