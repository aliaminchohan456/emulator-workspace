# Design verification — 2026-11-14

Browser prototype only. No production WPF code changed in this task.

Observed checks:
- JavaScript syntax checked with `node --check studio.js`.
- Rendered dark Studio and light library at 1440×900; no horizontal overflow.
- Rendered 980×620: document 980×620; details hidden; guest aspect ratio 1.6000;
  Android Recents bottom 566.5 px, inside the 620 px viewport.
- Corrected guest aspect fitting, small-canvas sample content scaling, short-height
  tool overflow, oversized card overflow buttons and create-form Cancel behavior.
- Create Cancel closed without adding a device; valid unique creation added a
  fourth simulated device. Search with no match showed the empty result.
- Recovery state disabled Android Back. Recovery consent's primary action was
  disabled before acknowledgement, enabled after it; Cancel preserved recovery.
- Palette search for settings + Enter opened Settings. Light appearance applied;
  reduced-motion switch exposed checked state.
- Saved rendered screenshots: studio-dark.jpg, devices-light.jpg,
  recovery-consent.jpg. Temporary browser viewport override reset afterward.

The preview uses native HTML dialog behavior and semantic controls, but no full
screen-reader, WCAG audit, Windows UIA, native high-contrast/DPI, actual guest
input, performance or hardware test is claimed. Other screens are design flows;
not every proposed native capability is implemented in the mock.

Actual WPF acceptance, backend wiring, large inventories and hardware benchmarks
are pending in the master plan. No .NET suite rerun was needed for these static
design/document files; prior 400-test evidence is historical code verification.
