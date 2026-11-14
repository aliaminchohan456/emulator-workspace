# Research and design decisions

Reviewed 2026-11-14. Owner input: `Emulator-UI-UX-Study.md` in Downloads,
prepared by Grok against d43abd5. `codex-review-8.md` contains the separate
code review, not the UX study. Third-party recommendations were evaluated as
reference material, not repository instructions.

## Independent primary-source findings

| Source | Verified workflow | Decision for this product |
|---|---|---|
| [BlueStacks side toolbar](https://support.bluestacks.com/hc/en-us/articles/360055672212-How-to-use-the-Side-toolbar-in-BlueStacks-5) | Common emulator actions are exposed from the edge toolbar; it can collapse. | Put frequent device tools next to the display; keep secondary commands in overflow. Do not reproduce all of its tools. |
| [BlueStacks shortcuts](https://support.bluestacks.com/hc/en-us/articles/4402768474381-How-to-view-or-edit-shortcut-keys-on-BlueStacks-5) | Shortcut editing includes duplicate-key warnings and restoring defaults. | Tooltips and palette show the same binding; a later editor rejects conflicts before saving. |
| [BlueStacks instance manager](https://support.bluestacks.com/hc/en-us/articles/360052834092-How-to-create-and-manage-instances-using-the-Multi-instance-Manager-on-BlueStacks-5) | Search and instance management are distinct from the guest display. | Separate the library from focused single-device use. |
| [LDPlayer multi-instance guide](https://www.ldplayer.net/support/introduction-to-ldmultiplayer.html) | Manager exposes create/clone, batching and arrangement; optimization settings include frame rate and memory options. | Make environment management deliberate. Batch operations require exact target review and remain later work. |
| [LDPlayer usage tips](https://www.ldplayer.net/support/tips-for-using-ldplayer.html?n=63773157) | A mini mode offers a smaller emulator window. | Preserve focus/pop-out affordances; implement them as views of the same managed instance. |
| [MuMu multi-instance guide](https://www.mumuplayer.com/help/win/multi-instance-guide.html) | Manager provides create, copy, backup, search and arrangement. | Expose recovery and environment reuse in the library. These are capabilities, not evidence that one card layout fits every product. |
| [Microsoft command bar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar) | Common actions and secondary overflow can adapt to available width. | Replace wrapping command rows with an explicit priority model. |
| [Microsoft navigation](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview) | Adaptive navigation preserves access while reducing pane width. | Shell navigation and device selection remain separate; collapse details before reducing the display. This is interaction guidance, not a WPF control dependency. |
| [Microsoft materials](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/materials) | Mica is a foundation material; acrylic is for transient surfaces. | Optional material treatment belongs in chrome/flyouts, not behind a continuously updating Android frame. Solid fallbacks are first-class. |
| [WPF .NET 10 changes](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100) | Fluent support continues to evolve, including High Contrast and control fixes. | Keep existing dictionaries for the first slice. Verify any Fluent adoption against the installed SDK rather than assuming every control is finished. |

These are documented workflows, not measurements from installed competitor
apps. No competitor installation, performance benchmark, accessibility audit,
or exact pixel/color extraction was performed for this proposal. Grok's remote
screenshot directory was not available here. No competitor assets are shipped.

## Current code audit

Source baseline: 24cc54d, plus no production edits in this design task.

| Evidence | Implication |
|---|---|
| `MainWindow.xaml`: top WrapPanel mixes lifecycle, Android navigation, APK, sample launch, scaling and global commands. | Grouping and overflow should be fixed before adding visual effects. |
| Same file: fixed 238 DIP device column and 318 DIP inspector, minimum window 980 × 620. | At narrow sizes, chrome consumes most of the content width. Inspector should collapse. |
| Same file: zoom-out content is `--'`. | Replace with a proper minus vector; retain an accessible action name. |
| `MainWindow.xaml.cs`: Ctrl+K, F11, Escape and viewport handlers already exist. | Preserve working behavior; do not create a second disconnected command system. |
| `Themes/Common.xaml`, Dark/Light/HighContrast, `Services/ThemeManager.cs`. | Extend semantic resources and focus states; preserve High Contrast precedence. |
| `MainViewModel.cs`: lifecycle, capture, install, root and palette commands already wired. | Recompose existing commands first. UI is not permission enforcement. |
| `AgentSessionsWindow.xaml`, `CommandPaletteWindow.xaml`. | These surfaces need coherent density and tokens, not feature duplication. |

This turn audits source and renders the new browser prototype. Existing WPF
screens observed in earlier verification are historical; no fresh native
redesign or native UX acceptance is claimed here.

## Where this differs from Grok's recommendation

1. **Viewport space:** rich cards in the full library, compact rows in Studio.
   Three permanently wide columns would recreate the current crowding.
2. **Accent:** a restrained ice-blue default, with a tested system-accent option
   later. Arbitrary accent colors must not reduce contrast on primary buttons.
3. **Root styling:** amber marks privileged context; red marks destructive or
   failed operations. Purple is not necessary to communicate privilege.
4. **Chrome:** keep native resize, Snap, Alt+Space and caption behavior. Dark
   native chrome comes before bespoke title-bar work. A custom title bar is a
   compatibility task, not a cheap cosmetic toggle.
5. **Component completeness:** settings, menus, focus, validation and disabled
   explanations matter as much as the landing composition. No generic rounded
   card around every label.
6. **Telemetry:** do not display the study's example FPS/latency as a product
   claim. A value needs units, sampling window, freshness and a real source.
7. **Scope:** recording video, resource controls, key mapping and synchronized
   input are separate engineering work. They are not unlocked by adding icons.
8. **Effort:** use exit gates rather than the study's short calendar estimates.
   DPI, screen readers and frame pacing need real Windows runs.

## Chosen art direction

**Graphite Studio** is a working design label, not a product rename. Warm neutral
dark surfaces, precise separation, readable hierarchy, original 1.5 px line
icons and one pale blue action color. The guest supplies the dominant color.
**Porcelain** uses the same structure with light neutral surfaces and a deep
blue accent. Full-window gradients, glass on every panel, neon outlines,
promotional cards and perpetual animation are intentionally absent.

The premium impression should come from consistent behavior: selection never
jumps, operations explain their destination, errors preserve the next action,
dialogs restore focus, and the frame remains responsive while panels move.
