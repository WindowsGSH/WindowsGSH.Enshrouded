# Enshrouded Dedicated Server

WindowsGSH module for Enshrouded dedicated servers.

## Support

If this module helps you host your servers, you can support development here:

- [Ko-fi](https://ko-fi.com/shenniko)
- [PayPal](https://paypal.me/shenniko)

## Module Layout

```text
WindowsGSH.Enshrouded/
  README.md
  LICENSE.md
  windowsgsm-plugin-source.Enshrouded.url.txt
  Enshrouded.mod/
    module.json
    EnshroudedModule.cs
    enshrouded_server_external.json
    author.png
```

Import `Enshrouded.mod` directly, or import the repository root and let WindowsGSH discover the nested module folder.

## Current Status

- Installs through SteamCMD app `2278520` with anonymous login.
- Starts `enshrouded_server.exe`.
- Writes `enshrouded_server.json` before start.
- Launches with `-log`, bind IP, query port, slot count, Steam server name, and optional extra arguments.
- Writes `enshrouded_server.json` as UTF-8 without a byte order mark because the current Enshrouded parser rejects otherwise valid JSON when the file starts with a UTF-8 BOM.
- Backs up `savegame`, `enshrouded_server.json`, and `logs`.
- Queries local A2S status on loopback using `network.queryPort`.
- Writes the current ban-list key, `bannedAccounts`, and migrates older `bans` entries when found.
- Creates default `Admin` and `Friend` role groups for fresh configs.
- Preserves existing role permissions, role passwords, and extra custom role groups already present in the config.
- RCON is not implemented by this module.
- Declares WindowsGSH module API `1.0`.
- Supports existing-server import for native Enshrouded folders and WindowsGSM-style folders.

## Quick Start

1. Import the module in WindowsGSH Module Management.
2. Create a new Enshrouded server.
3. Set the server name, bind IP, query port, player slots, chat options, role passwords, and gameplay options.
4. Install the server through WindowsGSH.
5. Start the server.

## Important Settings

- `server.name`: Steam server name shown to players.
- `server.maxPlayers`: player slot limit. Enshrouded supports `1` to `16`.
- `network.ip`: bind IP written to `ip`.
- `network.queryPort`: Steam server browser and connection query port. Default is `15637`.
- `chat.voiceChatMode`: voice chat mode, such as `Proximity` or `Global`.
- `chat.enableVoiceChat`: writes `enableVoiceChat` as `true` or `false`.
- `chat.enableTextChat`: writes `enableTextChat` as `true` or `false`.
- `roles.admin.password`: Admin role password.
- `roles.friend.password`: Friend role password.
- `server.additionalArguments`: optional extra launch arguments. WindowsGSH removes duplicates for module-managed flags.

## Role Configuration

Enshrouded uses `userGroups` in `enshrouded_server.json`. On a fresh config, WindowsGSH creates:

- `Admin`
- `Friend`

WindowsGSH exposes passwords and reserved slots for those default roles. If an existing config already includes `Guest`, `Visitor`, or custom role groups, the module preserves them and keeps their existing permissions instead of replacing the full `userGroups` array.

## WindowsGSM Import

The module is based on the WindowsGSM Enshrouded plugin by ohmcodes and can adopt WindowsGSM-style server folders that contain:

```text
serverfiles/enshrouded_server.exe
```

During import, WindowsGSH can read existing values from:

```text
enshrouded_server.json
```

This lets copied or adopted installs keep their server name, save path, log path, bind IP, query port, slot count, chat settings, gameplay settings, role passwords, and reserved slots.

## Existing Server Import

In WindowsGSH, use **Import Existing** and choose this module. Select either an Enshrouded install folder or a WindowsGSM server folder that contains `serverfiles`. WindowsGSH will detect `enshrouded_server.exe`, preview values from `enshrouded_server.json` when present, and then let you copy or adopt the existing install.

## References

- Dedicated server configuration: https://enshrouded.zendesk.com/hc/en-us/articles/16055441447709-Dedicated-Server-Configuration
- Gameplay settings: https://enshrouded.zendesk.com/hc/en-us/articles/20453241249821-Server-Gameplay-Settings
- Server roles: https://enshrouded.zendesk.com/hc/en-us/articles/19191581489309-Server-Roles-Configuration
- Dedicated server FAQ: https://enshrouded.zendesk.com/hc/en-us/articles/16056312924957-Dedicated-Server-FAQ
- Original WindowsGSM plugin: https://github.com/ohmcodes/WindowsGSM.Enshrouded

## Trust Note

C# modules run code on the user's machine. WindowsGSH does not create, own, review, sign, or guarantee third-party modules.
