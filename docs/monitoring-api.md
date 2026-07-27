# Monitoring API

`MapImmediateJobsDashboard("/jobs")` exposes the following stable endpoints under the selected prefix:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/overview` | State totals, schedules, and live nodes |
| `GET` | `/api/jobs?state=&queue=&search=&skip=&take=` | Filtered invocation history; `queue` is an exact persisted queue name |
| `GET` | `/api/jobs/{id}` | One invocation, including payload and latest error; `404` when absent |
| `GET` | `/api/batches?state=&skip=&take=` | Recent batches with aggregate progress |
| `GET` | `/api/batches/{id}` | One batch status; `404` when absent |
| `GET` | `/api/batches/{id}/members?state=&skip=&take=` | Filtered members of one batch |
| `GET` | `/api/batches/{id}/graph` | The batch dependency graph; `404` when absent |
| `GET` | `/api/batches/{id}/stream` | Named `status` and `graph` Server-Sent Events for one batch |
| `POST` | `/api/batches/{id}/cancel` | Cancel every non-terminal batch member |
| `DELETE` | `/api/batches/{id}` | Delete a terminal batch and its members |
| `GET` | `/api/recurring` | All code-defined and dynamic recurring schedules |
| `GET` | `/api/servers` | Scheduler-node heartbeat snapshots |
| `GET` | `/api/events` | Server-Sent Events snapshot stream |
| `POST` | `/api/jobs/{id}/retry` | Move a failed invocation to pending |
| `DELETE` | `/api/jobs/{id}` | Delete a terminal invocation |
| `POST` | `/api/recurring/{name}/trigger` | Enqueue one immediate invocation for a schedule |
| `POST` | `/api/recurring/{name}/pause` | Pause future scheduled occurrences |
| `POST` | `/api/recurring/{name}/resume` | Resume future scheduled occurrences |

Read endpoints return the public job, schedule, server, batch-status, member, and graph JSON shapes from `Immediate.Jobs.Shared`. Successful mutations return `202 Accepted` or `204 No Content`; missing resources return `404`, and an invalid state transition returns Problem Details with `409 Conflict`.

The global event stream emits `state` events whose `data` contains a `snapshot`, the latest jobs, and the latest batches; the event id is the snapshot's Unix timestamp in milliseconds. Batch streams emit `status` and `graph` events whenever either representation changes. Clients should reconnect or fall back to the corresponding JSON endpoints when SSE is unavailable.

Endpoints use the ASP.NET Core authorization metadata configured on `ImmediateJobsDashboardOptions`. If no policy is supplied, middleware rejects requests outside the `Development` environment.
