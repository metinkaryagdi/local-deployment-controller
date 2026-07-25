# Local Deployment Controller

A self-hosted mini PaaS / CI-CD engine for a Windows box running Docker Desktop (WSL2 backend).
Paste a Git URL and an `.env` into the dashboard; the controller clones the repository to
`C:\Deployments\<project>`, injects the configuration, runs `docker compose up -d --build`
and streams the whole build to your browser over Server-Sent Events.

```
┌──────────────┐  fetch/EventSource  ┌────────────────────────┐  Process   ┌──────────┐
│  wwwroot SPA │ ──────────────────► │  .NET 9 Minimal API    │ ─────────► │ git      │
│  (Tailwind)  │ ◄────── SSE ─────── │  Channel<T> job queue  │            │ docker   │
└──────────────┘                     └────────────────────────┘            └──────────┘
```

## Requirements

| Component      | Version used here | Notes                                       |
| -------------- | ----------------- | ------------------------------------------- |
| .NET SDK       | 8, 9 or 10        | project targets `net9.0`                    |
| Git            | any recent        | must be on `PATH`                           |
| Docker Desktop | 29.x              | WSL2 backend, `docker compose` v2 on `PATH` |

## Run it

```bash
dotnet run --project src/DeployController/DeployController.csproj
```

Then open <http://localhost:5000>. The API also binds `0.0.0.0`, so another machine on the
LAN can reach the dashboard at `http://<this-machine-ip>:5000`.

> Windows Firewall will prompt the first time. Allow it on **private networks** only —
> the controller has no authentication and can build and run arbitrary code from a Git URL,
> so it must never be exposed to the internet.

## Install it on the target machine

The controller is meant to live on the box that owns Docker Desktop, not on your workstation.
Build a self-contained package (no .NET required on the target) with:

```bash
powershell -ExecutionPolicy Bypass -File .\build-package.ps1 -Zip
```

That produces `dist\LocalDeploymentController\` (~47 MB, single-file `win-x64` exe + `wwwroot` +
the scripts from `packaging\`) and `dist\LocalDeploymentController.zip`. Copy the folder to the
target machine and run, from an **elevated** PowerShell in that folder:

```bash
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

`setup.ps1` verifies git/docker, creates the deployment root, writes the port and base directory
into `appsettings.json`, adds the inbound firewall rule and registers a **scheduled task at logon**
(Docker Desktop only runs inside a user session, so a boot-time Windows Service would start before
the daemon exists). `uninstall.ps1` reverses all of it and leaves deployed projects untouched.

Turkish step-by-step instructions for whoever installs it: [`packaging/KURULUM.md`](packaging/KURULUM.md).

## Configuration — `appsettings.json`

```json
{
  "Urls": "http://0.0.0.0:5000",
  "Deployment": {
    "BaseDirectory": "C:\\Deployments",
    "GitExecutable": "git",
    "DockerExecutable": "docker",
    "GitTimeoutSeconds": 900,
    "DockerBuildTimeoutSeconds": 3600,
    "DockerQuickTimeoutSeconds": 180,
    "MaxJobHistory": 50,
    "MaxLogLinesPerJob": 20000,
    "PublicHost": null
  }
}
```

