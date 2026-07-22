<script>
  let { rows = [], select = () => {}, act = () => {} } = $props();

  function width(value, total) {
    return total ? `${value / total * 100}%` : '0%';
  }
</script>

<div class="table-wrap">
  {#if rows.length === 0}
    <div class="empty">No batches have been committed yet.</div>
  {:else}
    <table>
      <thead>
        <tr>
          <th>Batch</th>
          <th>State</th>
          <th>Progress</th>
          <th>Members</th>
          <th>Created</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        {#each rows as batch}
          <tr>
            <td>
              <button class="job-link" onclick={() => select(batch)}>
                {batch.id}
              </button>
            </td>
            <td>
              <span class="state {batch.state.toLowerCase()}">{batch.state}</span>
            </td>
            <td class="batch-progress-cell">
              <div
                class="batch-progress"
                aria-label={`${batch.total - batch.remaining} of ${batch.total} settled`}
              >
                <span class="succeeded" style:width={width(batch.succeeded, batch.total)}></span>
                <span class="failed" style:width={width(batch.failed, batch.total)}></span>
                <span class="cancelled" style:width={width(batch.cancelled, batch.total)}></span>
                <span class="remaining" style:width={width(batch.remaining, batch.total)}></span>
              </div>
            </td>
            <td>{batch.total - batch.remaining} / {batch.total}</td>
            <td>{new Date(batch.createdAt).toLocaleString()}</td>
            <td>
              <span class="actions">
                {#if batch.state === 'Executing'}
                  <button
                    class="action danger"
                    onclick={() => act(`batches/${encodeURIComponent(batch.id)}/cancel`)}
                  >
                    Cancel
                  </button>
                {:else}
                  <button
                    class="action danger"
                    onclick={() => act(`batches/${encodeURIComponent(batch.id)}`, 'DELETE')}
                  >
                    Delete
                  </button>
                {/if}
              </span>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/if}
</div>
