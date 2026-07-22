<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{
	label: string;
	value: number;
}>();

const formattedValue = computed(() => props.value.toLocaleString());
</script>

<template>
	<article class="metric-card" :data-state="label.toLowerCase()">
		<div class="metric-label">
			<span aria-hidden="true"></span>
			{{ label }}
		</div>
		<strong :key="value">{{ formattedValue }}</strong>
	</article>
</template>
