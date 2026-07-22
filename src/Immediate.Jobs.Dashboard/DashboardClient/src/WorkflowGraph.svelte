<script>
  let { graph, select = () => {} } = $props();

  const minimumNodeWidth = 170;
  const nodeHeight = 58;
  const columnGap = 90;
  const rowGap = 26;
  const graphPadding = 24;
  const nodeHorizontalPadding = 24;

  let workflowViewport = $state();
  let followedActiveJobId = null;
  let textMeasurementContext = null;

  function measureNodeWidth(jobName) {
    let textWidth = jobName.length * 7.25;

    if (typeof document !== 'undefined') {
      textMeasurementContext ??= document.createElement('canvas').getContext('2d');

      if (textMeasurementContext) {
        textMeasurementContext.font =
          '600 12px Inter, ui-sans-serif, system-ui, -apple-system, sans-serif';
        textWidth = textMeasurementContext.measureText(jobName).width;
      }
    }

    return Math.max(
      minimumNodeWidth,
      Math.ceil(textWidth + nodeHorizontalPadding)
    );
  }

  function portY(node, index, portCount) {
    return node.y + nodeHeight * (index + 1) / (portCount + 1);
  }

  function createEdgePath(startX, startY, endX, endY) {
    const controlOffset = Math.max(36, (endX - startX) / 2);

    return [
      `M ${startX} ${startY}`,
      `C ${startX + controlOffset} ${startY},`,
      `${endX - controlOffset} ${endY},`,
      `${endX} ${endY}`
    ].join(' ');
  }

  function createEdges(value, positions) {
    const edges = value.edges
      .map((edge, index) => ({
        ...edge,
        index,
        from: edge.parentJobId ? positions.get(edge.parentJobId) : null,
        to: positions.get(edge.childJobId)
      }))
      .filter(edge => edge.to);

    const incomingByJob = new Map();
    const outgoingByJob = new Map();

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

    for (const incoming of incomingByJob.values()) {
      incoming.sort((left, right) =>
        (left.from?.y ?? left.index) - (right.from?.y ?? right.index)
      );
      incoming.forEach((edge, index) => {
        edge.endY = portY(edge.to, index, incoming.length);
      });
    }

    for (const outgoing of outgoingByJob.values()) {
      outgoing.sort((left, right) => left.to.y - right.to.y);
      outgoing.forEach((edge, index) => {
        edge.startY = portY(edge.from, index, outgoing.length);
      });
    }

    return edges.map(edge => {
      const startX = edge.from ? edge.from.x + edge.from.width : 4;
      const startY = edge.startY ?? edge.endY;
      const endX = edge.to.x;

      return {
        ...edge,
        path: createEdgePath(startX, startY, endX, edge.endY)
      };
    });
  }

  function layout(value) {
    if (!value) {
      return { nodes: [], edges: [], width: 0, height: 0 };
    }

    const nodesById = new Map(value.nodes.map(node => [
      node.jobId,
      {
        ...node,
        rank: 0,
        width: measureNodeWidth(node.jobName)
      }
    ]));

    for (let pass = 0; pass < value.nodes.length; pass++) {
      let changed = false;

      for (const edge of value.edges) {
        if (!edge.parentJobId ||
            !nodesById.has(edge.parentJobId) ||
            !nodesById.has(edge.childJobId)) {
          continue;
        }

        const child = nodesById.get(edge.childJobId);
        const nextRank = nodesById.get(edge.parentJobId).rank + 1;
        if (nextRank > child.rank) {
          child.rank = nextRank;
          changed = true;
        }
      }

      if (!changed) {
        break;
      }
    }

    const layers = [];
    for (const node of nodesById.values()) {
      (layers[node.rank] ??= []).push(node);
    }

    const nodes = [];
    let nextLayerX = graphPadding;
    let contentRight = graphPadding;

    for (const layer of layers) {
      layer.sort((left, right) => left.jobName.localeCompare(right.jobName));

      const layerWidth = Math.max(...layer.map(node => node.width));
      layer.forEach((node, row) => {
        nodes.push({
          ...node,
          x: nextLayerX + (layerWidth - node.width) / 2,
          y: graphPadding + row * (nodeHeight + rowGap)
        });
      });

      contentRight = nextLayerX + layerWidth;
      nextLayerX = contentRight + columnGap;
    }

    const positions = new Map(nodes.map(node => [node.jobId, node]));
    const edges = createEdges(value, positions);
    const largestLayer = Math.max(1, ...layers.map(layer => layer.length));

    return {
      nodes,
      edges,
      width: Math.max(420, contentRight + graphPadding),
      height: Math.max(
        240,
        graphPadding * 2 +
          largestLayer * nodeHeight +
          (largestLayer - 1) * rowGap
      )
    };
  }

  let drawing = $derived(layout(graph));

  function isNodeVisible(viewport, node) {
    const padding = 16;
    const left = viewport.scrollLeft + padding;
    const top = viewport.scrollTop + padding;
    const right = viewport.scrollLeft + viewport.clientWidth - padding;
    const bottom = viewport.scrollTop + viewport.clientHeight - padding;

    return node.x >= left &&
      node.y >= top &&
      node.x + node.width <= right &&
      node.y + nodeHeight <= bottom;
  }

  function centerNode(viewport, node) {
    viewport.scrollTo({
      left: Math.max(0, node.x + node.width / 2 - viewport.clientWidth / 2),
      top: Math.max(0, node.y + nodeHeight / 2 - viewport.clientHeight / 2),
      behavior: 'smooth'
    });
  }

  function isViewportFullyVisible(viewport) {
    const padding = 16;
    const bounds = viewport.getBoundingClientRect();

    return bounds.top >= padding &&
      bounds.left >= padding &&
      bounds.bottom <= window.innerHeight - padding &&
      bounds.right <= window.innerWidth - padding;
  }

  $effect(() => {
    const viewport = workflowViewport;
    const activeNodes = drawing.nodes.filter(node => node.state === 'Active');

    if (!viewport || activeNodes.length === 0) {
      followedActiveJobId = null;
      return;
    }

    const target = activeNodes.find(node => node.jobId === followedActiveJobId) ??
      activeNodes[0];
    followedActiveJobId = target.jobId;

    const frame = requestAnimationFrame(() => {
      if (!isViewportFullyVisible(viewport)) {
        viewport.scrollIntoView({
          behavior: 'smooth',
          block: 'nearest',
          inline: 'nearest'
        });
      }

      if (!isNodeVisible(viewport, target)) {
        centerNode(viewport, target);
      }
    });

    return () => cancelAnimationFrame(frame);
  });
</script>

{#if !graph}
  <div class="loading">Loading workflow…</div>
{:else}
  <div class="workflow-scroll" bind:this={workflowViewport}>
    <svg
      class="workflow"
      width={drawing.width}
      height={drawing.height}
      aria-label="Batch dependency graph"
    >
      <defs>
        <marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
          <path d="M0,0 L8,4 L0,8 z"></path>
        </marker>
      </defs>

      {#each drawing.edges as edge}
        <path
          class="workflow-edge"
          class:dashed={edge.trigger === 'AllComplete'}
          d={edge.path}
          marker-end="url(#arrow)"
        ></path>
      {/each}

      {#each drawing.nodes as node}
        <g
          class="workflow-node {node.state.toLowerCase()}"
          data-job-id={node.jobId}
          transform={`translate(${node.x} ${node.y})`}
          onclick={() => select(node.jobId)}
          onkeydown={event => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              select(node.jobId);
            }
          }}
          role="button"
          tabindex="0"
        >
          <rect width={node.width} height={nodeHeight} rx="9"></rect>
          <text x="12" y="24">{node.jobName}</text>
          <text class="node-state" x="12" y="43">{node.state}</text>
        </g>
      {/each}
    </svg>
  </div>
{/if}
