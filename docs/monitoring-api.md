# Monitoring API

`MapImmediateJobsDashboard("/jobs")` exposes the following stable endpoints under the selected prefix:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/overview` | State totals, schedules, and live nodes |
| `GET` | `/api/jobs?state=&queue=&search=&skip=&take=` | Filtered invocation history; `queue` is an exact persisted queue name |
| `GET` | `/api/jobs/{id}` | One invocation, including payload and latest error; `404` when absent |
| `GET` | `/api/recurring` | All code-defined and dynamic recurring schedules |
| `GET` | `/api/servers` | Scheduler-node heartbeat snapshots |
| `GET` | `/api/events` | Server-Sent Events snapshot stream |
| `POST` | `/api/jobs/{id}/retry` | Move a failed invocation to pending |
| `DELETE` | `/api/jobs/{id}` | Delete a terminal invocation |
| `POST` | `/api/recurring/{name}/trigger` | Enqueue one immediate invocation for a schedule |
| `POST` | `/api/recurring/{name}/pause` | Pause future ticks |
| `POST` | `/api/recurring/{name}/resume` | Resume future ticks |

Read endpoints return the public `JobMonitoringSnapshot`, `JobRecord`, `RecurringJobSchedule`, and `JobServerSnapshot` JSON shapes from `Immediate.Jobs`. Successful mutations return `202 Accepted` or `204 No Content`; missing resources return `404`, and an invalid state transition returns Problem Details with `409 Conflict`.

The event stream emits `snapshot` events whose `data` is a `JobMonitoringSnapshot`; the event id is the snapshot's Unix timestamp in milliseconds. Clients should reconnect or fall back to `GET /api/overview` polling when SSE is unavailable.

Endpoints use the ASP.NET Core authorization metadata configured on `ImmediateJobsDashboardOptions`. If no policy is supplied, middleware rejects requests outside the `Development` environment.
