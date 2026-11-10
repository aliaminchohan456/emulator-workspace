# Artifacts

Generated evidence. The directory contents are ignored by git except this file
and `.gitkeep`.

- `host-baseline.json` / `.md` - last `tools/host-baseline.ps1` result.
- `phase0/` - manual evidence from the feasibility spike.
- `phase0/run-<timestamp>/` - one `tools/phase0-spike.ps1` run: `summary.json`
  (machine-readable checks), `transcript.txt` (every command and observed
  output), frames, target and controller reports, snapshot timings, and the
  repacked ramdisk probe.
- `phase0/ramdisk/` - ramdisk container experiments. Only the latest probe image
  is needed to reproduce the Magisk boot-input finding.

Treat everything here as derived output: it can be regenerated with the
harness, and it must never contain secrets. Controller tokens are redacted by
the CLI (length only), and the ADB keys live outside this directory.
