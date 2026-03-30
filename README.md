# Code Command Center (ccc)

### List View

![List View](Images/1.png)

### Grid View

![Grid View](Images/2.png)

### Settings

![Settings](Images/3.png)

### Diff View

![Diff View](Images/6.png)

A terminal UI for managing multiple Claude Code sessions. Run dozens of Claude agents in parallel, see what they're all doing at a glance, and jump into any session instantly.

## Sidebar Mode

CCC runs inside its own tmux session with a persistent two-pane layout:

- **Left pane** — navigation: session list, groups, preview
- **Right pane** — the active session, always visible

Navigate with arrow keys and press `Enter` to switch the right pane to any session. Press `Ctrl+Space` (configurable via `focusKeybinding`) to toggle keyboard focus between the nav pane and the active session pane — no attach/detach cycle needed.

Sessions live in a pool managed by CCC. They survive CCC crashes and restarts. Standalone tmux sessions created by previous CCC versions are automatically imported into the pool on first run.

## Features

- **Live preview** — see each session's terminal output in real-time without attaching
- **Grid view** — monitor up to 6 sessions in a native tmux tiled layout with live panes
- **Waiting-for-input detection** — sessions that need your attention are marked with `!` and trigger notifications
- **Session groups** — organize related sessions together, create them in bulk from git worktrees, open a single session for the entire worktree
- **Git worktree integration** — create worktrees on the fly, one branch per session, shared feature folders with auto-discovery
- **Git diff view** — see what changed since a session started, with full colorized scrollable diff overlay
- **PR review** — pick a repo, pick a PR, get a review worktree with Claude pre-loaded with a review prompt
- **Notifications** — terminal bell, OSC, and desktop notifications when sessions go idle
- **Machine grouping** — sessions are grouped by host (Local, remote machines) with collapsible headers showing session counts
- **Remote sessions** — run Claude on remote machines via SSH, managed from your local dashboard
- **Cross-platform** — tmux on Linux/macOS (sessions persist)
- **Mobile mode** — single-column layout for SSH from your phone
- **Customizable keybindings** — rebind any action, disable what you don't need, all from the in-app settings page
- **IDE integration** — open any session's directory in your editor with one keypress
- **Auto-update** — checks GitHub for new releases and installs in-place
- **Single binary, single dependency** — just .NET 10 and tmux

## Requirements

