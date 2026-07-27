<script setup lang="ts">
import { computed } from 'vue';
import { CirclePause, CirclePlay, Play } from '@lucide/vue';

import FeedbackState from '@/components/FeedbackState.vue';
import PageHeader from '@/components/PageHeader.vue';
import StateBadge from '@/components/StateBadge.vue';
import { formatDate } from '@/format';
import { errorText } from '@/notifications';
import { useRecurringQuery } from '@/query';
import { useRecurringMutations } from '@/use-dashboard-mutations';

const recurringQuery = useRecurringQuery();
const recurringMutations = useRecurringMutations();
const schedules = computed(() => recurringQuery.data.value ?? []);
</script>

<template>
	<section>
		<PageHeader
			title="Recurring"
			description="Review schedules and control future materializations."
			:meta="`${schedules.length} configured`"
		/>

		<FeedbackState v-if="recurringQuery.error.value" type="error" title="Schedules could not be loaded" :description="errorText(recurringQuery.error.value)" />
		<FeedbackState v-else-if="recurringQuery.isPending.value" type="loading" title="Loading schedules" />
		<div v-else class="table-card">
			<FeedbackState v-if="schedules.length === 0" title="No recurring schedules" description="Code-defined and dynamic schedules will appear here." />
			<div v-else class="table-scroll">
				<table>
					<thead>
						<tr>
							<th>Name</th>
							<th>Job</th>
							<th>Schedule</th>
							<th>Next run</th>
							<th>Source</th>
							<th><span class="sr-only">Actions</span></th>
						</tr>
					</thead>
					<tbody>
						<tr v-for="schedule in schedules" :key="schedule.name">
							<td><code>{{ schedule.name }}</code></td>
							<td>{{ schedule.jobName }}</td>
							<td>
								<div class="schedule-cell">
									<code>{{ schedule.cron }}</code>
									<span>{{ schedule.timeZone }}</span>
								</div>
							</td>
							<td>{{ formatDate(schedule.nextRunAt) }}</td>
							<td>
								<div class="flex gap-1.5">
									<StateBadge :state="schedule.isCodeDefined ? 'Code' : 'Dynamic'" />
									<StateBadge v-if="schedule.isPaused" state="Paused" />
								</div>
							</td>
							<td>
								<div class="row-actions">
									<button
										class="button button-secondary"
										type="button"
										:disabled="recurringMutations.busyName.value === schedule.name"
										@click="recurringMutations.trigger(schedule.name)"
									>
										<Play :size="14" aria-hidden="true" />
										Trigger
									</button>
									<button
										class="button button-secondary"
										type="button"
										:disabled="recurringMutations.busyName.value === schedule.name"
										@click="recurringMutations.setPaused({ name: schedule.name, paused: !schedule.isPaused })"
									>
										<CirclePlay v-if="schedule.isPaused" :size="14" aria-hidden="true" />
										<CirclePause v-else :size="14" aria-hidden="true" />
										{{ schedule.isPaused ? 'Resume' : 'Pause' }}
									</button>
								</div>
							</td>
						</tr>
					</tbody>
				</table>
			</div>
		</div>
	</section>
</template>
