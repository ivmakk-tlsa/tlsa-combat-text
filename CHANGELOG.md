# Changelog

All notable changes to this mod are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this mod uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A health bar above each damaged zombie, hidden while its health is full and cleared on death.
- A floating number for each hit and each fire tick, showing the health actually lost, not the raw damage. A fully resisted hit draws nothing.
- Armor shown as blue segments under the health bar, one per armor part, from the first hit. A segment empties as its part breaks.
- Fire ticks merge inside a short window into one number, so damage over time stays readable.
- Configurable bars and numbers: turn either off, set the number lifetime and the fire-tick merge window, set the bar offset, and scale the whole overlay on top of the game's HUD scale.
- The overlay is hidden while the game is paused, a menu is open, the mission is not running, or a zombie is off screen.
