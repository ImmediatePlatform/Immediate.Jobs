<script>
  import { onMount } from 'svelte';

  import BatchTable from './BatchTable.svelte';
  import HistoryChart from './HistoryChart.svelte';
  import JobTable from './JobTable.svelte';
  import MetricCard from './MetricCard.svelte';
  import WorkflowGraph from './WorkflowGraph.svelte';

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
  let batches = [];
  let selectedBatch = null;
  let batchGraph = null;
  let batchSearch = '';
  let batchState = '';
  let batchEvents = null;
  let filteredBatches = [];
  let jobPage = {
    items: [],
    skip: 0,
    take: 50,
    hasNext: false
  };
  let jobPageIndex = 0;
  let jobFilterSignature = '';
  let jobRefreshTimer = null;
  let jobsPageAbortController = null;
  let mounted = false;

  const jobPageSize = 50;

  const states = [
    'Scheduled',
    'Pending',
    'AwaitingContinuation',
    'AwaitingParameters',
    'Active',
    'Succeeded',
    'Failed',
    'Cancelled'
  ];

  $: filteredBatches = batches.filter(batch => (
    (!batchState || batch.state === batchState) &&
    (!batchSearch || batch.id.toLowerCase().includes(batchSearch.toLowerCase()))
  ));

  $: {
    const nextFilterSignature = `${search}\u0000${queue}\u0000${state}`;
    if (mounted && nextFilterSignature !== jobFilterSignature) {
      jobFilterSignature = nextFilterSignature;
      jobPageIndex = 0;
      selectedJob = null;
      scheduleJobsPageRefresh();
    }
  }

  function formatDate(value) {
    return value ? new Date(value).toLocaleString() : '—';
  }

  function count(name) {
    return snapshot?.counts?.[name] ?? 0;
  }

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
    const response = await fetch(`api/${path}`, {
      headers: { accept: 'application/json' },
      ...init
    });

    if (!response.ok && response.status !== 204) {
      throw new Error((await response.text()) || `${response.status} ${response.statusText}`);
    }

    return response.status === 204 ? null : response.json();
  }

  function selectView(next) {
    view = next;
    selectedJob = null;

    if (next !== 'batches') {
      closeBatchStream();
    }

    if (next === 'jobs') {
      refreshJobsPage();
    }
  }

  function scheduleJobsPageRefresh() {
    clearTimeout(jobRefreshTimer);
    jobRefreshTimer = setTimeout(refreshJobsPage, 250);
  }

  async function refreshJobsPage() {
    jobsPageAbortController?.abort();
    const abortController = new AbortController();
    jobsPageAbortController = abortController;

    const parameters = new URLSearchParams({
      skip: String(jobPageIndex * jobPageSize),
      take: String(jobPageSize)
    });
    if (search) {
      parameters.set('search', search);
    }
    if (queue) {
      parameters.set('queue', queue);
    }
    if (state) {
      parameters.set('state', state);
    }

    try {
      const nextPage = await api(`jobs?${parameters}`, {
        signal: abortController.signal
      });
      if (jobsPageAbortController !== abortController) {
        return;
      }

      jobPage = nextPage;
      if (selectedJob) {
        selectedJob = jobPage.items.find(job => job.id === selectedJob.id) ?? selectedJob;
      }
    } catch (reason) {
      if (reason.name !== 'AbortError') {
        error = reason.message;
      }
    }
  }

  function showPreviousJobsPage() {
    if (jobPageIndex === 0) {
      return;
    }

    jobPageIndex--;
    selectedJob = null;
    refreshJobsPage();
  }

  function showNextJobsPage() {
    if (!jobPage.hasNext) {
      return;
    }

    jobPageIndex++;
    selectedJob = null;
    refreshJobsPage();
  }

  function applyBatchList(nextBatches) {
    batches = nextBatches;

    if (!selectedBatch) {
      return;
    }

    const updatedSelection = batches.find(batch => batch.id === selectedBatch.id);
    if (updatedSelection) {
      selectedBatch = updatedSelection;
      return;
    }

    selectedBatch = null;
    batchGraph = null;
    closeBatchStream();
  }

  function updateBatchStatus(status) {
    const existingIndex = batches.findIndex(batch => batch.id === status.id);
    if (existingIndex === -1) {
      batches = [status, ...batches];
    } else {
      batches = batches.map(batch => {
        if (batch.id === status.id) {
          return status;
        }

        return batch;
      });
    }

    if (selectedBatch?.id === status.id) {
      selectedBatch = status;
    }
  }

  function closeBatchStream() {
    batchEvents?.close();
    batchEvents = null;
  }

  async function selectBatch(batch) {
    closeBatchStream();
    selectedBatch = batch;
    batchGraph = null;

    connectBatchStream(batch.id);
    await refreshSelectedBatchGraph();
  }

  function connectBatchStream(batchId) {
    const encodedBatchId = encodeURIComponent(batchId);
    const events = new EventSource(`api/batches/${encodedBatchId}/stream`);
    batchEvents = events;

    events.addEventListener('status', event => {
      if (events !== batchEvents || selectedBatch?.id !== batchId) {
        return;
      }

      updateBatchStatus(JSON.parse(event.data));
    });
    events.addEventListener('graph', event => {
      if (events !== batchEvents || selectedBatch?.id !== batchId) {
        return;
      }

      batchGraph = JSON.parse(event.data);
    });
  }

  async function refreshSelectedBatchGraph() {
    if (!selectedBatch) {
      return;
    }

    const batchId = selectedBatch.id;

    try {
      const graph = await api(`batches/${encodeURIComponent(batchId)}/graph`);
      if (selectedBatch?.id === batchId) {
        batchGraph = graph;
      }
    } catch (reason) {
      error = reason.message;
    }
  }

  async function selectGraphJob(jobId) {
    try {
      selectedJob = await api(`jobs/${encodeURIComponent(jobId)}`);
    } catch (reason) {
      error = reason.message;
    }
  }

  async function openBatch(batchId) {
    try {
      const batch = batches.find(candidate => candidate.id === batchId)
        ?? await api(`batches/${encodeURIComponent(batchId)}`);
      view = 'batches';
      selectedJob = null;
      await selectBatch(batch);
    } catch (reason) {
      error = reason.message;
    }
  }

  async function act(path, method = 'POST') {
    try {
      await api(path, { method });
      if (view === 'jobs') {
        await refreshJobsPage();
      }
    } catch (reason) {
      error = reason.message;
    }
  }

  onMount(() => {
    mounted = true;
    jobFilterSignature = `${search}\u0000${queue}\u0000${state}`;
    refreshJobsPage();

    const events = new EventSource('api/events');
    events.onopen = () => {
      connection = 'live';
      error = '';
    };
    events.addEventListener('state', event => {
      const next = JSON.parse(event.data);
      recordSnapshot(next.snapshot);
      jobs = next.jobs;
      applyBatchList(next.batches ?? []);

      if (selectedJob && view === 'overview') {
        selectedJob = jobs.find(job => job.id === selectedJob.id) ?? null;
      }
      if (view === 'jobs') {
        refreshJobsPage();
      }
    });
    events.onerror = () => {
      connection = 'reconnecting';
    };
    return () => {
      events.close();
      closeBatchStream();
      clearTimeout(jobRefreshTimer);
      jobsPageAbortController?.abort();
    };
  });
