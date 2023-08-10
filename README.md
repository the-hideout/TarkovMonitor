# TarkovMonitor

[![build-dev](https://github.com/the-hideout/TarkovMonitor/actions/workflows/build-dev.yml/badge.svg)](https://github.com/the-hideout/TarkovMonitor/actions/workflows/build-dev.yml)

![image](https://github.com/the-hideout/TarkovMonitor/assets/1557581/99602d29-98c8-4738-8757-0fa763d54e9a)

TarkovMonitor is an Escape from Tarkov companion application that provides useful audio hints (only in the menu) and can automatically update your task progress on Tarkov Tracker.

When you're queueing for a raid, TarkovMonitor can trigger an audio notification when a match has been found and also when the raid is starting.

If you provide a Tarkov Tracker API token, TarkovMonitor can automatically update your task progress on Tarkov Tracker as you play. If you've failed restartable tasks but haven't restarted them, TarkovMonitor can also provide an audio reminder once you start queuing for another raid.

The latest release can be downloaded on [GitHub](https://github.com/the-hideout/TarkovMonitor/releases/latest).

## FAQ

### How does TarkovMonitor work?

TarkovMonitor simply watches the log files that the game creates as it's running for certain events.

### Is TarkovMonitor a cheat?

We don't have any official word from BSG, but it would be silly for TarkovMonitor to be considerd a cheat. It doesn't do anything while players are in-raid because the logs aren't updated while a raid is in-progress. Moreover, the application is simply reading the logs that are written to your computer.

### Can TarkovMonitor update my hideout build progress too?

Unfortunately, there are no log events for when you build hideout stations, so TarkovMonitor cannot automatically mark them as built.

### What is the "Submit Queue Time Data" option for?

When enabled, TarkovMonitor will submit the amount of time it takes to queue for a raid to Tarkov.dev. The information is sent anonymously and only the following pieces of information are sent and saved: the map, the time it took to find a raid, and whether the raid was for PMC or scav.
