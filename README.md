# TarkovMonitor

[![build-dev](https://github.com/the-hideout/TarkovMonitor/actions/workflows/build-dev.yml/badge.svg)](https://github.com/the-hideout/TarkovMonitor/actions/workflows/build-dev.yml)

[![Discord](https://img.shields.io/discord/956236955815907388?color=7388DA&label=Discord)](https://discord.gg/XPAsKGHSzH)

![image](https://github.com/the-hideout/TarkovMonitor/assets/1557581/99602d29-98c8-4738-8757-0fa763d54e9a)

TarkovMonitor is an Escape from Tarkov companion application that provides useful audio notifications, can automatically update your task progress on Tarkov Tracker, and includes other helpful features.

## Features

- Audio notifications
    - Match found
    - Raid starting
    - Restart failed tasks
    - Runthrough time elapsed
    - Turn air filter on/off
    - Scav cooldown
    - Customizable sounds for all of the above
- Goon tracking
    - Submit reports when you see the Goons to help other players find them
- Connect to the Tarkov.dev website via remote code
    - Automatically load the website map for the map you're playing on
    - Take an in-game screenshot and show your position on the website map
- Connect to Tarkov Tracker via API token
    - Automatically mark quests as complete as you complete them
- Statistics (all stored locally on your computer)
    - Track the your total sales on the flea market
    - Track how many times you play on each map

## Installation

Head on over to the [latest release](https://github.com/the-hideout/TarkovMonitor/releases/latest) page for this project. Once you are there, you'll see an `Assets` section and you'll want to download the `TarkovMonitor.zip` link:

<img width="845" alt="Screenshot 2023-08-10 at 7 58 36 PM" src="https://github.com/the-hideout/TarkovMonitor/assets/23362539/86fbb000-25a3-4d71-bf39-45d622d61e8e">

Once downloaded, extract the zip and run the `TarkovMonitor.exe` executable included within the bundle. Enjoy!

## FAQ

### How does TarkovMonitor work?

TarkovMonitor simply watches the log files that the game creates as it's running. Certain log messages correspond with particular events, so it's possible to automatically read some game events from these log files.

### I've installed and run TarkovMonitor, why hasn't it marked all my completed quests as complete?

TarkovMonitor only monitors new logs as they are being written while the app is running. Therefore, it doesn't automatically update quest progress that was made prior to the app running. It will, however, still mark quests as complete going forward while the app is running.

If you want to automatically update your progress from previous logs, open the Settings page, scroll down to the First Time Setup section, and click the Read Past Logs button. Tarkov Monitor will then present you with a list of breakpoints to choose the starting point to read logs from. The breakpoints are determined by the game's version number and your player profile id as written into each set of logs. Select the breakpoint corresponding with the start of the wipe for the corret account, click OK, and Tarkov Monitor will process all logs from that point forward for the selected profile and update your quest progress accordingly.

### Is TarkovMonitor a cheat?

We don't have any official word from BSG, but it would be silly for TarkovMonitor to be considerd a cheat. It doesn't do anything while players are in-raid because the logs aren't updated while a raid is in-progress. Moreover, the application is simply reading the logs that are written to your computer.

### Can TarkovMonitor update my hideout build progress on Tarkov Tracker?

Unfortunately, there are no log events for when you build hideout stations, so TarkovMonitor cannot automatically mark them as built.

### Does TarkovMonitor update my PMC level on Tarkov Tracker?

PMC level information is not logged by the game, so Tarkov Monitor cannot update it in Tarkov Tracker.

### What is the "Submit Queue Time Data" option for?

When enabled, TarkovMonitor will submit the amount of time it takes to queue for a raid to Tarkov.dev. The information is sent anonymously and only the following pieces of information are sent and saved: the map, the time it took to find a raid, and whether the raid was for PMC or scav.
