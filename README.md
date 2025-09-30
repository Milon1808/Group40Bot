# Group40Bot

Discord bot built with .NET 8 and Discord.Net.  
Purpose: Manage **temporary voice channels** per guild and provide admin-only slash commands.

## Features
- **Temp Voice**: When a member joins a configured **lobby** voice channel, the bot creates a personal voice channel in the same category (inherits category permissions), grants owner+bot extra rights, moves the user there, and deletes the channel when it becomes empty.
- **Live config (per guild)** via slash commands:
  - `/tempvoice add-lobby <voice-channel>` — mark a voice channel as a lobby.
  - `/tempvoice remove-lobby <voice-channel>` — unmark a lobby.
  - `/tempvoice list` — list all lobbies.
- **/help** — posts all available commands into the channel.
- **Multi-guild**: Commands and settings are scoped per guild.
- **Admin-only**: Commands require Administrator permission and are checked at runtime.

## Requirements
- Bot invited with scopes `bot` and `applications.commands`.
- Permissions: Manage Channels, Move Members, View Channel, Connect.
- Developer Portal → Bot → **SERVER MEMBERS INTENT** enabled.
- Runtime env var: `DISCORD_TOKEN`.

## Local development
```bash
# install .NET 8 SDK first
dotnet restore
dotnet run --project Group40Bot/Group40Bot.csproj
