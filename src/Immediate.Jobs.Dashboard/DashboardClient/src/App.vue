<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink, RouterView, useRoute } from 'vue-router';
import { useColorMode } from '@vueuse/core';
import {
	Activity,
	CalendarClock,
	Layers3,
	LayoutDashboard,
	ListChecks,
	Moon,
	Server,
	Sun,
	X,
} from '@lucide/vue';

import { dismissNotification, notifications } from '@/notifications';
import { useOverviewQuery } from '@/query';
import { connectionStatus } from '@/stream-state';
import { useDashboardStream } from '@/use-dashboard-stream';

useDashboardStream();
const overviewQuery = useOverviewQuery();

const route = useRoute();
const colorMode = useColorMode({
	attribute: 'data-theme',
	emitAuto: true,
	storageKey: 'immediate-jobs-theme',
});

const allNavigation = [
	{ name: 'overview', label: 'Overview', icon: LayoutDashboard },
	{ name: 'jobs', label: 'Jobs', icon: ListChecks },
	{ name: 'batches', label: 'Batches', icon: Layers3 },
	{ name: 'recurring', label: 'Recurring', icon: CalendarClock },
	{ name: 'servers', label: 'Servers', icon: Server },
] as const;

const navigation = computed(() => allNavigation.filter((item) => {
	const capabilities = overviewQuery.data.value?.capabilities;
	if (item.name === 'batches') {
		return capabilities?.includes('Graph') ?? true;
	}
	if (item.name === 'recurring') {
		return capabilities?.includes('Recurring') ?? true;
	}
	return true;
}));

const connectionLabel = computed(() => {
	if (connectionStatus.value === 'live') {
		return 'Live updates';
	}
	if (connectionStatus.value === 'reconnecting') {
		return 'Reconnecting';
	}
	return 'Connecting';
});

function navigationIsActive(name: string): boolean {
	return route.name === name
		|| (name === 'batches' && (route.name === 'batch-detail' || route.name === 'batch-job'));
}
</script>

<template>
	<div class="app-shell">
		<aside class="sidebar">
			<div class="brand">
				<span class="brand-mark" aria-hidden="true">
					<Activity :size="20" :stroke-width="2.4" />
				</span>
				<span>
					<strong>Immediate.Jobs</strong>
					<small>Operations console</small>
				</span>
			</div>

			<nav aria-label="Dashboard views">
				<RouterLink
					v-for="item in navigation"
					:key="item.name"
					:to="{ name: item.name }"
					:class="{ 'router-link-active': navigationIsActive(item.name) }"
				>
					<component :is="item.icon" :size="18" aria-hidden="true" />
					<span>{{ item.label }}</span>
				</RouterLink>
			</nav>

			<div class="sidebar-footer">
				<label class="theme-control">
					<Sun v-if="colorMode === 'light'" :size="16" aria-hidden="true" />
					<Moon v-else :size="16" aria-hidden="true" />
					<span class="sr-only">Color theme</span>
					<select v-model="colorMode" aria-label="Color theme">
						<option value="auto">System theme</option>
						<option value="light">Light theme</option>
						<option value="dark">Dark theme</option>
					</select>
				</label>
				<div class="connection" :data-status="connectionStatus">
					<span aria-hidden="true"></span>
					{{ connectionLabel }}
				</div>
			</div>
		</aside>

		<header class="mobile-header">
			<div class="brand compact">
				<span class="brand-mark" aria-hidden="true">
					<Activity :size="18" />
				</span>
				<strong>Immediate.Jobs</strong>
			</div>
			<div class="connection" :data-status="connectionStatus">
				<span aria-hidden="true"></span>
				{{ connectionLabel }}
			</div>
		</header>

		<nav class="mobile-nav" aria-label="Dashboard views">
			<RouterLink
				v-for="item in navigation"
				:key="item.name"
				:to="{ name: item.name }"
				:class="{ 'router-link-active': navigationIsActive(item.name) }"
			>
				<component :is="item.icon" :size="17" aria-hidden="true" />
				<span>{{ item.label }}</span>
			</RouterLink>
		</nav>

		<main id="main-content" class="main-content">
			<RouterView />
		</main>

		<div class="toast-region" aria-live="polite" aria-atomic="false">
			<div
				v-for="notification in notifications"
				:key="notification.id"
				class="toast"
				:class="`toast-${notification.tone}`"
			>
				<span>{{ notification.message }}</span>
				<button
					type="button"
					aria-label="Dismiss notification"
					@click="dismissNotification(notification.id)"
				>
					<X :size="15" aria-hidden="true" />
				</button>
			</div>
		</div>
	</div>
</template>
