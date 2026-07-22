<script setup lang="ts">
import { nextTick, ref, watch } from 'vue';
import { AlertTriangle, LoaderCircle } from '@lucide/vue';

const props = withDefaults(defineProps<{
	open: boolean;
	title: string;
	description: string;
	confirmLabel: string;
	pending?: boolean;
}>(), {
	pending: false,
});

const emit = defineEmits<{
	cancel: [];
	confirm: [];
}>();

const dialog = ref<HTMLDialogElement>();

watch(
	() => props.open,
	async (open) => {
		await nextTick();
		if (open && !dialog.value?.open) {
			dialog.value?.showModal();
		} else if (!open && dialog.value?.open) {
			dialog.value.close();
		}
	},
	{ immediate: true },
);

function cancel(): void {
	if (!props.pending) {
		emit('cancel');
	}
}
</script>

<template>
	<dialog
		ref="dialog"
		class="confirm-dialog"
		@cancel.prevent="cancel"
		@close="cancel"
	>
		<div class="dialog-icon">
			<AlertTriangle :size="20" aria-hidden="true" />
		</div>
		<div>
			<h2>{{ title }}</h2>
			<p>{{ description }}</p>
		</div>
		<div class="dialog-actions">
			<button class="button button-secondary" type="button" :disabled="pending" @click="cancel">
				Keep it
			</button>
			<button class="button button-danger" type="button" :disabled="pending" @click="emit('confirm')">
				<LoaderCircle v-if="pending" class="spin" :size="15" aria-hidden="true" />
				{{ pending ? 'Working…' : confirmLabel }}
			</button>
		</div>
	</dialog>
</template>
