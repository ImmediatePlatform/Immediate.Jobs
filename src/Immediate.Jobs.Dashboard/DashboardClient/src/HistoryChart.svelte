<script>
  let { points = [], valueKey, label, color = 'var(--accent)' } = $props();

  const width = 320;
  const height = 88;
  const plotLeft = 30;
  const plotRight = 8;
  const plotTop = 8;
  const plotBottom = 18;

  function valueAt(point) {
    const value = Number(point?.[valueKey]);
    return Number.isFinite(value) ? Math.max(0, value) : 0;
  }

  function maximum() {
    return Math.max(1, ...points.map(valueAt));
  }

  function xAt(index) {
    const available = width - plotLeft - plotRight;
    return points.length < 2 ? plotLeft + available / 2 : plotLeft + available * index / (points.length - 1);
  }

  function yAt(value) {
    const available = height - plotTop - plotBottom;
    return plotTop + available * (1 - value / maximum());
  }

  function linePoints() {
    return points.map((point, index) => `${xAt(index)},${yAt(valueAt(point))}`).join(' ');
  }

  function areaPath() {
    if (points.length === 0)
      return '';

    const baseline = height - plotBottom;
    const firstX = xAt(0);
    const lastX = xAt(points.length - 1);
    return `M ${firstX} ${baseline} L ${linePoints().replaceAll(',', ' ')} L ${lastX} ${baseline} Z`;
  }

  function formatTime(value) {
    return value ? new Date(value).toLocaleTimeString() : 'latest update';
  }

  function pointLabel(point) {
    return `${label}: ${valueAt(point).toLocaleString()} at ${formatTime(point.capturedAt)}`;
  }

  function tooltipX(index) {
    return Math.max(55, Math.min(width - 55, xAt(index)));
  }

  function tooltipY(value) {
    const pointY = yAt(value);
    return pointY < 32 ? pointY + 34 : pointY - 8;
  }
</script>

<div class="history-chart">
  <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={`Recent ${label.toLowerCase()} history`}>
    <line class="chart-grid" x1={plotLeft} x2={width - plotRight} y1={plotTop} y2={plotTop}></line>
    <line class="chart-grid" x1={plotLeft} x2={width - plotRight} y1={height - plotBottom} y2={height - plotBottom}></line>
    <text class="axis-label" x="0" y={plotTop + 4}>{maximum().toLocaleString()}</text>
    <text class="axis-label" x="18" y={height - plotBottom + 4}>0</text>
    <path class="chart-area" d={areaPath()} fill={color}></path>
    <polyline class="chart-line" points={linePoints()} stroke={color}></polyline>
    {#each points as point, index}
      {@const value = valueAt(point)}
      <g class="chart-point">
        <title>{pointLabel(point)}</title>
        <circle class="point-target" cx={xAt(index)} cy={yAt(value)} r="10"></circle>
        <circle class="point-dot" cx={xAt(index)} cy={yAt(value)} r="3" fill={color}></circle>
        <foreignObject class="point-focus" x={xAt(index) - 10} y={yAt(value) - 10} width="20" height="20">
          <button aria-label={pointLabel(point)}></button>
        </foreignObject>
        <g class="chart-tooltip" transform={`translate(${tooltipX(index)} ${tooltipY(value)})`}>
          <rect x="-50" y="-22" width="100" height="34" rx="6"></rect>
          <text class="tooltip-value" y="-8">{value.toLocaleString()}</text>
          <text class="tooltip-time" y="6">{formatTime(point.capturedAt)}</text>
        </g>
      </g>
    {/each}
  </svg>
</div>
