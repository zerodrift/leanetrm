# Managed risk workers

Open **Jobs & Infrastructure → Risk worker pool**. A workspace member can see requested replicas, the last count observed in the Docker service specification, worker state, node, engine version and last heartbeat. The panel refreshes every five seconds while open. Swarm service replica count expresses desired placement. An idle or busy heartbeat comes from an actual worker process.

**Run 1 worker** and **Run 2 workers** set the desired count, up to the installed maximum. **Stop after current jobs** immediately closes new claims by this pool. For a downscale the controller waits for active tasks to return from their financial calculation before updating the service replica count. Queued jobs remain in PostgreSQL. Independently registered legacy workers continue to serve their own compatible claims.

**Worker app logs** shows recent worker connections, job claims and the managed worker's completion, failure or cancellation messages. **Node events** shows changes in Swarm's observed health, scheduling availability and presence. Open either view to read the latest 100 entries from its bounded workspace history. Host journals, arbitrary container output and interactive shells stay with the host operator.

## Installation

1. Apply migration 0366 and deploy compatible API, controller and risk-worker binaries. Register the controller and its authenticated manager endpoint using the existing automation setup. Register a dedicated risk-worker login for this pool in the same tenant and workspace.
2. Render a service specification with `python3 tools/risk_worker_pool.py --profile /private/pool.json --output /private/service.json --stopped`. The profile takes `protocol` (`bounded_native_risk_pool_v1`), `id` (pool UUID), `image` (repository and SHA-256 digest), `architecture` (`arm64` or `amd64`), `replicas` (1 or 2), `databaseNetworkId`, `configSecretId`, `connectionSecretId` and `databaseRootSecretId`. Supply `configSecretName`, `connectionSecretName` and `databaseRootSecretName` alongside the corresponding IDs. Use the registered Swarm secret names from `docker secret inspect`. The container mount filenames are fixed by the renderer. Each eligible Linux worker node needs the label `openetrm.compute=risk` and capacity for a 1 CPU, 2 GiB reservation in addition to existing services and standalone containers.
3. Supply the worker configuration, connection and database trust root as separate secrets. The settings file points to `/run/secrets/risk-worker.connection`. Network connections require `SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Root Certificate=/run/secrets/database-root.pem`. The service supplies the Swarm task ID, slot and node ID to each process. The registered login authenticates the pool. These task labels identify workers within that trusted service.
4. Using the installation's authenticated manager, create the reviewed service initially scaled to zero. Capture its exact service ID and the `Spec` returned by `GET /v1.47/services/{id}` on the same authenticated manager endpoint as the controller. An installation administrator registers it once in `automation.risk_pools(cluster_id,worker_login,service_id,approved_spec,maximum)`. Use the reviewed inspected JSON object for `approved_spec`, including the normalized image digest, command, labels, network and secret references. The installation owns this registration. API users cannot submit a Docker specification, choose an image or select a host endpoint.
5. Assign `ScaleWorkerPool` and `ViewWorkerDiagnostics` to local roles as needed. Scaling also requires an unexpired installation operator registration with capability `ManageInfrastructure`. Neither new permission is assigned by the migration.

The controller compares the inspected service against its registered specification and uses Docker's service version for updates. Replica count and the controller's generation label can change. The desired generation and request receipt are committed before dispatch. A subsequent inspection reconciles a lost response. A stale controller observation closes new claims after two minutes. Unexpected overlapping task slots or nodes suspend admission. Stale worker heartbeats are shown separately from idle workers.

## Local verification

With the repository SDK, PostgreSQL tools and Chromium available, run:

```sh
python3 tools/run_preview.py --historical-pool-check --browser-check \
  --pg-bin "$PG_BIN" --dotnet "$DOTNET" --browser "$BROWSER"
```

The disposable preview starts two local worker processes and submits three independent historical quote scenarios through the risk API. A retained portfolio and valuation clock stay fixed while the settlement quote changes. For each scenario the check compares the worker's native report with direct repricing and observes distinct, concurrent claims. Browser checks cover scaling, uncertain receipts, worker app logs and node events. The manager transport and task placement are simulated.

## Disposable multi-node qualification

A separate integration run used Docker Engine 29.5.2 on three arm64 Linux VMs on one physical host. One VM managed the Swarm and two were eligible workers. Each VM had two vCPUs and 4 GiB RAM. The controller used mutual TLS and Engine API v1.47. Both workers pulled the same image by digest from an authenticated fixture registry. The worker service used an internal encrypted overlay and verified the certificate of the disposable PostgreSQL 18 database.

| Exercise | Observed result |
|---|---|
| Submit three independent risk jobs with the pool stopped | Each repriced the same 24 synthetic positions in five instruments at the December 18, 2020 valuation clock, using settlement quote changes from December 15, 16 or 17. Direct reports supplied the expected results. |
| Select **Run 2 workers** in Jobs | Two distinct tasks on separate worker nodes held different jobs and leases concurrently. |
| Hold both calculations at a database read, then select **Stop after current jobs** | The controller reported draining with two replicas. Both active jobs finished after release, the third stayed queued, and the service reached zero. |
| Select **Run 1 worker** and terminate its container during the third calculation | Swarm replaced the task. After lease expiry the third job completed on attempt two. All three completed reports matched direct repricing for inputs, context, snapshot, scenario, factors, totals and exclusions. |
| Inspect a running worker and open diagnostics in Jobs | Linux enforced one CPU, 2 GiB memory and 128 PIDs. The worker ran as UID 1654 with zero effective capabilities, a read-only root filesystem, no new privileges and no published ports. The browser displayed worker completion messages and node events. |

For deployment, repeat this exercise with the intended image and private database route. Check both Swarm reservations and standalone API, database and worker container consumption on the target hosts before assigning workers. This fixture measures functional behavior at small scale. Deployment-host capacity, large-portfolio throughput and latency still need measurement.
