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
	startY?: number;
	endY: number;
	path: string;
}

interface PositionedJoin {
	childJobId: string;
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
	joins: PositionedJoin[];
	width: number;
	height: number;
}

const props = defineProps<{
	graph: BatchGraph | undefined;
}>();

const emit = defineEmits<{
	select: [jobId: string];
}>();

const minimumNodeWidth = 170;
const nodeHeight = 58;
const columnGap = 90;
const rowGap = 26;
const graphPadding = 24;
const nodeHorizontalPadding = 30;

const viewport = ref<HTMLElement>();
const showAllConstraints = ref(false);
let followedActiveJobId: string | undefined;
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

function portY(node: PositionedNode, index: number, portCount: number): number {
	return node.y + nodeHeight * (index + 1) / (portCount + 1);
}

function edgePath(startX: number, startY: number, endX: number, endY: number): string {
	const controlOffset = Math.max(36, (endX - startX) / 2);
	return [
		`M ${startX} ${startY}`,
		`C ${startX + controlOffset} ${startY},`,
		`${endX - controlOffset} ${endY},`,
		`${endX} ${endY}`,
	].join(' ');
}

function groupEdgesByParent(edges: BatchGraphEdge[]): Map<string, IndexedEdge[]> {
	const grouped = new Map<string, IndexedEdge[]>();
	for (const [index, edge] of edges.entries()) {
		if (!edge.parentJobId) {
			continue;
		}
		const outgoing = grouped.get(edge.parentJobId) ?? [];
		outgoing.push({ edge, index });
		grouped.set(edge.parentJobId, outgoing);
	}
	return grouped;
}

function hasAlternatePath(excluded: BatchGraphEdge, excludedIndex: number, edgesByParent: Map<string, IndexedEdge[]>): boolean {
	if (!excluded.parentJobId) {
		return false;
	}

	const requireSuccess = excluded.trigger === 'AllSucceeded';
	const visited = new Set<string>([excluded.parentJobId]);
	const pending = [{ jobId: excluded.parentJobId, depth: 0 }];
	while (pending.length > 0) {
		const current = pending.pop();
		if (!current) {
			continue;
		}
		for (const { edge, index } of edgesByParent.get(current.jobId) ?? []) {
			if (index === excludedIndex
				|| (requireSuccess && edge.trigger !== 'AllSucceeded')) {
				continue;
			}
			if (edge.childJobId === excluded.childJobId && current.depth > 0) {
				return true;
			}
			if (!visited.has(edge.childJobId)) {
				visited.add(edge.childJobId);
				pending.push({ jobId: edge.childJobId, depth: current.depth + 1 });
			}
		}
	}
	return false;
}

function essentialEdges(edges: BatchGraphEdge[]): BatchGraphEdge[] {
	const edgesByParent = groupEdgesByParent(edges);
	return edges.filter((edge, index) => !hasAlternatePath(edge, index, edgesByParent));
}

function createEdges(edgesToDraw: BatchGraphEdge[], positions: Map<string, PositionedNode>): {
	edges: PositionedEdge[];
	joins: PositionedJoin[];
} {
	const edges = edgesToDraw.flatMap((edge, index) => {
		const to = positions.get(edge.childJobId);
		if (!to) {
			return [];
		}
		const from = edge.parentJobId ? positions.get(edge.parentJobId) : undefined;
		return [{ ...edge, index, from, to, joinsFanIn: false, endY: to.y + nodeHeight / 2 }];
	});
	const incomingByJob = new Map<string, typeof edges>();
	const outgoingByJob = new Map<string, typeof edges>();

	for (const edge of edges) {
		const incoming = incomingByJob.get(edge.childJobId) ?? [];
		incoming.push(edge);
		incomingByJob.set(edge.childJobId, incoming);
		if (edge.parentJobId) {
			const outgoing = outgoingByJob.get(edge.parentJobId) ?? [];
			outgoing.push(edge);
			outgoingByJob.set(edge.parentJobId, outgoing);
		}
	}

	const joins: PositionedJoin[] = [];
	for (const [childJobId, incoming] of incomingByJob) {
		incoming.sort((left, right) => (left.from?.y ?? left.index) - (right.from?.y ?? right.index));
		if (incoming.length === 1) {
			continue;
		}

		const to = incoming[0]?.to;
		if (!to) {
			continue;
		}
		const joinX = to.x - 32;
		const joinY = to.y + nodeHeight / 2;
		for (const edge of incoming) {
			edge.joinsFanIn = true;
			edge.endY = joinY;
		}
		joins.push({
			childJobId,
			x: joinX,
			y: joinY,
			path: `M ${joinX} ${joinY} L ${to.x} ${joinY}`,
		});
	}
	for (const outgoing of outgoingByJob.values()) {
		outgoing.sort((left, right) => left.to.y - right.to.y);
		outgoing.forEach((edge, index) => {
			if (edge.from) {
				edge.startY = portY(edge.from, index, outgoing.length);
			}
		});
	}

	const positionedEdges = edges.map((edge) => {
		const startX = edge.from ? edge.from.x + edge.from.width : 4;
		const startY = edge.startY ?? edge.endY;
		const endX = edge.joinsFanIn ? edge.to.x - 32 : edge.to.x;
		return {
			...edge,
			path: edgePath(startX, startY, endX, edge.endY),
		};
	});
	return { edges: positionedEdges, joins };
}

