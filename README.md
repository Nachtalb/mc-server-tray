# McServerTray

A small Windows tray app that watches the Minecraft servers you have saved in your
[Prism Launcher](https://prismlauncher.org/) instances and tells you when someone comes online.

- Reads `servers.dat` from every Prism instance (merged, de-duplicated by address) and picks up
  changes automatically.
- Pings each server every 30 seconds with the regular server list ping.
- Pops up a notification when a player joins (by name when the server shares its player list,
  otherwise by count).
- Left-click the tray icon for a server list drawn like the in-game *Multiplayer* screen: server
  icon, your name for the server, the coloured MOTD, player count and ping bars. Hover the count
  for the player list, or the bars for the latency and version.
- The tray icon is a grass block, grey while everyone's offline and green when someone is on.
- Right-click for *Refresh now*, *Start with Windows* and *Exit*. Starting the exe again while
  it's running opens the server list.

## Minecraft look

Nothing from Mojang is shipped here. At startup the app opens the newest
`minecraft-*-client.jar` in Prism's library folder and loads the vanilla bitmap font and GUI
sprites from it. If no jar is found it falls back to plain Windows drawing.

## Build

Requires the .NET 9 SDK.

```powershell
dotnet publish -c Release -o publish
.\publish\McServerTray.exe
```

Prism's data folder is looked up in `%USERPROFILE%\scoop\persist\prismlauncher` (Scoop install)
and then `%APPDATA%\PrismLauncher`.

## License

[MIT](LICENSE)