Per-project metadata (repo, branch, commit, compose file, last deploy time) is persisted as
JSON under `C:\Deployments\.state\` so the dashboard survives a restart.

## The deployment contract

A target repository must have **one** of the following at its root:

1. `docker-compose.yml` / `docker-compose.yaml` / `compose.yml` / `compose.yaml` — used as-is.
2. `Dockerfile` — the controller generates `docker-compose.deploycontroller.yml` for you:
   one `app` service, `restart: unless-stopped`, `env_file: .env`, and
   `"<hostPort>:<containerPort>"` where the container port is taken from `PORT` /
   `APP_PORT` / `SERVER_PORT` in your `.env` (falling back to the host port, then 80).

If you supply a host port and write your own compose file, reference it as `${HOST_PORT}` —
the controller appends `HOST_PORT=<port>` to the generated `.env` when you do not define it
yourself.

## What a deploy actually does

| Step | New project                                            | Existing project                                                                                                 |
| ---- | ------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------- |
| 1    | `git clone --progress -b <branch> <url> <dir>`          | delete stale `*.lock` files, re-point `origin`, `git fetch --all --prune`, `git checkout -B`, `git reset --hard origin/<branch>`, `git clean -fd` |
| 2    | write `.env` (LF, no BOM) + any extra injected files    | same                                                                                                              |
| 3    | resolve or generate the compose file                    | same                                                                                                              |
| 4    | `docker compose -p <project> up -d --build --remove-orphans` | same                                                                                                         |
| 5    | read back `docker ps` and record the container state    | same                                                                                                              |

Jobs run on a background `Channel<T>` worker, **one at a time** — concurrent
`docker build` runs on a single daemon fight over the build cache and produce unreadable
interleaved logs. The HTTP request only enqueues, so no request thread ever blocks on a build.

## API

| Method   | Route                                                | Description                                                        |
| -------- | ---------------------------------------------------- | ------------------------------------------------------------------ |
| `GET`    | `/api/health`                                        | Controller status, base directory, Docker server version.          |
| `GET`    | `/api/deployments`                                   | Every project with status, ports, containers, commit, timestamps.  |
| `GET`    | `/api/deployments/{name}`                            | A single project.                                                  |
| `POST`   | `/api/deploy`                                        | Queue a deployment → `202` + `{ jobId, streamUrl }`.               |
| `GET`    | `/api/deployments/{name}/logs?lines=100`             | `docker compose logs --tail=N` (falls back to `docker logs`).      |
| `POST`   | `/api/deployments/{name}/restart`                    | `docker compose restart`.                                          |
| `DELETE` | `/api/deployments/{name}`                            | `docker compose down -v` + delete the local checkout.              |
| `GET`    | `/api/deployments/{name}/stream-build-logs`          | SSE for the project's active (or most recent) build.               |
| `GET`    | `/api/jobs`, `/api/jobs/{id}`                        | Recent jobs / one job with its full log buffer.                    |
| `GET`    | `/api/jobs/{id}/stream-build-logs`                   | SSE for one specific job.                                          |
| `POST`   | `/api/jobs/{id}/cancel`                              | Kill the running `git`/`docker` process tree.                      |

### `POST /api/deploy`

```json
{
  "projectName": "my-backend-service",
  "repoUrl": "https://github.com/username/repo.git",
  "branch": "main",
  "hostPort": 8080,
  "envContent": "PORT=80\nDB_HOST=192.168.1.50\nNODE_ENV=production",
  "files": [{ "path": "config/appsettings.Production.json", "content": "{}" }]
}
```

`files` is optional — extra configuration files injected relative to the repo root
(path traversal outside the project directory is rejected).

### SSE frames

```
event: status
data: {"id":"39c77810d085","projectName":"ldc-smoke","status":"Running","step":"docker compose up -d --build --remove-orphans", ...}

event: log
data: {"seq":42,"timestamp":"2026-07-25T21:56:41+03:00","stream":"stdout","text":"#8 exporting to image"}

event: end
data: {"id":"39c77810d085","status":"Succeeded", ...}
```

`stream` is `stdout`, `stderr`, `system` (controller narration) or `error`. Subscribing
mid-build replays the entire buffer first, so a late viewer still sees the whole log; a
`: heartbeat` comment every 15 s keeps idle connections alive during long build steps.

## Project layout

```
src/DeployController/
  Program.cs                     endpoint mapping, DI, SSE writer
  Models/Contracts.cs            DTOs + project/branch/URL validation rules
  Services/
    ProcessRunner.cs             deadlock-free async process execution
    DeploymentService.cs         git sync, config injection, compose orchestration
    DeploymentQueue.cs           Channel<T> queue + BackgroundService worker
    JobStore.cs                  job registry, log buffer, live subscribers
    DockerOutput.cs              `docker ps --format json` + port-string parsing
    FileSystemHelpers.cs         LF/no-BOM writes, path guards, force delete, .env parsing
    DeploymentOptions.cs         appsettings binding
  wwwroot/index.html             the whole dashboard (Tailwind CDN + vanilla JS)
packaging/                       install kit copied into the published package
  setup.ps1                      prerequisites, config, firewall, autostart
  uninstall.ps1                  removes task + firewall rule + running process
  start.bat                      run it in a console
  KURULUM.md                     Turkish install guide for the target machine
build-package.ps1                publishes dist\LocalDeploymentController (+ .zip)
```

## Implementation notes

- **No deadlocks.** `ProcessRunner` consumes stdout and stderr through the async
  `OutputDataReceived` / `ErrorDataReceived` events instead of a blocking `ReadToEnd` on one
  stream, waits for both readers to close after exit, and kills the whole process tree on
  timeout or cancel.
- **No hanging on credentials.** Git runs with `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`
  and a closed stdin, so a private repository fails fast instead of blocking forever.
- **Arguments are never string-concatenated** — everything goes through
  `ProcessStartInfo.ArgumentList`, and project names, branches and repo URLs are validated
  (a URL or branch starting with `-` is rejected so it cannot become a git option).
- **Windows-specific cleanup.** Deletion clears the read-only attributes git puts on pack
  files and retries a few times while Docker or antivirus still hold handles;
  `core.longpaths=true` is set for clones.
- **`git clean -fd`** (not `-fdx`) — untracked leftovers go, ignored artefacts such as
  `node_modules` and bind-mounted volume data stay.

## Security

There is no authentication, and by design the controller clones arbitrary repositories and
executes their `Dockerfile`. Treat it exactly like an SSH session on the host: bind it to a
trusted LAN, and put it behind a reverse proxy with auth if it needs to leave this machine.
