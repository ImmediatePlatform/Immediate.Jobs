import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { compile } from 'svelte/compiler';
import { render } from 'svelte/server';

test('renders completed jobs returned by the dashboard API', async () => {
  const source = await readFile(new URL('../src/JobTable.svelte', import.meta.url), 'utf8');
  const compiled = compile(source, { filename: 'JobTable.svelte', generate: 'server' });
  const serverRuntime = JSON.stringify(import.meta.resolve('svelte/internal/server'));
  const moduleSource = compiled.js.code.replace("'svelte/internal/server'", serverRuntime);
  const moduleUrl = `data:text/javascript;base64,${Buffer.from(moduleSource).toString('base64')}`;
  const { default: JobTable } = await import(moduleUrl);
  const job = {
    id: '86bf8c31-d8e6-415b-8e92-45587a09fc52',
    jobName: 'SendGreeting',
    payload: '{"name":"Duke"}',
    context: '{"http-request":{"clientIpAddress":"127.0.0.1","userAgent":"curl/8.7.1"}}',
    state: 'Succeeded',
    attempt: 1,
    createdAt: '2026-07-21T11:59:59Z',
    dueAt: '2026-07-21T12:00:00Z',
    completedAt: '2026-07-21T12:00:01Z'
  };
  const props = {
    rows: [job],
    act: () => {},
    select: () => {}
  };
  const collapsed = render(JobTable, { props }).body;
  const expanded = render(JobTable, { props: { ...props, selected: job } }).body;

  assert.match(collapsed, /SendGreeting/);
  assert.match(collapsed, /Succeeded/);
  assert.doesNotMatch(collapsed, /Nothing to show yet/);
  assert.doesNotMatch(collapsed, /Payload/);
  assert.doesNotMatch(collapsed, /Context envelope/);
  assert.match(expanded, /Details for SendGreeting/);
  assert.match(expanded, /Payload/);
  assert.match(expanded, /Duke/);
  assert.match(expanded, /Context envelope/);
  assert.match(expanded, /http-request/);
  assert.match(expanded, /curl\/8\.7\.1/);
});
