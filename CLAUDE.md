# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build                    # Build debug
dotnet run                      # Build + run (must be outside tmux)
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist  # Release binary
```

No test project exists. Verify changes by building and manual testing.

## Architecture

**Code Command Center (ccc)** is a terminal dashboard for managing multiple Claude Code sessions. Single .NET 10
executable, single dependency (Spectre.Console). Uses tmux on Linux/macOS.

### Core Loop

`Program.cs` creates `App`, which runs a polling-based TUI main loop (30ms tick):

1. Process keyboard input via `HandleKey()` → `DispatchAction()`
2. Refresh pane captures every 500ms
3. Detect waiting-for-input sessions via content hash stability
4. Re-render with `Renderer.BuildLayout()`

### App.cs (~800 lines)

Orchestration shell. Contains the main loop, pane capture, key routing, cursor movement, and lifecycle management.
Delegates domain logic to handlers.

- **Key dispatch** (`DispatchAction`): Maps action IDs to handler methods. Actions defined in
  `KeyBindingService.Defaults`.
- **Main loop** (`MainLoop`): 30ms tick, polls pane content, checks update status, re-renders on state changes.
- **Pane capture** (`UpdateCapturedPane`): Detects waiting-for-input transitions, notifies, captures pane content for
  preview.

### Handlers/

| Handler           | Purpose                                                                                                                              |
|-------------------|--------------------------------------------------------------------------------------------------------------------------------------|
| `FlowHelper`      | Flow UI scaffolding (RunFlow, prompts, pickers), static utilities (sanitize names, worktree scanning, IDE launch, key ID resolution) |
| `DiffHandler`     | Diff overlay view — open, scroll, jump between files                                                                                 |
| `SettingsHandler` | Settings view — navigation, editing, rebinding, favorites, config file opening                                                       |
| `SessionHandler`  | Session CRUD — create, delete, edit, attach, exclude, send keys, open folder/IDE                                                     |
| `GroupHandler`    | Group CRUD — create (3 modes: existing worktree, new worktrees, manual), delete, edit, move session to group                         |

Handlers receive `AppState`, `CccConfig`, and `ISessionBackend` via primary constructors, plus `Action` callbacks for
cross-cutting concerns (reload sessions, render, reset caches).

### Session Backend

`ISessionBackend` abstracts all session lifecycle operations.

| Backend        | Platform      | Session persistence                        |
|----------------|---------------|--------------------------------------------|
| `TmuxBackend`  | Linux / macOS | Persistent (tmux server survives CCC exit) |

### Services (all static)

| Service             | Purpose                                                                                                |
|---------------------|--------------------------------------------------------------------------------------------------------|
| `ConfigService`     | JSON persistence to `~/.ccc/config.json`. Handles session metadata, groups, keybindings                |
| `KeyBindingService` | Resolves keybindings from defaults + config overrides. Builds key→action map                           |
| `GitService`        | `git worktree add`, `IsGitRepo`, `FetchPrune`, `DetectGitInfo`, branch name sanitization               |
| `UpdateChecker`     | Async GitHub API check for newer releases                                                              |
| `CrashLog`          | Exception logging to `~/.ccc/crash.log` with auto-trim                                                 |

### UI Layer

| File                 | Purpose                                                                       |
|----------------------|-------------------------------------------------------------------------------|
| `AppState`           | Mutable state container: sessions, cursor, view mode, input buffer, groups, tree items |
| `Renderer`           | Stateless rendering — builds Spectre.Console `IRenderable` from `AppState`    |
| `AnsiParser`         | Converts tmux ANSI escape sequences to Spectre styles (256-color + truecolor) |
| `SettingsDefinition` | Builds settings categories and items for the settings view                    |

### Models

- `CccConfig`: Root config (favorites, groups, colors, descriptions, keybindings, worktreeBasePath)
- `Session`: Runtime session with backend metadata + git info + waiting-for-input status
- `SessionGroup`: Named group of session names with optional worktree path
- `TreeItem`: Discriminated union for unified tree view (SessionItem, GroupHeader)
- `KeyBinding` / `KeyBindingConfig`: Resolved keybinding and JSON config override
- `SettingsItem` / `SettingsCategory`: Settings view data structures

### Enums

- `ViewMode`: List, Grid, Settings, DiffOverlay
- `SettingsItemType`: Text, Toggle, Number, Action

## Code Style

- **One type per file.** Each class, enum, record, struct, and exception gets its own file. No nested types.
- **Folder names describe contents.** `Models/`, `Enums/`, `Services/`, `UI/` — a file's folder should tell you what
  kind of thing it is.

## Key Patterns

**Session naming**: `SanitizeTmuxSessionName()` replaces dots/colons with underscores (tmux requirement). Group sessions
named `{groupName}-{repoName}`.

**Waiting-for-input detection**: Hash pane content (excluding status bar), track consecutive stable polls. After
threshold, mark session idle with `!` indicator.

**Grid view**: Ctrl+G creates a temporary `ccc-grid` tmux session using `join-pane` to move session panes
into a tiled layout. User interacts with real tmux panes. On exit (Ctrl+G or detach), panes are restored
to their original sessions. Max 6 sessions. Crash recovery via tmux environment variable manifest.

**Worktree creation**: `PickDirectory` shows `⑂` entries for git repo favorites. Creates worktrees at
`worktreeBasePath/{branchName}/{repoName}/`. Group flow generates `.feature-context.json` for later discovery.

**Color system**: 13 predefined Spectre.Console colors. Converted to RGB hex for tmux `status-style`. "Just give me one"
picks random unused color.

## Config

Stored at `~/.ccc/config.json`. Key fields: `favoriteFolders`, `ideCommand`, `groups`, `sessionColors`,
`sessionDescriptions`, `worktreeBasePath`, `keybindings`, `excludedSessions`.

## CI/CD

`.github/workflows/release.yml`: Push to main triggers semantic version bump (based on commit prefixes), cross-platform
build (linux-x64, osx-arm64, osx-x64), and GitHub release with binary archives.

## Documentation

Always update `README.md` when adding or changing features. The README is the primary user-facing documentation —
keybindings table, config options, and feature descriptions must stay in sync with the code.

## Version

Bumped in the `.csproj` `<Version>` property. CI auto-calculates from git tags + commit messages.
