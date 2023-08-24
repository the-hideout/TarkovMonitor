# TarkovMonitor

[![build-dev](https://github.com/the-hideout/TarkovMonitor/actions/workflows/build-dev.yml/badge.svg)](https://github.com/the-hideout/TarkovMonitor/actions/workflows/build-dev.yml)

![image](https://github.com/the-hideout/TarkovMonitor/assets/1557581/99602d29-98c8-4738-8757-0fa763d54e9a)

TarkovMonitor is an Escape from Tarkov companion application that provides useful audio hints (only in the menu) and can automatically update your task progress on Tarkov Tracker.

When you're queueing for a raid, TarkovMonitor can trigger an audio notification when a match has been found and also when the raid is starting.

If you provide a Tarkov Tracker API token, TarkovMonitor can automatically update your task progress on Tarkov Tracker as you play. If you've failed restartable tasks but haven't restarted them, TarkovMonitor can also provide an audio reminder once you start queuing for another raid.

## Installation

Head on over to the [latest release](https://github.com/the-hideout/TarkovMonitor/releases/latest) page for this project. Once you are there, you'll see an `Assets` section and you'll want to download the `TarkovMonitor.zip` link:

<img width="845" alt="Screenshot 2023-08-10 at 7 58 36 PM" src="https://github.com/the-hideout/TarkovMonitor/assets/23362539/86fbb000-25a3-4d71-bf39-45d622d61e8e">

Once downloaded, extract the zip and run the `TarkovMonitor.exe` executable included within the bundle. Enjoy!

## FAQ

### How does TarkovMonitor work?

TarkovMonitor simply watches the log files that the game creates as it's running for certain events.

### I've installed and run TarkovMonitor, why hasn't marked all my completed quests as complete?

TarkovMonitor only monitors new logs as they are being written while the app is running. Therefore, it doesn't automatically update quest progress that was made prior to the app running. It will, however, still mark quests as complete going forward while the app is running.

### Is TarkovMonitor a cheat?

We don't have any official word from BSG, but it would be silly for TarkovMonitor to be considerd a cheat. It doesn't do anything while players are in-raid because the logs aren't updated while a raid is in-progress. Moreover, the application is simply reading the logs that are written to your computer.

### Can TarkovMonitor update my hideout build progress too?

Unfortunately, there are no log events for when you build hideout stations, so TarkovMonitor cannot automatically mark them as built.

### What is the "Submit Queue Time Data" option for?

When enabled, TarkovMonitor will submit the amount of time it takes to queue for a raid to Tarkov.dev. The information is sent anonymously and only the following pieces of information are sent and saved: the map, the time it took to find a raid, and whether the raid was for PMC or scav.
