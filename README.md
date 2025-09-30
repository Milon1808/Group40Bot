# Group40Bot

Discord bot built with .NET 8 and Discord.Net.  
Purpose: Manage **temporary voice channels** per guild and provide admin-only slash commands.

## Features
- **Temp Voice**: Joining a configured **lobby** voice channel spawns a personal voice channel in the same category (inherits category permissions), grants owner+bot extra rights, moves the user there, and deletes the channel when empty.
- **Live config (per guild)** via slash commands:
  - `/tempvoice add-lobby <voice-channel>` — mark a voice channel as a lobby.
  - `/tempvoice remove-lobby <voice-channel>` — unmark a lobby.
  - `/tempvoice list` — list all lobbies.
- **/help** — posts all available commands into the channel.
- **Multi-guild** — settings are scoped per guild.
- **Admin-only** — commands require Administrator permission and are checked at runtime.

## Requirements
- Invite with scopes: `bot`, `applications.commands`.
- Bot permissions: **Manage Channels**, **Move Members**, **View Channel**, **Connect**.
- Developer Portal → Bot → **SERVER MEMBERS INTENT** enabled.
- Runtime env var: `DISCORD_TOKEN`.
- Optional: `DATA_DIR` for persistent settings (defaults to `./data`).

## Command overview
Run `/help` on any guild to see the current list of slash commands.

## Local development
```bash
# .NET 8 SDK required
dotnet restore
dotnet run --project Group40Bot/Group40Bot.csproj
```

## Local secrets (recommended)

Store the token outside the repo using .NET User Secrets:
```
cd Group40Bot/Group40Bot
dotnet user-secrets init                # creates UserSecretsId in csproj
dotnet user-secrets set "DISCORD_TOKEN" "YOUR_TOKEN"
# optional: data dir
dotnet user-secrets set "DATA_DIR" "./data"
```

## Linux server deployment (systemd)
```
Option A — build on the server

Ubuntu 22.04+:

# Add Microsoft packages and install .NET 8 SDK + git
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 git

# Create service user and data dir
sudo useradd -r -m -d /home/bot bot
sudo mkdir -p /var/lib/group40bot
sudo chown -R bot:bot /var/lib/group40bot

# Fetch and publish
sudo -u bot bash -lc 'git clone https://github.com/<you>/Group40Bot.git ~/Group40Bot && cd ~/Group40Bot/Group40Bot && dotnet publish -c Release -o out'

Option B — build locally, copy to server

dotnet publish Group40Bot/Group40Bot.csproj -c Release -o out
#copy ./out to the server (e.g., /home/bot/Group40Bot/out)

#Env file for the service
echo 'DISCORD_TOKEN=YOUR_TOKEN' | sudo tee /etc/default/group40bot >/dev/null
echo 'DATA_DIR=/var/lib/group40bot' | sudo tee -a /etc/default/group40bot >/dev/null

# systemd unit
sudo tee /etc/systemd/system/group40bot.service >/dev/null <<'UNIT'

[Unit]
Description=Group40 Discord Bot (.NET 8)
After=network-online.target

[Service]
WorkingDirectory=/home/bot/Group40Bot/Group40Bot/out
EnvironmentFile=/etc/default/group40bot
ExecStart=/usr/bin/dotnet Group40Bot.dll
User=bot
Restart=always

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now group40bot
journalctl -u group40bot -f
```

## Updates

```
sudo -u bot bash -lc '
cd ~/Group40Bot &&
git pull &&
cd Group40Bot &&
dotnet publish -c Release -o out
'
sudo systemctl restart group40bot
```

## Configuration & data

Persistent settings are stored as JSON at ${DATA_DIR}/settings.json.
Set DATA_DIR=/var/lib/group40bot on servers; keep default (./data) for local dev.

## Security

Never commit tokens or secrets. Use User Secrets locally and environment variables in production.
Enable secret scanning / branch protection on the repository.
The bot checks Administrator at runtime for all admin commands.

## Troubleshooting

PrivilegedIntentsRequired: enable SERVER MEMBERS INTENT in the Developer Portal.
Commands not visible: ensure the bot was invited with applications.commands scope; wait a minute after first deploy per guild; check logs.
Missing Permissions when creating channels: verify the bot role has Manage Channels, Move Members, View Channel, Connect and can see the target category.

## LICENSE

Copyright (c) Group 40. All rights reserved.

Permission is hereby NOT granted to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of this software without explicit written permission
from Group 40.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT.
