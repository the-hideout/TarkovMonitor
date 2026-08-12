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
    - Quest Items Reminder (does not check for actual quest items, just a friendly reminder to allow you to back out of matching)
    - Customizable sounds for all of the above
- Goon tracking
    - Submit reports when you see the Goons to help other players find them
- Connect to the Tarkov.dev website via remote code
    - Automatically load the website map for the map you're playing on
    - Take an in-game screenshot and show your position on the website map
- Connect to Tarkov Tracker via API token
    - Automatically mark ordinary quests as accepted/active, failed, restarted, or complete as EFT writes those lifecycle events
- Statistics (all stored locally on your computer)
    - Track your total sales on the flea market
    - Track how many times you play on each map
- Visual Timers (have that friend that never heard the audio and asks "has the runthrough timer happened yet?")
   - Displays "Time in Raid"
   - Displays countdown for "Runthrough time"
   - Display countdown for Scav cooldown time

## Installation

Head on over to the [latest release](https://github.com/the-hideout/TarkovMonitor/releases/latest) page for this project. Once you are there, you'll see an `Assets` section and you'll want to download the `TarkovMonitor.zip` link:

<img width="845" alt="Screenshot 2023-08-10 at 7 58 36 PM" src="https://github.com/the-hideout/TarkovMonitor/assets/23362539/86fbb000-25a3-4d71-bf39-45d622d61e8e">

Once downloaded, extract the zip and run the `TarkovMonitor.exe` executable included within the bundle. Enjoy!

## Setup

On its own, TarkovMonitor will play audio notifications (e.g., when you match into a raid and when the raid begins). But its most useful features are unlocked when used in conjunction with other tools.

### Quest Tracking with TarkovTracker

[Tarkov Tracker](https://tarkovtracker.org) is a free website that allows you to track your quest progress. Once you log in to create a Tarkov Tracker account, you can share your quest progress with other tools (including TarkovMonitor) by creating an API token. Navigate to the [Tarkov Tracker settings page](https://tarkovtracker.org/settings), click the `create a token` button, and create a token that has permissions to `get progression` and `write progression`. You can give the token any name you want, but if you're creating it for Tarkov Monitor, it makes sense to name it `Tarkov Monitor`. Then click the `create token` button and click the token's copy button. Do not try to manually highlight the displayed token and copy it; some of the displayed token's characters are obfuscated with asterisks (*). Once you've copied the token, paste it in the Tarkov Tracker API token box in Tarkov Monitor settings and click the `Test Token` button. If you see a pop up indicating success, Tarkov Tracker is ready to start automatically updating your progress on Tarkov Tracker.

Tarkov Monitor syncs ordinary quest acceptance (`active`), failure, restart, and completion while it is running. Live writes wait until EFT has explicitly logged a recognized PvE, regular PvP, or seasonal PvP mode; missing or unsupported mode data pauses sync rather than defaulting to another mode, and sync resumes once identity and mode are established. Failed tasks retain Tarkov Tracker's completed-plus-failed encoding. Accepted-task sync requires a Tarkov Tracker server version that supports the explicit `active` task state; Tarkov Monitor will warn and leave that state unwritten only when Tracker's response identifies `active` as an unsupported task-state value, rather than incorrectly falling back to `uncompleted`. If you already made progress prior to running Tarkov Monitor, see [the below section on how to read past progress](#ive-installed-and-run-tarkovmonitor-why-hasnt-it-marked-all-my-completed-quests-as-complete).

EFT does not continuously write all useful events while you are in a raid. Task events become available to Tarkov Monitor only after EFT writes them to its logs, commonly when you are out of raid.

### Tarkov.dev Website Integration

The [Tarkov.dev website](https://tarkov.dev) has a "remote control" feature that allows the user to navigate to different pages in a browser window by using a different device. The original use case for this was to have the Tarkov.dev website open on a second monitor as you're playing the game and then using your cellphone as the remote control to load different pages on the website shown on the monitor without having to alt+tab out of the game.

TarkovMonitor can act as the "control" device, which allows it to do things like opening the corresponding map page on the website when you're loading into a raid and show your position (and rotation) on the map when you take a screenshot. To enable this integration, open the Tarkov.dev website, click the `Click to connect` button in the lower left, copy the `ID for remote control` shown in that box, and paste it in the Tarkov Monitor remote id settings. If you keep your browser window open, Tarkov Monitor should be set to control the Tarkov.dev site. Note that if you reload the Tarkov.dev site (including by restarting your browser), you'll need to click the `Click to connect` button again, but the remote code should remain the same.

## FAQ

### How does TarkovMonitor work?

TarkovMonitor simply watches the log files that the game creates as it's running. Certain log messages correspond with particular events, so it's possible to automatically read some game events from these log files.

### I've installed and run TarkovMonitor, why hasn't it marked all my completed quests as complete?

TarkovMonitor only monitors new logs as they are being written while the app is running. Therefore, it doesn't automatically update quest progress that was made prior to the app running. Going forward, it syncs ordinary task acceptance, failure/restart, and completion after EFT writes those events to its logs (typically out of raid).

If you want to automatically update your progress from previous logs, open the Settings page, scroll down to the Initial Setup section, and click the Read Past Logs button. Tarkov Monitor will then present you with a list of breakpoints to choose the starting point to read logs from. The breakpoints are determined by the game's version number, player profile id, and supported game mode as written into the logs. Select the breakpoint corresponding with the start of the wipe for the correct account and mode, then click OK.

Read Past Logs orders task lifecycle events globally by their logged timestamp, even when logs span folders or rotated/overlapping files. It establishes profile and mode at each event, ignores duplicates, and sends only the final state for each task in the selected profile and mode as one quota-conscious batch. Events whose profile or mode cannot be established are skipped rather than attributed across PvE, regular PvP, or seasonal PvP. Accepted states have the same Tarkov Tracker `active` server-version dependency described above.

### Is TarkovMonitor a cheat?

We don't have any official word from BSG, but it would be silly for TarkovMonitor to be considered a cheat. It doesn't do anything while players are in-raid because the logs aren't updated while a raid is in-progress. Moreover, the application is simply reading the logs that are written to your computer.

### Can TarkovMonitor update my hideout build progress on Tarkov Tracker?

Unfortunately, there are no log events for when you build hideout stations, so TarkovMonitor cannot automatically mark them as built.

### Does TarkovMonitor update my PMC level on Tarkov Tracker?

PMC level information is not logged by the game, so Tarkov Monitor cannot update it in Tarkov Tracker.

### What is the "Tarkov.dev Website Remote" option for?

The Tarkov.dev website has a feature that allows the user to "control" the website using another device. The typical use case is for someone to have the Tarkov.dev website loaded in a browser on their second monitor and then use their phone as the second device to load pages on the website without having to alt+tab out of the game. TarkovMonitor can act as the remote device and do things like load the Tarkov.dev map page for the map you're loading into a raid on. Linking the remote also enables showing your position on the Tarkov.dev map when you take a screenshot. To get the remote code for Tarkov.dev, just open the Tarkov.dev website in your browser, click the "Click to connect" box in the lower left, and then copy and paste that code into the Remote ID setting box in Tarkov Monitor.

### What is the "Submit Queue Time Data" option for?

When enabled, TarkovMonitor will submit the amount of time it takes to queue for a raid to Tarkov.dev. The information is sent anonymously and only the following pieces of information are sent and saved: the map, the time it took to find a raid, and whether the raid was for PMC or scav.

## Trouble Shooting

If the app won't launch and you see an "invalid WebView2 installation" error (sometimes accompanied by "The system cannot find the file specified"), you're likely hitting a known WebView2 issue.

Follow the solution here: [Error on launching program (invalid WebView2 installation) #22 — solution comment](https://github.com/the-hideout/TarkovMonitor/issues/22#issuecomment-3443766675)