</script>

<header>
  <div class="brand">
    <span class="mark">I</span>
    <div>
      <strong>Immediate.Jobs</strong>
      <small>background work, at a glance</small>
    </div>
  </div>
  <div
    class="connection"
    class:live={connection === 'live'}
    class:offline={connection === 'reconnecting'}
  >
    {connection}
  </div>
</header>

<nav aria-label="Dashboard views">
  {#each ['overview', 'jobs', 'batches', 'recurring', 'servers'] as item}
    <button class:active={view === item} onclick={() => selectView(item)}>
      {item[0].toUpperCase() + item.slice(1)}
    </button>
  {/each}
</nav>

<main>
  {#if error}
    <div class="error">{error}</div>
  {/if}

  {#if !snapshot}
    <div class="loading">Loading dashboard…</div>
  {:else if view === 'overview'}
    <div class="grid">
      {#each states as item}
        <MetricCard label={item} value={count(item)} />
      {/each}
    </div>
    <div class="charts">
      <article class="card chart">
        <div>
          <strong>Throughput</strong>
          <small>completed per update</small>
        </div>
        <HistoryChart points={history} valueKey="throughput" label="Throughput" />
      </article>
      <article class="card chart">
        <div>
          <strong>Queue depth</strong>
          <small>pending and scheduled</small>
        </div>
        <HistoryChart
          points={history}
          valueKey="queued"
          label="Queue depth"
          color="#77b7ff"
        />
      </article>
    </div>
    <div class="section-title">
      <h2>Recent jobs</h2>
      <span>captured {formatDate(snapshot.capturedAt)}</span>
    </div>
    <JobTable
      rows={jobs.slice(0, 8)}
      {act}
      selected={selectedJob}
      select={job => selectedJob = job}
      selectBatch={openBatch}
    />
  {:else if view === 'jobs'}
    <div class="section-title">
      <h2>Jobs</h2>
      <span>{jobPage.items.length} shown</span>
    </div>
    <div class="filters">
      <input bind:value={search} type="search" placeholder="Search by job name">
      <input bind:value={queue} type="search" placeholder="Filter by queue">
      <select bind:value={state}>
        <option value="">All states</option>
        {#each states as item}
          <option>{item}</option>
        {/each}
      </select>
    </div>
    <JobTable
      rows={jobPage.items}
      {act}
      selected={selectedJob}
      select={job => selectedJob = job}
      selectBatch={openBatch}
    />
    <div class="pagination" aria-label="Jobs pagination">
      <button
        class="action"
        disabled={jobPageIndex === 0}
        onclick={showPreviousJobsPage}
      >
        Previous
      </button>
      <span>Page {jobPageIndex + 1}</span>
      <button
        class="action"
        disabled={!jobPage.hasNext}
        onclick={showNextJobsPage}
      >
        Next
      </button>
    </div>
  {:else if view === 'batches'}
    <div class="section-title">
      <h2>Batches</h2>
      <span>{filteredBatches.length} shown</span>
    </div>
    <div class="filters">
      <input bind:value={batchSearch} type="search" placeholder="Search by batch ID">
      <select bind:value={batchState}>
        <option value="">All states</option>
        {#each ['Executing', 'Succeeded', 'Failed', 'Cancelled'] as item}
          <option>{item}</option>
        {/each}
      </select>
    </div>
    <BatchTable rows={filteredBatches} select={selectBatch} {act} />

    {#if selectedBatch}
      <div class="section-title workflow-title">
        <h2>Workflow</h2>
        <span>{selectedBatch.id}</span>
      </div>
      <WorkflowGraph graph={batchGraph} select={selectGraphJob} />
      {#if selectedJob}
        <article class="card workflow-detail">
          <strong>{selectedJob.jobName}</strong>
          <span class="state {selectedJob.state.toLowerCase()}">{selectedJob.state}</span>
          {#if selectedJob.state === 'Failed'}
            <button
              class="action"
              onclick={() => act(`jobs/${encodeURIComponent(selectedJob.id)}/retry`)}
            >
              Retry job
            </button>
          {/if}
          <dl>
            <div>
              <dt>Job ID</dt>
              <dd><code>{selectedJob.id}</code></dd>
            </div>
            <div>
              <dt>Attempts</dt>
              <dd>{selectedJob.attempt}</dd>
            </div>
            <div>
              <dt>Created</dt>
              <dd>{formatDate(selectedJob.createdAt)}</dd>
            </div>
            <div>
              <dt>Due</dt>
              <dd>{formatDate(selectedJob.dueAt)}</dd>
            </div>
            <div>
              <dt>Completed</dt>
              <dd>{formatDate(selectedJob.completedAt)}</dd>
            </div>
          </dl>
          <details>
            <summary>Payload</summary>
            <pre>{selectedJob.payload}</pre>
          </details>
          {#if selectedJob.lastError}
            <pre class="failure">{selectedJob.lastError}</pre>
          {/if}
        </article>
      {/if}
    {/if}
  {:else if view === 'recurring'}
    <div class="section-title">
      <h2>Recurring schedules</h2>
      <span>{snapshot.recurring.length} configured</span>
    </div>
    <div class="table-wrap">
      {#if snapshot.recurring.length === 0}
        <div class="empty">Nothing to show yet.</div>
      {:else}
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Job</th>
              <th>Cron</th>
              <th>Zone</th>
              <th>Next run</th>
              <th>Source</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            {#each snapshot.recurring as schedule}
              <tr>
                <td><code>{schedule.name}</code></td>
                <td>{schedule.jobName}</td>
                <td><code>{schedule.cron}</code></td>
                <td>{schedule.timeZone}</td>
                <td>{formatDate(schedule.nextRunAt)}</td>
                <td>{schedule.isCodeDefined ? 'code' : 'dynamic'}</td>
                <td>
                  <span class="actions">
                    <button
                      class="action"
                      onclick={() => act(`recurring/${encodeURIComponent(schedule.name)}/trigger`)}
                    >
                      Trigger
                    </button>
                    <button
                      class="action"
                      onclick={() => act(
                        `recurring/${encodeURIComponent(schedule.name)}/${schedule.isPaused ? 'resume' : 'pause'}`
                      )}
                    >
                      {schedule.isPaused ? 'Resume' : 'Pause'}
                    </button>
                  </span>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>
  {:else}
    <div class="section-title">
      <h2>Scheduler nodes</h2>
      <span>{snapshot.servers.length} online</span>
    </div>
    <div class="table-wrap">
      {#if snapshot.servers.length === 0}
        <div class="empty">Nothing to show yet.</div>
      {:else}
        <table>
          <thead>
            <tr>
              <th>Worker</th>
              <th>Heartbeat</th>
              <th>Workers</th>
            </tr>
          </thead>
          <tbody>
            {#each snapshot.servers as server}
              <tr>
                <td><code>{server.workerId}</code></td>
                <td>{formatDate(server.lastHeartbeat)}</td>
                <td>{server.activeWorkers} / {server.maxWorkers}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>
  {/if}
</main>
