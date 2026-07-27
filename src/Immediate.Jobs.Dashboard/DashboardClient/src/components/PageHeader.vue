<script setup lang="ts">
defineProps<{
	title: string;
	description: string;
	meta?: string;
}>();
</script>

<template>
	<header class="page-header">
		<div>
			<h1>{{ title }}</h1>
			<p>{{ description }}</p>
		</div>
		<span v-if="meta" class="page-meta">{{ meta }}</span>
	</header>
</template>
