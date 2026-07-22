<script setup lang="ts">
import { computed } from 'vue';

import type { HistoryPoint } from '@/contracts';

const props = withDefaults(defineProps<{
	points: HistoryPoint[];
	valueKey: 'throughput' | 'queued';
	label: string;
	color?: string;
}>(), {
	color: 'var(--accent)',
});

const width = 360;
const height = 104;
const plotLeft = 34;
const plotRight = 8;
const plotTop = 10;
const plotBottom = 20;

function valueAt(point: HistoryPoint): number {
	return Math.max(0, point[props.valueKey]);
}

const maximum = computed(() => Math.max(1, ...props.points.map(valueAt)));

function xAt(index: number): number {
	const available = width - plotLeft - plotRight;
	return props.points.length < 2
		? plotLeft + available / 2
		: plotLeft + available * index / (props.points.length - 1);
}

function yAt(value: number): number {
	const available = height - plotTop - plotBottom;
	return plotTop + available * (1 - value / maximum.value);
}

const linePoints = computed(() => props.points
	.map((point, index) => `${xAt(index)},${yAt(valueAt(point))}`)
	.join(' '));

const areaPath = computed(() => {
	if (props.points.length === 0) {
		return '';
	}
	const baseline = height - plotBottom;
	return `M ${xAt(0)} ${baseline} L ${linePoints.value.replaceAll(',', ' ')} L ${xAt(props.points.length - 1)} ${baseline} Z`;
});

function formatTime(value: string): string {
	return new Date(value).toLocaleTimeString();
}

function pointLabel(point: HistoryPoint): string {
	return `${props.label}: ${valueAt(point).toLocaleString()} at ${formatTime(point.capturedAt)}`;
}

function tooltipX(index: number): number {
	return Math.max(58, Math.min(width - 58, xAt(index)));
}

function tooltipY(value: number): number {
	const pointY = yAt(value);
	return pointY < 34 ? pointY + 38 : pointY - 8;
}
</script>

<template>
	<div class="history-chart">
		<svg :viewBox="`0 0 ${width} ${height}`" role="img" :aria-label="`Recent ${label.toLowerCase()} history`">
			<line class="chart-grid" :x1="plotLeft" :x2="width - plotRight" :y1="plotTop" :y2="plotTop" />
			<line class="chart-grid" :x1="plotLeft" :x2="width - plotRight" :y1="height - plotBottom" :y2="height - plotBottom" />
			<text class="axis-label" x="28" :y="plotTop + 4">{{ maximum.toLocaleString() }}</text>
			<text class="axis-label" x="28" :y="height - plotBottom + 4">0</text>
			<path class="chart-area" :d="areaPath" :fill="color" />
			<polyline class="chart-line" :points="linePoints" :stroke="color" />
			<g v-for="(point, index) in points" :key="point.capturedAt" class="chart-point">
				<title>{{ pointLabel(point) }}</title>
				<circle class="point-target" :cx="xAt(index)" :cy="yAt(valueAt(point))" r="10" />
				<circle class="point-dot" :cx="xAt(index)" :cy="yAt(valueAt(point))" r="3" :fill="color" />
				<foreignObject class="point-focus" :x="xAt(index) - 10" :y="yAt(valueAt(point)) - 10" width="20" height="20">
					<button type="button" :aria-label="pointLabel(point)" />
				</foreignObject>
				<g class="chart-tooltip" :transform="`translate(${tooltipX(index)} ${tooltipY(valueAt(point))})`">
					<rect x="-54" y="-24" width="108" height="36" rx="7" />
					<text class="tooltip-value" y="-9">{{ valueAt(point).toLocaleString() }}</text>
					<text class="tooltip-time" y="6">{{ formatTime(point.capturedAt) }}</text>
				</g>
			</g>
		</svg>
	</div>
</template>
