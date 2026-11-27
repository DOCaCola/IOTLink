# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.3.1](https://github.com/DOCaCola/IOTLink/releases/tag/v2.3.1) - 2026-11-27

- Updated Newtonsoft.Json to 13.0.4 across the service, agent, API, and bundled add-ons to address [CVE-2024-21907](https://github.com/advisories/GHSA-5crp-9r3c-p9vr).

## [2.3.0](https://github.com/DOCaCola/IOTLink/releases/tag/v2.3.0) - 2026-11-27

- WindowsMonitor publishes per-display desktop and active signal dimensions, refresh rate, monitor name, active HDR state, primary display flag, connector type, and rotation over MQTT with Home Assistant discovery.
- The agent caches display information and refreshes it when Windows reports a change. WindowsMonitor reduces repeated CPU, network, disk, uptime, and user queries. MQTT topic formatting and discovery metadata are cached.
- MQTT clients stop their reconnect workers on shutdown. Disabled monitors release their resources, inactive process entries expire, and configuration reloads reuse identical agent add-on assemblies.
- Fragmented screenshots use less temporary memory. The service reuses per-session agent handles and skips caller lookup for suppressed log messages.

## [Example - Keep at bottom of this file]
### Added
- Added CHANGELOG file. [@alexslx](https://gitlab.com/alexslx).
- Second important change. [@alexslx](https://gitlab.com/alexslx).

### Changed
- Changed something. [@alexslx](https://gitlab.com/alexslx).
- Important fix with reference. Fix #2. [@alexslx](https://gitlab.com/alexslx).

### Removed
- Something was removed. [@alexslx](https://gitlab.com/alexslx).