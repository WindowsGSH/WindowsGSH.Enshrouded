# WindowsGSH.Enshrouded

WindowsGSH module for Enshrouded dedicated servers.

This module installs the official Enshrouded Dedicated Server through SteamCMD, writes `enshrouded_server.json`, starts `enshrouded_server.exe`, queries the server through A2S on the configured query port, and supports importing existing Enshrouded server folders.

## What It Does

- Installs Steam app `2278520` anonymously with SteamCMD.
- Starts `enshrouded_server.exe`.
- Writes `enshrouded_server.json` before start.
- Uses the official default query port `15637`.
- Preserves existing role permissions, extra role groups, and role passwords when saving settings.
- Generates role passwords for fresh configs using cryptographic randomness.
- Exposes server, network, chat, gameplay, role, path, and launch settings.
- Adds readiness checks for the executable, config file, slot count, and global voice chat.
- Adds backup targets for `savegame`, `enshrouded_server.json`, and `logs`.
- Writes the current Enshrouded ban-list key, `bannedAccounts`, and migrates older `bans` entries when found.

## Not Supported

- RCON is not implemented. Enshrouded does not document a dedicated RCON interface.
- Advanced editing of arbitrary custom role permissions is not exposed yet. The module exposes role passwords plus reserved slots for the default Admin and Friend roles, while preserving existing permissions and extra custom groups already present in the config.
- Automatic mod/package installation is not implemented.

## Default Ports

| Purpose | Default |
| --- | ---: |
| Query / connection | `15637` |

Steam may also use its own required networking ports. See the official FAQ for the current guidance.

## Role Passwords

Enshrouded now uses `userGroups` in `enshrouded_server.json` instead of the older single `password` entry. On a fresh config, WindowsGSH generates passwords for:

- Admin
- Friend

When an existing config already has passwords, WindowsGSH preserves them unless the corresponding module setting is changed.

The server config is written as UTF-8 without a byte order mark. The current Enshrouded dedicated server parser rejects otherwise valid JSON when the file starts with a UTF-8 BOM.

## Existing Server Import

The import flow detects either:

- an Enshrouded install folder containing `enshrouded_server.exe`; or
- a WindowsGSM-style folder containing `serverfiles\enshrouded_server.exe`.

When `enshrouded_server.json` exists, WindowsGSH reads the main server, network, chat, gameplay, and role password settings into the module settings.

## References

- Dedicated server configuration: https://enshrouded.zendesk.com/hc/en-us/articles/16055441447709-Dedicated-Server-Configuration
- Gameplay settings: https://enshrouded.zendesk.com/hc/en-us/articles/20453241249821-Server-Gameplay-Settings
- Server roles: https://enshrouded.zendesk.com/hc/en-us/articles/19191581489309-Server-Roles-Configuration
- Dedicated server FAQ: https://enshrouded.zendesk.com/hc/en-us/articles/16056312924957-Dedicated-Server-FAQ
