<script setup lang="ts">
import { computed } from 'vue';

import DefinitionTable from '@/components/DefinitionTable.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import PageHeader from '@/components/PageHeader.vue';
import { errorText } from '@/notifications';
import { useDefinitionStatusesQuery } from '@/query';
import { useDefinitionMutations } from '@/use-dashboard-mutations';

const definitionsQuery = useDefinitionStatusesQuery();
const definitionMutations = useDefinitionMutations();
const definitions = computed(() => definitionsQuery.data.value ?? []);
</script>

<template>
	<section>
		<PageHeader
			title="Definitions"
			description="Pause pending and future jobs while running work finishes. Resume remains subject to configured limits."
			:meta="`${definitions.length} stored`"
		/>
		<FeedbackState v-if="definitionsQuery.error.value" type="error" title="Definitions could not be loaded" :description="errorText(definitionsQuery.error.value)" />
		<FeedbackState v-else-if="definitionsQuery.isPending.value" type="loading" title="Loading definitions" />
		<DefinitionTable
			v-else
			:definitions="definitions"
			:busy-name="definitionMutations.busyName.value"
			@set-paused="definitionMutations.setPaused"
		/>
	</section>
</template>
