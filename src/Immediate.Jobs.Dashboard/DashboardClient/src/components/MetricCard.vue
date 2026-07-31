<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{
	label: string;
	value: number;
}>();

const formattedLabel = computed(() => {
	const words = props.label.replace(/([a-z\d])(?=[A-Z])/g, '$1 ');
	return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
});
const formattedValue = computed(() => props.value.toLocaleString());
</script>

<template>
	<article class="metric-card" :data-state="label.toLowerCase()">
		<div class="metric-label">
			<span aria-hidden="true"></span>
			{{ formattedLabel }}
		</div>
		<strong :key="value">{{ formattedValue }}</strong>
	</article>
</template>
