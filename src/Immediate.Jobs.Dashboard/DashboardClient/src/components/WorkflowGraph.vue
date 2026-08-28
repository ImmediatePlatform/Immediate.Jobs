<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';

import FeedbackState from '@/components/FeedbackState.vue';
import type { BatchGraph, BatchGraphEdge, BatchGraphNode } from '@/contracts';

interface PositionedNode extends BatchGraphNode {
	rank: number;
	width: number;
	x: number;
	y: number;
}

interface PositionedEdge extends BatchGraphEdge {
	index: number;
	from?: PositionedNode;
	to: PositionedNode;
	joinsFanIn: boolean;
	channelX?: number;
	startX: number;
	startY: number;
	endY: number;
	path: string;
}

interface PositionedFork {
	parentJobHandle: string;
	x: number;
	y: number;
	path: string;
}

interface PositionedJoin {
	childJobHandle: string;
	x: number;
	y: number;
	path: string;
}

interface IndexedEdge {
	edge: BatchGraphEdge;
	index: number;
}

interface Drawing {
	nodes: PositionedNode[];
	edges: PositionedEdge[];
	forks: PositionedFork[];
	joins: PositionedJoin[];
	width: number;
	height: number;
}

const props = defineProps<{
	graph: BatchGraph | undefined;
}>();

const emit = defineEmits<{
	select: [jobHandle: string];
}>();

const minimumNodeWidth = 170;
const nodeHeight = 58;
const columnGap = 90;
const rowGap = 26;
const graphPadding = 24;
const nodeHorizontalPadding = 30;
const minimumGraphHeight = 270;
const layoutSweepCount = 4;
const edgeCornerRadius = 9;
const junctionOffset = 24;

const viewport = ref<HTMLElement>();
const showAllConstraints = ref(false);
let followedActiveJobHandle: string | undefined;
let textMeasurementContext: CanvasRenderingContext2D | null | undefined;

function measureNodeWidth(jobName: string): number {
	let textWidth = jobName.length * 7.25;
	if (typeof document !== 'undefined') {
		textMeasurementContext ??= document.createElement('canvas').getContext('2d');
		if (textMeasurementContext) {
			textMeasurementContext.font = '600 12px ui-sans-serif, system-ui, sans-serif';
			textWidth = textMeasurementContext.measureText(jobName).width;
		}
	}
	return Math.max(minimumNodeWidth, Math.ceil(textWidth + nodeHorizontalPadding));
}

function pipelineEdgePath(startX: number, startY: number, endX: number, endY: number, channelX?: number): string {
	const availableWidth = endX - startX;
	if (availableWidth < 24) {
		return `M ${startX} ${startY} L ${endX} ${endY}`;
	}

	const preferredChannelX = channelX ?? startX + availableWidth / 2;
	const routeX = Math.min(
		endX - 12,
		Math.max(startX + 12, preferredChannelX),
	);
	if (startY === endY) {
		return `M ${startX} ${startY} H ${endX}`;
	}

	const verticalDirection = endY > startY ? 1 : -1;
	const radius = Math.min(
		edgeCornerRadius,
		Math.abs(endY - startY) / 2,
		routeX - startX,
		endX - routeX,
	);
	return [
		`M ${startX} ${startY}`,
		`H ${routeX - radius}`,
		`Q ${routeX} ${startY} ${routeX} ${startY + verticalDirection * radius}`,
		`V ${endY - verticalDirection * radius}`,
		`Q ${routeX} ${endY} ${routeX + radius} ${endY}`,
		`H ${endX}`,
	].join(' ');
}

function groupEdgesByParent(edges: BatchGraphEdge[]): Map<string, IndexedEdge[]> {
	const grouped = new Map<string, IndexedEdge[]>();
	for (const [index, edge] of edges.entries()) {
		if (!edge.parentJobHandle) {
			continue;
		}
		const outgoing = grouped.get(edge.parentJobHandle) ?? [];
		outgoing.push({ edge, index });
		grouped.set(edge.parentJobHandle, outgoing);
	}
	return grouped;
}

function hasAlternatePath(excluded: BatchGraphEdge, excludedIndex: number, edgesByParent: Map<string, IndexedEdge[]>): boolean {
	if (!excluded.parentJobHandle) {
		return false;
	}

	const requiredTrigger = excluded.trigger;
	const visited = new Set<string>([excluded.parentJobHandle]);
	const pending = [{ jobHandle: excluded.parentJobHandle, depth: 0 }];
	while (pending.length > 0) {
		const current = pending.pop();
		if (!current) {
			continue;
		}
		for (const { edge, index } of edgesByParent.get(current.jobHandle) ?? []) {
			if (index === excludedIndex || edge.trigger !== requiredTrigger) {
				continue;
			}
			if (edge.childJobHandle === excluded.childJobHandle && current.depth > 0) {
				return true;
			}
			if (!visited.has(edge.childJobHandle)) {
				visited.add(edge.childJobHandle);
				pending.push({ jobHandle: edge.childJobHandle, depth: current.depth + 1 });
			}
		}
	}
	return false;
}

