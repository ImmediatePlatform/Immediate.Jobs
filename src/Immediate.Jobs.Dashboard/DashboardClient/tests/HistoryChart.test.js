import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { compile } from 'svelte/compiler';
import { render } from 'svelte/server';

test('renders exact hover and focus values for chart history', async () => {
  const source = await readFile(new URL('../src/HistoryChart.svelte', import.meta.url), 'utf8');
  const compiled = compile(source, { filename: 'HistoryChart.svelte', generate: 'server' });
  const serverRuntime = JSON.stringify(import.meta.resolve('svelte/internal/server'));
  const moduleSource = compiled.js.code.replace("'svelte/internal/server'", serverRuntime);
  const moduleUrl = `data:text/javascript;base64,${Buffer.from(moduleSource).toString('base64')}`;
  const { default: HistoryChart } = await import(moduleUrl);
  const points = [
    { capturedAt: '2026-07-21T12:00:00Z', queued: 3 },
    { capturedAt: '2026-07-21T12:00:05Z', queued: 17 }
  ];

  const output = render(HistoryChart, {
    props: { points, valueKey: 'queued', label: 'Queue depth', color: '#77b7ff' }
  }).body;

  assert.match(output, /Recent queue depth history/);
  assert.match(output, /Queue depth: 3 at/);
  assert.match(output, /Queue depth: 17 at/);
  assert.match(output, /<button aria-label="Queue depth: 17 at/);
  assert.match(output, /class="chart-line"/);
  assert.match(output, /class="chart-tooltip"/);
});
