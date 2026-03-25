# Container Session Support

## Problem

CCC manages Claude Code sessions via tmux on the host. The Wint dev container (`claude-dev`) provides a sandboxed environment with firewall isolation, .NET SDKs, and pre-installed plugins. Currently there's no way to create CCC-managed sessions that run inside the container.

## Solution

Option A: tmux stays on the host, session command wraps with `docker exec`. The container runs persistently via docker-compose, and CCC creates normal tmux sessions whose command is `docker exec -it ... claude` instead of a direct shell invocation.

## Design

### Config

`CccConfig` gets two new properties:

- `ContainerName` (`string`, default `""`): Docker container name (e.g. `"claude-dev"`)
- `ContainerSessions` (`HashSet<string>`): Tracks which sessions were created as container sessions

### ContainerService (new static service)

- `IsRunning(string containerName) -> bool`: Runs `docker inspect --format={{.State.Running}} <name>`, caches result with ~30s TTL

### SshService.BuildSessionCommand — container path

Existing paths:

- Local: `$SHELL -lic claude`
- Remote: `ssh -t host 'cd path && exec $SHELL -lc claude'`

New path:

- Container: `docker exec -it -e CCC_SESSION_NAME=<name> -w <path> <container> zsh -lic claude`

### Session Creation Flow

After directory pick in `SessionHandler.Create`:

1. Check `config.ContainerName` is set AND `ContainerService.IsRunning(name)` returns true
2. If yes: prompt with two choices — `Container (claude-dev)` / `Local`
3. If no: skip step entirely, current behavior preserved
4. When container chosen, pass `containerName` through to `BuildSessionCommand`

### TmuxBackend.CreateSession

When container is used:

- Env vars (`CCC_SESSION_NAME`, `CLAUDE_CONFIG_DIR`) go into the `docker exec -e` flags instead of tmux `-e` flags
- Tmux working directory set to `$HOME` (like remote sessions) since the real working dir is inside the container

### Visual Indicator

- Container sessions show a whale icon in the session list
- `ContainerSessions` HashSet in config tracks which sessions are container sessions
- Renderer checks membership and prepends indicator to session name

### Settings UI

`ContainerName` added as an editable text field in settings.

## Out of Scope

- Auto-starting stopped containers
- Per-group container settings (global config only)
- Container session creation for groups (single session flow only for now)

## Validation

Manually tested that `docker exec -it -w <path> claude-dev zsh -lic claude` inside a tmux session works: CCC can capture pane content, send keys, and detect waiting-for-input state.
