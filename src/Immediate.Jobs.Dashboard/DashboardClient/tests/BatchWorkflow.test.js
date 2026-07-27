import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { compile } from 'svelte/compiler';
import { render } from 'svelte/server';

async function loadComponent(name) {
  const source = await readFile(new URL(`../src/${name}.svelte`, import.meta.url), 'utf8');
  const compiled = compile(source, { filename: `${name}.svelte`, generate: 'server' });
  const serverRuntime = JSON.stringify(import.meta.resolve('svelte/internal/server'));
  const moduleSource = compiled.js.code.replace("'svelte/internal/server'", serverRuntime);
  const moduleUrl = `data:text/javascript;base64,${Buffer.from(moduleSource).toString('base64')}`;
  return (await import(moduleUrl)).default;
}

test('tracks streamed batch updates through an explicit reactive filter', async () => {
  const source = await readFile(new URL('../src/App.svelte', import.meta.url), 'utf8');

  assert.match(source, /\$: filteredBatches = batches\.filter/);
  assert.match(source, /applyBatchList\(next\.batches \?\? \[\]\)/);
  assert.match(source, /rows=\{filteredBatches\}/);
});

test('requests jobs in server-side pages of fifty', async () => {
  const source = await readFile(new URL('../src/App.svelte', import.meta.url), 'utf8');

  assert.match(source, /const jobPageSize = 50/);
  assert.match(source, /skip: String\(jobPageIndex \* jobPageSize\)/);
  assert.match(source, /rows=\{jobPage\.items\}/);
  assert.match(source, /disabled=\{!jobPage\.hasNext\}/);
});

test('renders segmented batch progress and lifecycle actions', async () => {
  const BatchTable = await loadComponent('BatchTable');
  const output = render(BatchTable, {
    props: {
      rows: [{
        id: 'campaign-batch',
        state: 'Executing',
        total: 10,
        succeeded: 6,
        failed: 1,
        cancelled: 1,
        remaining: 2,
        createdAt: '2026-07-21T12:00:00Z'
      }]
    }
  }).body;

  assert.match(output, /campaign-batch/);
  assert.match(output, /8 of 10 settled/);
  assert.match(output, /class="succeeded" style="width: 60%;/);
  assert.match(output, />Cancel</);
});

test('renders fan-out and join dependencies as a workflow graph', async () => {
  const WorkflowGraph = await loadComponent('WorkflowGraph');
  const output = render(WorkflowGraph, {
    props: {
      graph: {
        batchId: 'deploy',
        nodes: [
          { jobId: 'build', jobName: 'Build', state: 'Succeeded' },
          { jobId: 'region-a', jobName: 'Deploy A', state: 'Active' },
          { jobId: 'region-b', jobName: 'Deploy B', state: 'Pending' },
          {
            jobId: 'smoke',
            jobName: 'order-record-fraud-assessment',
            state: 'AwaitingContinuation'
          }
        ],
        edges: [
          { childJobId: 'region-a', parentJobId: 'build', trigger: 'AllSucceeded' },
          { childJobId: 'region-b', parentJobId: 'build', trigger: 'AllSucceeded' },
          { childJobId: 'smoke', parentJobId: 'region-a', trigger: 'AllComplete' },
          { childJobId: 'smoke', parentJobId: 'region-b', trigger: 'AllComplete' }
        ]
      }
    }
  }).body;

  assert.match(output, /Batch dependency graph/);
  assert.match(output, /Build/);
  assert.match(output, /Deploy A/);
  assert.match(output, /order-record-fraud-assessment/);
  assert.match(output, /workflow-edge dashed/);
  assert.match(output, /data-job-id="region-a"/);

  const longNode = output.match(
    /data-job-id="smoke"[\s\S]*?<rect width="([\d.]+)"/
  );
  assert.ok(longNode);
  assert.ok(Number(longNode[1]) > 170);

  const edgePaths = [...output.matchAll(/class="workflow-edge[^\"]*" d="([^"]+)"/g)];
  assert.notEqual(edgePaths[2][1], edgePaths[3][1]);
});

test('follows the active workflow job when it leaves the graph viewport', async () => {
  const source = await readFile(new URL('../src/WorkflowGraph.svelte', import.meta.url), 'utf8');

  assert.match(source, /bind:this=\{workflowViewport\}/);
  assert.match(source, /node\.state === 'Active'/);
  assert.match(source, /viewport\.scrollIntoView\(\{/);
  assert.match(source, /viewport\.scrollTo\(\{/);
});