function essentialEdges(edges: BatchGraphEdge[]): BatchGraphEdge[] {
	const edgesByParent = groupEdgesByParent(edges);
	return edges.filter((edge, index) => !hasAlternatePath(edge, index, edgesByParent));
}

function groupConnectedJobs(
	edges: BatchGraphEdge[],
): { parentsByJob: Map<string, string[]>; childrenByJob: Map<string, string[]> } {
	const parentsByJob = new Map<string, string[]>();
	const childrenByJob = new Map<string, string[]>();
	for (const edge of edges) {
		if (!edge.parentJobHandle) {
			continue;
		}

		const parents = parentsByJob.get(edge.childJobHandle) ?? [];
		parents.push(edge.parentJobHandle);
		parentsByJob.set(edge.childJobHandle, parents);

		const children = childrenByJob.get(edge.parentJobHandle) ?? [];
		children.push(edge.childJobHandle);
		childrenByJob.set(edge.parentJobHandle, children);
	}
	return { parentsByJob, childrenByJob };
}

function normalizedNodePositions(layers: PositionedNode[][]): Map<string, number> {
	const positions = new Map<string, number>();
	for (const layer of layers) {
		const denominator = Math.max(1, layer.length - 1);
		layer.forEach((node, index) => {
			positions.set(node.jobHandle, layer.length === 1 ? 0.5 : index / denominator);
		});
	}
	return positions;
}

function orderLayerByNeighbors(
	layer: PositionedNode[],
	neighborsByJob: Map<string, string[]>,
	positions: Map<string, number>,
): void {
	const previousOrder = new Map(layer.map((node, index) => [node.jobHandle, index]));
	const scores = new Map<string, number>();
	for (const node of layer) {
		const neighborPositions = (neighborsByJob.get(node.jobHandle) ?? [])
			.flatMap((jobHandle) => {
				const position = positions.get(jobHandle);
				return position === undefined ? [] : [position];
			});
		if (neighborPositions.length > 0) {
			const total = neighborPositions.reduce((sum, position) => sum + position, 0);
			scores.set(node.jobHandle, total / neighborPositions.length);
		}
	}

	layer.sort((left, right) => {
		const leftScore = scores.get(left.jobHandle);
		const rightScore = scores.get(right.jobHandle);
		if (leftScore !== undefined && rightScore !== undefined && leftScore !== rightScore) {
			return leftScore - rightScore;
		}
		if (leftScore !== undefined && rightScore === undefined) {
			return -1;
		}
		if (leftScore === undefined && rightScore !== undefined) {
			return 1;
		}
		return (previousOrder.get(left.jobHandle) ?? 0) - (previousOrder.get(right.jobHandle) ?? 0);
	});
}

function orderLayers(layers: PositionedNode[][], edges: BatchGraphEdge[]): void {
	const { parentsByJob, childrenByJob } = groupConnectedJobs(edges);
	for (const layer of layers) {
		layer.sort((left, right) => left.jobName.localeCompare(right.jobName));
	}

	for (let sweep = 0; sweep < layoutSweepCount; sweep++) {
		for (let rank = 1; rank < layers.length; rank++) {
			orderLayerByNeighbors(layers[rank] ?? [], parentsByJob, normalizedNodePositions(layers));
		}
		for (let rank = layers.length - 2; rank >= 0; rank--) {
			orderLayerByNeighbors(layers[rank] ?? [], childrenByJob, normalizedNodePositions(layers));
		}
	}
}

