<script setup lang="ts">
import { computed } from 'vue';

import FeedbackState from '@/components/FeedbackState.vue';
import PageHeader from '@/components/PageHeader.vue';
import ServerCard from '@/components/ServerCard.vue';
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
			<ServerCard v-for="server in servers" :key="server.workerId" :server="server" />
		</div>
		<FeedbackState v-else title="No scheduler nodes" description="Nodes will appear after their first heartbeat." />
	</section>
</template>
