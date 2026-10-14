<script setup lang="ts">
import { computed, ref } from 'vue';
import { Search } from '@lucide/vue';

import DefinitionTable from '@/components/DefinitionTable.vue';
import FeedbackState from '@/components/FeedbackState.vue';
import PageHeader from '@/components/PageHeader.vue';
import { errorText } from '@/notifications';
import { useDefinitionStatusesQuery } from '@/query';
import { useDefinitionMutations } from '@/use-dashboard-mutations';

const definitionsQuery = useDefinitionStatusesQuery();
const definitionMutations = useDefinitionMutations();
const definitions = computed(() => definitionsQuery.data.value ?? []);
const search = ref('');
const filteredDefinitions = computed(() => {
	const term = search.value.trim().toLowerCase();
	return term
		? definitions.value.filter(definition => [definition.name, definition.queueName, ...definition.tags]
			.some(value => value.toLowerCase().includes(term)))
			: definitions.value;
});
</script>

<template>
	<section>
		<PageHeader
			title="Definitions"
			description="Pause pending and future jobs while running work finishes. Resume remains subject to configured limits."
			:meta="search.trim() ? `${filteredDefinitions.length} of ${definitions.length} shown` : `${definitions.length} stored`"
		/>
		<div class="filter-bar">
			<label class="search-field">
				<Search :size="16" aria-hidden="true" />
				<span class="sr-only">Search definitions</span>
				<input v-model="search" type="search" placeholder="Search definitions, queues, or tags" />
			</label>
		</div>
		<FeedbackState v-if="definitionsQuery.error.value" type="error" title="Definitions could not be loaded" :description="errorText(definitionsQuery.error.value)" />
		<FeedbackState v-else-if="definitionsQuery.isPending.value" type="loading" title="Loading definitions" />
		<FeedbackState v-else-if="definitions.length > 0 && filteredDefinitions.length === 0" title="No matching definitions" description="Try another definition name, queue, or tag." />
		<DefinitionTable
			v-else
			:definitions="filteredDefinitions"
			:busy-name="definitionMutations.busyName.value"
			@set-paused="definitionMutations.setPaused"
		/>
	</section>
</template>