function createEdges(edgesToDraw: BatchGraphEdge[], positions: Map<string, PositionedNode>): {
	edges: PositionedEdge[];
	forks: PositionedFork[];
	joins: PositionedJoin[];
} {
	const edges = edgesToDraw.flatMap((edge, index) => {
		const to = positions.get(edge.childJobHandle);
		if (!to) {
			return [];
		}
		const from = edge.parentJobHandle ? positions.get(edge.parentJobHandle) : undefined;
		const endY = to.y + nodeHeight / 2;
		return [{
			...edge,
			index,
			from,
			to,
			joinsFanIn: false,
			startX: from ? from.x + from.width : 4,
			startY: from ? from.y + nodeHeight / 2 : endY,
			endY,
		}];
	});
	const incomingByJob = new Map<string, typeof edges>();
	const outgoingByJob = new Map<string, typeof edges>();

	for (const edge of edges) {
		const incoming = incomingByJob.get(edge.childJobHandle) ?? [];
		incoming.push(edge);
		incomingByJob.set(edge.childJobHandle, incoming);
		if (edge.parentJobHandle) {
			const outgoing = outgoingByJob.get(edge.parentJobHandle) ?? [];
			outgoing.push(edge);
			outgoingByJob.set(edge.parentJobHandle, outgoing);
		}
	}

	const joins: PositionedJoin[] = [];
	for (const [childJobHandle, incoming] of incomingByJob) {
		if (incoming.length === 1) {
			continue;
		}

		const to = incoming[0]?.to;
		if (!to) {
			continue;
		}
		const joinX = to.x - junctionOffset;
		const joinY = to.y + nodeHeight / 2;
		const channelX = joinX - junctionOffset;
		for (const edge of incoming) {
			edge.joinsFanIn = true;
			edge.channelX = channelX;
			edge.endY = joinY;
		}
		joins.push({
			childJobHandle,
			x: joinX,
			y: joinY,
			path: `M ${joinX} ${joinY} L ${to.x} ${joinY}`,
		});
	}

	const forks: PositionedFork[] = [];
	for (const [parentJobHandle, outgoing] of outgoingByJob) {
		const from = outgoing[0]?.from;
		if (outgoing.length < 2 || !from) {
			continue;
		}

		const sourceX = from.x + from.width;
		const forkX = sourceX + junctionOffset;
		const forkY = from.y + nodeHeight / 2;
		for (const edge of outgoing) {
			edge.startX = forkX;
			edge.startY = forkY;
		}
		forks.push({
			parentJobHandle,
			x: forkX,
			y: forkY,
			path: `M ${sourceX} ${forkY} L ${forkX} ${forkY}`,
		});
	}

	const positionedEdges = edges.map((edge) => {
		const endX = edge.joinsFanIn ? edge.to.x - junctionOffset : edge.to.x;
		return {
			...edge,
			path: pipelineEdgePath(edge.startX, edge.startY, endX, edge.endY, edge.channelX),
		};
	});
	return { edges: positionedEdges, forks, joins };
}

function layout(graph: BatchGraph | undefined, edgesToDraw: BatchGraphEdge[]): Drawing {
	if (!graph) {
		return { nodes: [], edges: [], forks: [], joins: [], width: 0, height: 0 };
	}

	const nodesById = new Map(graph.nodes.map((node) => [
		node.jobHandle,
		{ ...node, rank: 0, width: measureNodeWidth(node.jobName), x: 0, y: 0 },
	]));
	for (let pass = 0; pass < graph.nodes.length; pass++) {
		let changed = false;
		for (const edge of edgesToDraw) {
			if (!edge.parentJobHandle) {
				continue;
			}
			const parent = nodesById.get(edge.parentJobHandle);
			const child = nodesById.get(edge.childJobHandle);
			if (!parent || !child) {
				continue;
			}
			const nextRank = parent.rank + 1;
			if (nextRank > child.rank) {
				child.rank = nextRank;
				changed = true;
			}
		}
		if (!changed) {
			break;
		}
	}

	const layers: PositionedNode[][] = [];
	for (const node of nodesById.values()) {
		(layers[node.rank] ??= []).push(node);
	}
	orderLayers(layers, edgesToDraw);

	const largestLayer = Math.max(1, ...layers.map((layer) => layer.length));
	const contentHeight = largestLayer * nodeHeight + (largestLayer - 1) * rowGap;
	const height = Math.max(minimumGraphHeight, graphPadding * 2 + contentHeight);
	const nodes: PositionedNode[] = [];
	let nextLayerX = graphPadding;
	let contentRight = graphPadding;
	for (const layer of layers) {
		const layerWidth = Math.max(...layer.map((node) => node.width));
		const layerHeight = layer.length * nodeHeight + (layer.length - 1) * rowGap;
		const layerTop = (height - layerHeight) / 2;
		layer.forEach((node, row) => {
			node.width = layerWidth;
			node.x = nextLayerX;
			node.y = layerTop + row * (nodeHeight + rowGap);
			nodes.push(node);
		});
		contentRight = nextLayerX + layerWidth;
		nextLayerX = contentRight + columnGap;
	}

	const positions = new Map(nodes.map((node) => [node.jobHandle, node]));
	const { edges, forks, joins } = createEdges(edgesToDraw, positions);
	return {
		nodes,
		edges,
		forks,
		joins,
		width: Math.max(420, contentRight + graphPadding),
		height,
	};
}

