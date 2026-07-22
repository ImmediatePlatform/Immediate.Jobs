<script setup lang="ts">
defineProps<{
	state: string;
}>();
</script>

<template>
	<span class="state-badge" :data-state="state.toLowerCase()">
		<span aria-hidden="true"></span>
		{{ state }}
	</span>
</template>
