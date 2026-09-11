# Changelog

All notable changes to this mod are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this mod uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-09-11

### Added

- A broken-shield icon that flashes above the bar when a zombie's last armor plate breaks, so you can tell it is open to health damage.
- Fire, bleed, and stun icons in a row after the health bar while a zombie is affected, each filling down from the top as the effect's time runs out.
- Settings for the status icon size, the armor-break icon size, and how long an effect must last before its icon shows.

### Changed

- The health bar sits a little higher above the zombie by default, to clear the head.
- Damage numbers merge over a shorter window by default, so quick separate hits show their own numbers.

## [1.0.0] - 2026-09-10

### Added

- A health bar above each damaged zombie, hidden while its health is full and cleared on death.
- A floating number for each hit and each fire tick, showing the health actually lost, not the raw damage. A fully resisted hit draws nothing.
- Armor shown as blue segments under the health bar, one per armor part, from the first hit. A segment empties as its part breaks.
- Fire ticks merge inside a short window into one number, so damage over time stays readable.
- Configurable bars and numbers: turn either off, set the number lifetime and the fire-tick merge window, set the bar offset, and scale the whole overlay on top of the game's HUD scale.
- The overlay is hidden while the game is paused, a menu is open, the mission is not running, or a zombie is off screen.
