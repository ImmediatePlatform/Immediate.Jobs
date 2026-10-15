<script setup lang="ts">
import StateBadge from '@/components/StateBadge.vue';
import type { JobAcquisitionLimits, JobAcquisitionState } from '@/contracts';

defineProps<{
	limits: JobAcquisitionLimits;
	acquisition?: JobAcquisitionState | undefined;
}>();
</script>

<template>
	<div class="acquisition-limits">
		<span v-if="limits.maxConcurrency === 0 && limits.fixedWindowMax === 0 && limits.slidingWindowMax === 0">No limit</span>
		<template v-else>
			<span v-if="limits.maxConcurrency > 0">
				Max active: <template v-if="acquisition">{{ acquisition.activeCount }} / </template>{{ limits.maxConcurrency }}
			</span>
			<span v-if="limits.fixedWindowMax > 0 && limits.fixedWindowPeriod">Fixed window: <template v-if="acquisition">{{ acquisition.fixedWindowCount }} / </template>{{ limits.fixedWindowMax }} per {{ limits.fixedWindowPeriod }}</span>
			<span v-if="limits.slidingWindowMax > 0 && limits.slidingWindowPeriod">Sliding window: <template v-if="acquisition">{{ acquisition.slidingWindowCount }} / </template>{{ limits.slidingWindowMax }} per {{ limits.slidingWindowPeriod }}</span>
			<StateBadge v-if="acquisition?.isConcurrencyLimited" state="At capacity" />
		</template>
	</div>
</template>
