<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink } from 'vue-router';

import type { JobState } from '@/contracts';

const props = defineProps<{
	counts: Partial<Record<JobState, number>>;
}>();

const groups: { title: string; caption: string; states: JobState[] }[] = [
	{ title: 'Queue', caption: 'Waiting for a worker or running', states: ['Pending', 'Scheduled', 'Active'] },
	{ title: 'Waiting', caption: 'Paused until input arrives', states: ['AwaitingContinuation', 'AwaitingParameters'] },
	{ title: 'Finished', caption: 'Reached a terminal state', states: ['Succeeded', 'Failed', 'Cancelled', 'Skipped'] },
];

const summary = computed(() => groups.map((group) => {
	const rows = group.states.map(state => ({ state, label: stateLabel(state), value: props.counts[state] ?? 0 }));
	const total = rows.reduce((sum, row) => sum + row.value, 0);
	return { ...group, rows, total };
}));

function stateLabel(state: string): string {
	const words = state.replace(/([a-z\d])(?=[A-Z])/g, '$1 ');
	return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
}

function share(value: number, total: number): string {
	return total === 0 ? '0%' : `${(value / total) * 100}%`;
}
</script>

<template>
	<section class="panel state-summary" aria-label="Job states">
		<div v-for="group in summary" :key="group.title" class="state-group">
			<header>
				<span class="eyebrow">{{ group.title }}</span>
				<strong :key="group.total">{{ group.total.toLocaleString() }}</strong>
				<p>{{ group.caption }}</p>
			</header>

			<div class="state-group-bar" aria-hidden="true">
				<template v-if="group.total > 0">
					<span
						v-for="row in group.rows"
						:key="row.state"
						:data-state="row.state.toLowerCase()"
						:style="{ width: share(row.value, group.total) }"
					></span>
				</template>
			</div>

			<ul>
				<li v-for="row in group.rows" :key="row.state">
					<RouterLink
						class="state-row"
						:class="{ empty: row.value === 0 }"
						:data-state="row.state.toLowerCase()"
						:to="{ name: 'jobs', query: { state: row.state } }"
					>
						<span class="state-dot" aria-hidden="true"></span>
						<span class="state-row-label">{{ row.label }}</span>
						<span class="state-row-value">{{ row.value.toLocaleString() }}</span>
					</RouterLink>
				</li>
			</ul>
		</div>
	</section>
</template>