function layout(graph: BatchGraph | undefined, edgesToDraw: BatchGraphEdge[]): Drawing {
	if (!graph) {
		return { nodes: [], edges: [], joins: [], width: 0, height: 0 };
	}

	const nodesById = new Map(graph.nodes.map((node) => [
		node.jobId,
		{ ...node, rank: 0, width: measureNodeWidth(node.jobName), x: 0, y: 0 },
	]));
	for (let pass = 0; pass < graph.nodes.length; pass++) {
		let changed = false;
		for (const edge of edgesToDraw) {
			if (!edge.parentJobId) {
				continue;
			}
			const parent = nodesById.get(edge.parentJobId);
			const child = nodesById.get(edge.childJobId);
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
	const nodes: PositionedNode[] = [];
	let nextLayerX = graphPadding;
	let contentRight = graphPadding;
	for (const layer of layers) {
		layer.sort((left, right) => left.jobName.localeCompare(right.jobName));
		const layerWidth = Math.max(...layer.map((node) => node.width));
		layer.forEach((node, row) => {
			node.x = nextLayerX + (layerWidth - node.width) / 2;
			node.y = graphPadding + row * (nodeHeight + rowGap);
			nodes.push(node);
		});
		contentRight = nextLayerX + layerWidth;
		nextLayerX = contentRight + columnGap;
	}

	const positions = new Map(nodes.map((node) => [node.jobId, node]));
	const largestLayer = Math.max(1, ...layers.map((layer) => layer.length));
	const { edges, joins } = createEdges(edgesToDraw, positions);
	return {
		nodes,
		edges,
		joins,
		width: Math.max(420, contentRight + graphPadding),
		height: Math.max(240, graphPadding * 2 + largestLayer * nodeHeight + (largestLayer - 1) * rowGap),
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

watch(() => props.graph?.batchId, () => {
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
		followedActiveJobId = undefined;
		return;
	}
	const target = activeNodes.find((node) => node.jobId === followedActiveJobId) ?? activeNodes[0];
	if (!target) {
		return;
	}
	followedActiveJobId = target.jobId;
	await nextTick();
	if (!nodeIsVisible(viewportElement, target)) {
		viewportElement.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
		centerNode(viewportElement, target);
	}
}, { immediate: true });

function handleNodeKeydown(event: KeyboardEvent, jobId: string): void {
	if (event.key === 'Enter' || event.key === ' ') {
		event.preventDefault();
		emit('select', jobId);
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
					:key="`${edge.childJobId}-${edge.parentJobId}-${edge.index}`"
					class="workflow-edge"
					:class="{ dashed: edge.trigger === 'AllComplete' }"
					:data-parent-job-id="edge.parentJobId"
					:data-child-job-id="edge.childJobId"
					:data-trigger="edge.trigger"
					:d="edge.path"
					:marker-end="edge.joinsFanIn ? undefined : 'url(#workflow-arrow)'"
				/>
				<g
					v-for="join in drawing.joins"
					:key="`join-${join.childJobId}`"
					class="workflow-join"
					:data-child-job-id="join.childJobId"
				>
					<path :d="join.path" marker-end="url(#workflow-arrow)" />
					<circle :cx="join.x" :cy="join.y" r="3.5" />
				</g>
				<g
					v-for="node in drawing.nodes"
					:key="node.jobId"
					class="workflow-node"
					:class="node.state.toLowerCase()"
					:data-job-id="node.jobId"
					:transform="`translate(${node.x} ${node.y})`"
					role="button"
					tabindex="0"
					@click="emit('select', node.jobId)"
					@keydown="handleNodeKeydown($event, node.jobId)"
				>
					<rect :width="node.width" :height="nodeHeight" rx="9" />
					<text x="12" y="24">{{ node.jobName }}</text>
					<text class="node-state" x="12" y="43">{{ node.state }}</text>
				</g>
			</svg>
		</div>
	</div>
</template>
