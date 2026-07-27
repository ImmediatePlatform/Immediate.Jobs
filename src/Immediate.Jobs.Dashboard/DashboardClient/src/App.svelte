<script>
  import { onMount } from 'svelte';
  import HistoryChart from './HistoryChart.svelte';
  import JobTable from './JobTable.svelte';
  import MetricCard from './MetricCard.svelte';

  let snapshot = null;
  let jobs = [];
  let view = 'overview';
  let connection = 'connecting';
  let search = '';
  let state = '';
  let queue = '';
  let error = '';
  let history = [];
  let selectedJob = null;

  const states = ['Scheduled', 'Pending', 'Active', 'Succeeded', 'Failed', 'Cancelled'];
  const fmt = value => value ? new Date(value).toLocaleString() : '—';
  const count = name => snapshot?.counts?.[name] ?? 0;

  function recordSnapshot(next) {
    const complete = countFrom(next, 'Succeeded') + countFrom(next, 'Failed');
    const previous = history.at(-1)?.complete ?? complete;
    history = [...history.slice(-29), {
      capturedAt: next.capturedAt,
      complete,
      throughput: Math.max(0, complete - previous),
      queued: countFrom(next, 'Pending') + countFrom(next, 'Scheduled')
    }];
    snapshot = next;
  }

  function countFrom(source, name) {
    return source?.counts?.[name] ?? 0;
  }

  async function api(path, init) {
    const response = await fetch(`api/${path}`, { headers: { accept: 'application/json' }, ...init });
    if (!response.ok && response.status !== 204)
      throw new Error((await response.text()) || `${response.status} ${response.statusText}`);
    return response.status === 204 ? null : response.json();
  }

  const visibleJobs = () => jobs.filter(job =>
    (!state || job.state === state) &&
    (!queue || job.queueName === queue) &&
    (!search || job.jobName.toLowerCase().includes(search.toLowerCase()))
  );

  function selectView(next) {
    view = next;
    selectedJob = null;
  }

  async function act(path, method = 'POST') {
    try {
      await api(path, { method });
    } catch (reason) {
      error = reason.message;
    }
  }

  onMount(() => {
    const events = new EventSource('api/events');
    events.onopen = () => {
      connection = 'live';
      error = '';
    };
    events.addEventListener('state', event => {
      const next = JSON.parse(event.data);
      recordSnapshot(next.snapshot);
      jobs = next.jobs;
      if (selectedJob)
        selectedJob = jobs.find(job => job.id === selectedJob.id) ?? null;
    });
    events.onerror = () => {
      connection = 'reconnecting';
    };
    return () => {
      events.close();
    };
  });
</script>

<header>
  <div class="brand"><span class="mark">I</span><div><strong>Immediate.Jobs</strong><small>background work, at a glance</small></div></div>
  <div class:live={connection === 'live'} class:offline={connection === 'reconnecting'} class="connection">{connection}</div>
</header>
<nav aria-label="Dashboard views">
  {#each ['overview', 'jobs', 'recurring', 'servers'] as item}
    <button class:active={view === item} onclick={() => selectView(item)}>{item[0].toUpperCase() + item.slice(1)}</button>
  {/each}
</nav>
<main>
  {#if error}<div class="error">{error}</div>{/if}
  {#if !snapshot}
    <div class="loading">Loading dashboard…</div>
  {:else if view === 'overview'}
    <div class="grid">
      {#each states as item}
        <MetricCard label={item} value={count(item)} />
      {/each}
    </div>
    <div class="charts">
      <article class="card chart"><div><strong>Throughput</strong><small>completed per update</small></div><HistoryChart points={history} valueKey="throughput" label="Throughput" /></article>
      <article class="card chart"><div><strong>Queue depth</strong><small>pending and scheduled</small></div><HistoryChart points={history} valueKey="queued" label="Queue depth" color="#77b7ff" /></article>
    </div>
    <div class="section-title"><h2>Recent jobs</h2><span>captured {fmt(snapshot.capturedAt)}</span></div>
    <JobTable rows={jobs.slice(0, 8)} {act} selected={selectedJob} select={job => selectedJob = job} />
  {:else if view === 'jobs'}
    <div class="section-title"><h2>Jobs</h2><span>{visibleJobs().length} shown</span></div>
    <div class="filters">
      <input bind:value={search} type="search" placeholder="Search by job name">
      <input bind:value={queue} type="search" placeholder="Filter by queue">
      <select bind:value={state}><option value="">All states</option>{#each states as item}<option>{item}</option>{/each}</select>
    </div>
    <JobTable rows={visibleJobs()} {act} selected={selectedJob} select={job => selectedJob = job} />
  {:else if view === 'recurring'}
    <div class="section-title"><h2>Recurring schedules</h2><span>{snapshot.recurring.length} configured</span></div>
    <div class="table-wrap">
      {#if snapshot.recurring.length === 0}<div class="empty">Nothing to show yet.</div>{:else}
        <table><thead><tr><th>Name</th><th>Job</th><th>Cron</th><th>Zone</th><th>Next run</th><th>Source</th><th>Actions</th></tr></thead>
        <tbody>{#each snapshot.recurring as schedule}<tr>
          <td><code>{schedule.name}</code></td><td>{schedule.jobName}</td><td><code>{schedule.cron}</code></td><td>{schedule.timeZone}</td><td>{fmt(schedule.nextRunAt)}</td><td>{schedule.isCodeDefined ? 'code' : 'dynamic'}</td>
          <td><span class="actions"><button class="action" onclick={() => act(`recurring/${encodeURIComponent(schedule.name)}/trigger`)}>Trigger</button><button class="action" onclick={() => act(`recurring/${encodeURIComponent(schedule.name)}/${schedule.isPaused ? 'resume' : 'pause'}`)}>{schedule.isPaused ? 'Resume' : 'Pause'}</button></span></td>
        </tr>{/each}</tbody></table>
      {/if}
    </div>
  {:else}
    <div class="section-title"><h2>Scheduler nodes</h2><span>{snapshot.servers.length} online</span></div>
    <div class="table-wrap">
      {#if snapshot.servers.length === 0}<div class="empty">Nothing to show yet.</div>{:else}
        <table><thead><tr><th>Worker</th><th>Heartbeat</th><th>Workers</th></tr></thead><tbody>{#each snapshot.servers as server}<tr><td><code>{server.workerId}</code></td><td>{fmt(server.lastHeartbeat)}</td><td>{server.activeWorkers} / {server.maxWorkers}</td></tr>{/each}</tbody></table>
      {/if}
    </div>
  {/if}
</main>
