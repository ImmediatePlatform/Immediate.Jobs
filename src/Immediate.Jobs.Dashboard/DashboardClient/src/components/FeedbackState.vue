<script setup lang="ts">
import { AlertCircle, Inbox, LoaderCircle } from '@lucide/vue';

withDefaults(defineProps<{
	type?: 'loading' | 'empty' | 'error';
	title: string;
	description?: string;
}>(), {
	type: 'empty',
	description: '',
});
</script>

<template>
	<div class="feedback-state" :data-type="type" role="status">
		<LoaderCircle v-if="type === 'loading'" class="spin" :size="22" aria-hidden="true" />
		<AlertCircle v-else-if="type === 'error'" :size="22" aria-hidden="true" />
		<Inbox v-else :size="22" aria-hidden="true" />
		<strong>{{ title }}</strong>
		<span v-if="description">{{ description }}</span>
	</div>
</template>
