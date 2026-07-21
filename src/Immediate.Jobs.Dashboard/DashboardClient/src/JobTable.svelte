<script>
  let { rows = [], act, selected, select } = $props();

  function fmt(value) {
    return value ? new Date(value).toLocaleString() : '—';
  }

  function pretty(value) {
    try { return JSON.stringify(JSON.parse(value), null, 2); }
    catch { return value; }
  }

  function isSelected(job) {
    return selected?.id === job.id;
  }

  function toggleDetails(job) {
    select(isSelected(job) ? null : job);
  }
</script>

<div class="table-wrap">
  {#if rows.length === 0}
    <div class="empty">Nothing to show yet.</div>
  {:else}
    <table>
      <thead><tr><th>Job</th><th>Queue</th><th>State</th><th>Attempt</th><th>Due</th><th>Completed</th><th>Actions</th></tr></thead>
      <tbody>
        {#each rows as job}
          <tr>
            <td><button class="job-link" aria-expanded={isSelected(job)} onclick={() => toggleDetails(job)}>{job.jobName}</button></td>
            <td><code>{job.queueName}</code></td>
            <td><span class="state {job.state.toLowerCase()}">{job.state}</span></td>
            <td>{job.attempt}</td>
            <td>{fmt(job.dueAt)}</td>
            <td>{fmt(job.completedAt)}</td>
            <td>
              <span class="actions">
                <button class="action" aria-expanded={isSelected(job)} onclick={() => toggleDetails(job)}>{isSelected(job) ? 'Hide' : 'Details'}</button>
                {#if job.state === 'Failed'}
                  <button class="action" onclick={() => act(`jobs/${job.id}/retry`)}>Retry</button>
                  <button class="action danger" onclick={() => act(`jobs/${job.id}`, 'DELETE')}>Delete</button>
                {/if}
              </span>
            </td>
          </tr>
          {#if isSelected(job)}
            <tr class="job-detail">
              <td colspan="7">
                <section class="detail" aria-label={`Details for ${job.jobName}`}>
                  <div class="section-title"><h2>{job.jobName}</h2><button class="action" onclick={() => select(null)}>Close</button></div>
                  <dl><div><dt>Invocation</dt><dd><code>{job.id}</code></dd></div><div><dt>Queue</dt><dd><code>{job.queueName}</code></dd></div><div><dt>State</dt><dd><span class="state {job.state.toLowerCase()}">{job.state}</span></dd></div><div><dt>Attempt</dt><dd>{job.attempt}</dd></div><div><dt>Created</dt><dd>{fmt(job.createdAt)}</dd></div><div><dt>Due</dt><dd>{fmt(job.dueAt)}</dd></div><div><dt>Completed</dt><dd>{fmt(job.completedAt)}</dd></div></dl>
                  <h3>Payload</h3><pre>{pretty(job.payload)}</pre>
                  {#if job.context}
                    <h3>Context envelope</h3><pre>{pretty(job.context)}</pre>
                  {/if}
                  {#if job.lastError}<h3>Latest error</h3><pre class="failure">{job.lastError}</pre>{/if}
                </section>
              </td>
            </tr>
          {/if}
        {/each}
      </tbody>
    </table>
  {/if}
</div>