const simplifiedEdges = computed(() => essentialEdges(props.graph?.edges ?? []));
const hiddenConstraintCount = computed(() => (props.graph?.edges.length ?? 0) - simplifiedEdges.value.length);
const visibleEdges = computed(() => showAllConstraints.value ? (props.graph?.edges ?? []) : simplifiedEdges.value);
const drawing = computed(() => layout(props.graph, visibleEdges.value));
const toolbarDescription = computed(() => {
	if (showAllConstraints.value) {
		return 'Showing every persisted constraint';
	}
	const suffix = hiddenConstraintCount.value === 1 ? '' : 's';
	return `${hiddenConstraintCount.value} transitive constraint${suffix} simplified`;
});
const toggleLabel = computed(() => showAllConstraints.value ? 'Simplify workflow' : 'Show all constraints');

watch(() => props.graph?.batchHandle, () => {
	showAllConstraints.value = false;
});

function nodeIsVisible(element: HTMLElement, node: PositionedNode): boolean {
	const padding = 16;
	return node.x >= element.scrollLeft + padding
		&& node.y >= element.scrollTop + padding
		&& node.x + node.width <= element.scrollLeft + element.clientWidth - padding
		&& node.y + nodeHeight <= element.scrollTop + element.clientHeight - padding;
}

function centerNode(element: HTMLElement, node: PositionedNode): void {
	element.scrollTo({
		left: Math.max(0, node.x + node.width / 2 - element.clientWidth / 2),
		top: Math.max(0, node.y + nodeHeight / 2 - element.clientHeight / 2),
		behavior: 'smooth',
	});
}

watch([drawing, viewport], async ([nextDrawing, viewportElement]) => {
	const activeNodes = nextDrawing.nodes.filter((node) => node.state === 'Active');
	if (!viewportElement || activeNodes.length === 0) {
		followedActiveJobHandle = undefined;
		return;
	}
	const target = activeNodes.find((node) => node.jobHandle === followedActiveJobHandle) ?? activeNodes[0];
	if (!target) {
		return;
	}
	followedActiveJobHandle = target.jobHandle;
	await nextTick();
	if (!nodeIsVisible(viewportElement, target)) {
		viewportElement.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
		centerNode(viewportElement, target);
	}
}, { immediate: true });

function handleNodeKeydown(event: KeyboardEvent, jobHandle: string): void {
	if (event.key === 'Enter' || event.key === ' ') {
		event.preventDefault();
		emit('select', jobHandle);
	}
}
</script>

<template>
	<FeedbackState v-if="!graph" type="loading" title="Loading workflow" />
	<div v-else class="workflow-frame">
		<div v-if="hiddenConstraintCount > 0" class="workflow-toolbar">
			<p>{{ toolbarDescription }}</p>
			<button
				class="workflow-toggle"
				type="button"
				:aria-pressed="showAllConstraints"
				@click="showAllConstraints = !showAllConstraints"
			>
				{{ toggleLabel }}
			</button>
		</div>
		<div ref="viewport" class="workflow-scroll">
			<svg class="workflow" :width="drawing.width" :height="drawing.height" aria-label="Batch dependency graph">
				<defs>
					<marker id="workflow-arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
						<path d="M0,0 L8,4 L0,8 z" />
					</marker>
				</defs>
				<path
					v-for="edge in drawing.edges"
					:key="`${edge.childJobHandle}-${edge.parentJobHandle}-${edge.index}`"
					class="workflow-edge"
					:class="{ dashed: edge.trigger === 'Complete', failure: edge.trigger === 'Failure' }"
					:data-parent-job-id="edge.parentJobHandle"
					:data-child-job-id="edge.childJobHandle"
					:data-trigger="edge.trigger"
					:d="edge.path"
					:marker-end="edge.joinsFanIn ? undefined : 'url(#workflow-arrow)'"
				/>
				<g
					v-for="fork in drawing.forks"
					:key="`fork-${fork.parentJobHandle}`"
					class="workflow-fork"
					:data-parent-job-id="fork.parentJobHandle"
				>
					<path :d="fork.path" />
					<circle :cx="fork.x" :cy="fork.y" r="3.5" />
				</g>
				<g
					v-for="join in drawing.joins"
					:key="`join-${join.childJobHandle}`"
					class="workflow-join"
					:data-child-job-id="join.childJobHandle"
				>
					<path :d="join.path" marker-end="url(#workflow-arrow)" />
					<circle :cx="join.x" :cy="join.y" r="3.5" />
				</g>
				<g
					v-for="node in drawing.nodes"
					:key="node.jobHandle"
					class="workflow-node"
					:class="node.state.toLowerCase()"
					:data-job-id="node.jobHandle"
					:transform="`translate(${node.x} ${node.y})`"
					role="button"
					tabindex="0"
					@click="emit('select', node.jobHandle)"
					@keydown="handleNodeKeydown($event, node.jobHandle)"
				>
					<rect :width="node.width" :height="nodeHeight" rx="9" />
					<text x="12" y="24">{{ node.jobName }}</text>
					<text class="node-state" x="12" y="43">{{ node.state }}</text>
				</g>
			</svg>
		</div>
	</div>
</template>
