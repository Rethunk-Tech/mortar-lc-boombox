# Boombox

Plays songs from boombox song packs on the boombox, and your own songs too. Packs made for Custom Boombox Fix work with
it.

- Who needs it: each player who wants to hear them. Players without it hear the game's songs.
- Config: shared from the host through [ConfigSync](https://thunderstore.io/c/lethal-company/p/Mortar/ConfigSync/), so
  everyone hears the same song.
  - `Include game songs`: keeps the boombox's own songs in the list. Off by default.
  - `Next song on each press`: plays the list in order. Off by default, which shuffles it, with no repeats until
    every song has played.
  - `New song when turned on`: starts a different song each time. Off by default, so it carries on where it stopped.

It plays the files where they are, so nothing gets copied or deleted, and loads each song the first time it comes up.
The host keeps everyone in step every few seconds. Look at the boombox to see what's playing, by its tags (artist and
title) or else its file name. Whoever holds it can press Q for the previous song or E for the next. Songs you add
while the game runs join the list straight away.

## Adding songs

Put MP3, OGG or WAV files in a `mortar/boombox` folder in your package. Your own can go in
`BepInEx/Custom Songs/Boombox Music`.

The `Custom Songs` folder in a package is still read.

With Custom Boombox Music installed, this one stays out of the way and that one plays the songs.