| Platform        | Backend | Requirements                                                                     |
|-----------------|---------|----------------------------------------------------------------------------------|
| Linux / macOS   | tmux    | [.NET 10](https://dotnet.microsoft.com/download), [tmux](https://github.com/tmux/tmux) |

CCC uses tmux as the session backend — sessions persist independently of CCC.

## Build

```bash
dotnet build
```

## Install

### From GitHub Release

Download and install the latest release automatically (Linux / macOS):

```bash
curl -fsSL https://raw.githubusercontent.com/AdamGardelov/code-command-center/main/install.sh | bash
```

This detects your platform (Linux, macOS Intel/ARM), downloads the latest release, and installs to `/usr/local/bin`.

### From Source

Requires [.NET 10](https://dotnet.microsoft.com/download) SDK.

```bash
# Linux / WSL
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist

# macOS Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist

# macOS Intel
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -o dist

```

Then copy to your PATH:

```bash
sudo cp dist/ccc /usr/local/bin/ccc
```

After installing, the `ccc` command is available from any terminal.

## Usage

Run outside of tmux:

```bash
ccc
```

The app shows a split-panel TUI — sessions on the left, a live pane preview on the right. Sessions that have been idle
for a few seconds are marked with `!` (waiting for input).

### Mobile Mode

For SSH clients on phones (e.g. Termius), launch with the `-m` flag:

```bash
ccc -m
```

Mobile mode uses a single-column layout optimized for narrow terminals — no preview panel, no grid view. You get a
scrollable session list, a 3-line detail bar for the selected session, and a context-sensitive status bar.

<p>
  <img src="Images/4.jpg" alt="Phone mode" width="350">
  <img src="Images/5.jpg" alt="Phone mode group filter" width="350">
</p>

| Key                | Action                                           |
|--------------------|--------------------------------------------------|
| `j` / `k` / arrows | Navigate sessions                                |
| `Enter`            | Attach to selected session                       |
| `Y`                | Approve (shown when session is waiting)          |
| `N`                | Reject (shown when session is waiting)           |
| `S`                | Send text to session                             |
| `g`                | Cycle group filter (All > Group1 > Group2 > All) |
| `r`                | Refresh session list                             |
| `q`                | Quit                                             |

### Grid View

Press `Ctrl+G` to enter grid view. CCC creates a temporary `ccc-grid` tmux session and moves session panes into a
tiled layout using `join-pane`. You interact with real, live tmux panes — not screenshots.

Press `Ctrl+G` again or detach from the grid session to exit. Panes are automatically restored to their original
sessions. Supports up to 6 sessions. If CCC crashes while in grid mode, panes are recovered on next startup via
a manifest stored in a tmux environment variable.

### Keybindings

#### List View (default)

| Key                | Action                                                           |
|--------------------|------------------------------------------------------------------|
| `j` / `k` / arrows | Navigate sessions                                                |
| `Enter`            | Attach to session / open worktree group session / toggle expand on machine header |
| `Space`            | Toggle expand/collapse group                                     |
| `Ctrl+G`           | Toggle grid view (group grid when on a grouped session; sidebar stays visible) |
| `D`                | Toggle git diff mode (summary in preview, `Enter` for full diff) |
| `n`                | Create new session (launches `claude` in a given directory)      |
| `C`                | Create session in background (no immediate focus switch)         |
| `Ctrl+Space`       | Toggle focus between nav pane and active session pane            |
| `g`                | Create new group                                                 |
| `f`                | Open session directory in file manager                           |
| `i`                | Open session directory in IDE                                    |
| `s`                | Open settings page                                               |
| `d`                | Delete session (with confirmation)                               |
| `e`                | Edit session (name, description, color)                          |
| `x`                | Exclude/restore session from grid view                           |
| `m`                | Move standalone session to a group                               |
| `a`                | Adopt an untracked remote session into CCC                       |
| `p`                | Review a PR — pick repo, pick PR, create review worktree         |
| `r`                | Refresh session list                                             |
| `Y`                | Approve — sends `y` to the selected session                      |
| `N`                | Reject — sends `n` to the selected session                       |
| `S`                | Send — type a message and send it to the selected session        |
| `q`                | Quit                                                             |

#### Grid View

| Key               | Action                                                  |
|-------------------|---------------------------------------------------------|
| `Ctrl+G`          | Exit grid view and restore panes                        |
| All other keys    | Handled directly by tmux panes                          |

Arrow keys always work for navigation regardless of configuration. In sidebar mode, press `Ctrl+Space` to toggle focus back to the nav pane at any time. When using a remote session in a dedicated terminal, detach with `Ctrl-b d` (standard tmux detach) to return to CCC.

### Git Diff View

Press `D` to toggle diff mode. When active, the preview panel shows a `git diff --stat` summary of all changes since the
session started (auto-refreshes every 5 seconds). Press `Enter` while diff mode is on to open a fullscreen scrollable
overlay with the complete colorized diff.

CCC records each session's HEAD commit at creation time as a baseline. All diffs are computed against this baseline, so
you see exactly what changed during the session.

#### Diff Overlay Controls

| Key                   | Action          |
|-----------------------|-----------------|
| `j` / `k` / `↑` / `↓` | Scroll one line |
| `Space` / `PageDown`  | Page down       |
| `PageUp`              | Page up         |
| `g`                   | Jump to top     |
| `G`                   | Jump to bottom  |
| `Esc` / `q`           | Close overlay   |

### Settings Page

Press `s` to open the in-app settings page. Navigate categories on the left, settings on the right.

| Category      | What you can configure                  |
|---------------|-----------------------------------------|
| General       | IDE command, worktree base path         |
| Keybindings   | Enable/disable actions                  |
| Notifications | Bell, desktop, OSC notify, cooldown     |
| Favorites     | Add, edit, delete favorite folders      |
| Advanced      | Skip permissions toggle, open raw config file, reset keybindings |

**Controls:** `j/k` navigate, `Tab` switch panels, `Enter` edit/toggle, `Esc` back, `o` open config file.

### Skip Permissions

Sessions can launch Claude with `--dangerously-skip-permissions` in two ways:

- **Global toggle** — Settings > Advanced > Skip Permissions. When ON, all new sessions skip permissions automatically.
- **Per-session** — When creating a session or group, a prompt lets you opt in for that specific session. When the global toggle is ON, the per-session prompt is skipped automatically.

Both are decided at creation time and apply for the lifetime of the session — changing the global toggle won't affect already-running sessions. Sessions launched with skip-permissions show a `⚡` indicator in the session list, preview panel, and grid view.

### Worktree Support

CCC can create git worktrees directly — no external tooling needed.

**Single session:** When creating a new session (`n`), the directory picker shows worktree entries below your favorites.
Picking one creates a worktree using the session name as the branch name — zero extra steps.

```
Pick a directory:
  Core  ~/Dev/Wint/Core
  BankService  ~/Dev/Wint/BankService
  ⑂ Core  (new worktree)
  ⑂ BankService  (new worktree)
  Custom path...
  Cancel
```

**Group with worktrees:** When creating a group (`g`), select "New worktrees (pick repos)" to multi-select repos, enter
a feature name, and CCC creates worktrees for all of them in a shared folder with a `.feature-context.json` for
discoverability. Sessions are **not created eagerly** — repos appear as placeholders in the group, and sessions are
created on demand when you press `Enter` on a repo item.

```
~/Dev/Wint/worktrees/
└── my-feature/
    ├── Core/                      ← worktree
    ├── BankService/               ← worktree
    └── .feature-context.json      ← auto-generated
```

**Opening a worktree group:** Press `Enter` on a worktree group header to open a single Claude session at the worktree
root — giving one session access to all repos. Press `Space` to expand/collapse the group and see individual repos.
Press `Enter` on a repo item to create a dedicated session for that repo.

These worktrees are also discoverable via the "Existing worktree feature" option when creating groups later.

### PR Review

Press `p` to start a PR review flow. CCC walks you through picking a repo from your favorites, fetching open PRs via
`gh pr list`, and selecting one to review. It then creates a dedicated worktree under `reviews/{pr-branch}` with the PR
branch checked out and launches a Claude session with a review prompt pre-loaded.

The review prompt language defaults to English. Set `prReviewLanguage` to `"sv"` in your config to use Swedish instead.

### Configuration

Create `~/.ccc/config.json` to configure favorite folders. When creating a new session, you'll be able to pick from this
list instead of typing a full path.

```json
{
    "favoriteFolders": [
        {
            "name": "Core",
            "path": "~/Dev/Wint/Core"
        },
        {
            "name": "Salary",
            "path": "~/Dev/Wint/Wint.Salary"
        }
    ],
    "ideCommand": "rider"
}
```

| Setting               | Default                 | Description                                                       |
|-----------------------|-------------------------|-------------------------------------------------------------------|
| `favoriteFolders`     | `[]`                    | Quick-pick directories when creating sessions                     |
| `ideCommand`          | ``                      | Command to run when pressing `i` (e.g. `rider`, `code`, `cursor`) |
| `sessionDescriptions` | `{}`                    | Display names shown under sessions in the preview panel           |
| `sessionColors`       | `{}`                    | Spectre Console color names for session panel borders             |
| `worktreeBasePath`        | `~/Dev/Wint/worktrees/` | Root directory for created worktrees                              |
| `keybindings`             | `{}`                    | Keybinding overrides (see below)                                  |
| `claudeConfigRoutes`      | `[]`                    | Directory-based Claude config routing (see below)                 |
| `defaultClaudeConfigDir`  | ``                      | Fallback `CLAUDE_CONFIG_DIR` when no route matches                |
| `remoteHosts`             | `[]`                    | SSH remote machines for running sessions (see below)              |
| `prReviewLanguage`        | `"en"`                  | Language for PR review prompts (`"en"` or `"sv"`)                 |
| `focusKeybinding`         | `"C-Space"`             | Tmux keybinding for toggling focus between nav and session pane   |
| `mouseEnabled`            | `true`                  | Enable mouse click to switch focus between panes                  |

The config file is created automatically on first run. Tilde (`~`) paths are expanded automatically.

#### Claude Config Routing

If you use multiple Claude Code accounts (e.g. personal + work), configure directory-based routing so each session
automatically uses the correct config:

```json
{
    "claudeConfigRoutes": [
        {
            "pathPrefix": "~/code/personal",
            "configDir": "~/.claude"
        }
    ],
    "defaultClaudeConfigDir": "~/.claude-work"
}
```

### Machine Grouping

The session list groups sessions by host. Each group is preceded by a collapsible machine header:

```
▼ Local (5)
  my-project
  another-session
▶ MY-SERVER (2)
```

- The header shows an expand/collapse icon (`▼` / `▶`), the machine name, and the session count.
- Press `Enter` on a machine header to toggle it open or closed.
- Pressing `n` (new session) while a machine header is selected pre-selects that host in the "Where to run?" step.
- Individual sessions no longer show a separate cloud icon — the machine header provides the host context.

#### Remote Hosts

Sessions live in tmux on the remote machine. Close your laptop, reopen it, and everything is still running — CCC
reconnects and shows the live state.

**Prerequisites:**

- SSH key-based authentication is required. CCC uses SSH ControlMaster with `BatchMode=yes`, so password prompts will
  never appear and unauthenticated connections will fail silently.
- `tmux` must be installed on the remote machine.
- `claude` must be installed and on PATH on the remote machine (login shell is used, so `~/.profile` / `~/.bash_profile`
  are sourced).
- `git` on the remote (for branch detection and worktree creation).

**Config:**

Add `remoteHosts` to `~/.ccc/config.json`:

```json
{
    "remoteHosts": [
        {
            "name": "MY-SERVER",
            "host": "user@server.example.com",
            "worktreeBasePath": "~/worktrees",
            "favoriteFolders": [
                { "name": "MyProject", "path": "~/projects/myproject", "defaultBranch": "main" }
            ]
        }
    ]
}
```

Each remote host has its own `favoriteFolders` and `worktreeBasePath`. When creating a new session (`n`), a "Where to
run?" step appears first — choose Local or any configured remote host. The directory picker then shows that host's
favorites.

Remote sessions show the host name in the detail panel (`Remote: MY-SERVER`) and in group views (`@MY-SERVER`). Git
branch detection and worktree creation work over SSH automatically.

**Session visibility:**

CCC only shows remote sessions that it created or that you explicitly adopted. Tmux sessions on the remote that were
created outside CCC are not shown by default. Press `a` to see and adopt untracked remote sessions — this lets you
bring existing sessions into CCC with a description and color.

**Offline behavior:**

If a host is unreachable, its sessions appear greyed out with a `✗` indicator and CCC shows the last-known session list
from its local cache. No interaction is possible while offline. Sessions become live again automatically within ~30
seconds of the host reconnecting — no manual refresh needed.

**Attaching to a remote session:**

Press `Enter` on a remote session to embed it in the sidebar's right pane. For a dedicated full-terminal view, CCC opens a `ssh + tmux attach` terminal. To detach and return to CCC, press the tmux prefix followed by `d` (default: `Ctrl-b d`).

When creating a session, CCC matches the working directory against `claudeConfigRoutes` (first match wins) and sets
`CLAUDE_CONFIG_DIR` accordingly. If no route matches, `defaultClaudeConfigDir` is used. If that's also empty, the
environment variable is not set and Claude uses its default config.

#### Keybinding Configuration

Override default keybindings by adding a `keybindings` object to your config. Only include the actions you want to
change — missing entries keep their defaults.

```json
{
    "keybindings": {
        "approve": {
            "key": "y",
            "label": "yes"
        },
        "delete-session": {
            "enabled": false
        },
        "open-ide": {
            "key": "e",
            "label": "editor"
        }
    }
}
```

Each override supports three optional fields:

| Field     | Type     | Description                                                         |
|-----------|----------|---------------------------------------------------------------------|
| `key`     | `string` | Single char (`"n"`), special key (`"Enter"`), or modifier combo (`"Ctrl+G"`) |
| `enabled` | `bool`   | `false` to disable the action (ignored for non-disableable actions) |
| `label`   | `string` | Status bar text; empty string hides from the bar                    |

**Available actions:**

| Action ID        | Default Key | Default Label | Can Disable |
|------------------|-------------|---------------|-------------|
| `navigate-up`    | `k`         | (hidden)      | No          |
| `navigate-down`  | `j`         | (hidden)      | No          |
| `approve`        | `Y`         | approve       | Yes         |
| `reject`         | `N`         | reject        | Yes         |
| `send-text`      | `S`         | send          | Yes         |
| `attach`         | `Enter`     | attach        | Yes         |
| `new-session`    | `n`         | new           | Yes         |
| `new-session-bg` | `C`         | new (bg)      | Yes         |
| `toggle-focus`   | `Ctrl+Space` | focus        | Yes         |
| `new-group`      | `g`         | group         | Yes         |
| `open-folder`    | `f`         | folder        | Yes         |
| `open-ide`       | `i`         | ide           | Yes         |
| `open-settings`  | `s`         | settings      | No          |
| `delete-session` | `d`         | del           | Yes         |
| `edit-session`   | `e`         | edit          | Yes         |
| `toggle-exclude` | `x`         | hide          | Yes         |
| `move-to-group`  | `m`         | move          | Yes         |
| `adopt-remote`   | `a`         | adopt         | Yes         |
| `review-pr`     | `p`         | review        | Yes         |
| `toggle-expand`  | `Space`     | (hidden)      | No          |
| `toggle-grid`    | `Ctrl+G`    | grid          | Yes         |
| `toggle-diff`    | `D`         | diff          | Yes         |
| `refresh`        | `r`         | (hidden)      | Yes         |
| `quit`           | `q`         | quit          | No          |

Arrow keys always work for navigation regardless of configuration.

**Adding a new keybinding (developer guide):** Add a default entry in `KeyBindingService.Defaults` and a case in
`App.DispatchAction()`.

### Notification Hooks

CCC can detect when sessions are idle, working, or waiting for input using Claude Code hooks. This enables the `!`
indicator, terminal bell, and desktop notifications.

**Without hooks**, CCC falls back to content-hash polling — it watches for the terminal output to stop changing and
pattern-matches the idle prompt. This works but is slower and less reliable.

**With hooks**, Claude Code tells CCC exactly when state changes happen via a small shell script.

#### Setup

1. **Copy the hook script** to `~/.ccc/hooks/`:

```bash
mkdir -p ~/.ccc/hooks
cp hooks/ccc-state.sh ~/.ccc/hooks/ccc-state.sh
chmod +x ~/.ccc/hooks/ccc-state.sh
```

2. **Add hooks to your Claude Code settings** (`~/.claude/settings.json`):

```json
{
  "hooks": {
    "Notification": [
      {
        "matcher": "permission_prompt|elicitation_dialog",
        "hooks": [
          {
            "type": "command",
            "command": "bash ~/.ccc/hooks/ccc-state.sh"
          }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "bash ~/.ccc/hooks/ccc-state.sh"
          }
        ]
      }
    ],
    "UserPromptSubmit": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "bash ~/.ccc/hooks/ccc-state.sh"
          }
        ]
      }
    ]
  }
}
```

If you already have other hooks configured, merge these entries into your existing `hooks` object.

#### How It Works

CCC injects a `CCC_SESSION_NAME` environment variable into every session it creates. The hook script reads this variable
and writes the session state (`working`, `idle`, or `waiting`) to `~/.ccc/states/{session-name}`. CCC polls these files
and updates the UI accordingly.

| Event               | State Written | Meaning                              |
|---------------------|---------------|--------------------------------------|
| `UserPromptSubmit`  | `working`     | User sent a message, Claude is busy  |
| `Stop`              | `idle`        | Claude finished, waiting at prompt   |
| `Notification`      | `waiting`     | Claude needs permission or input     |

The script exits silently when run outside CCC (no `CCC_SESSION_NAME` set), so it won't interfere with standalone
Claude Code usage.
