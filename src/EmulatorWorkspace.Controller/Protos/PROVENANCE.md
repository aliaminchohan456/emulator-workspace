# Vendored controller protos

These files are copied unmodified from the Android Emulator SDK package that
this workspace pins. The SDK ships the protos inside the emulator package, so
the generated client always matches the exact engine binary under test.

| Field | Value |
| --- | --- |
| Emulator package | `emulator` 37.2.12 (build_id 16428233) |
| Source directory | `<ANDROID_SDK_ROOT>\emulator\lib` |
| Files | `emulator_controller.proto`, `snapshot.proto`, `snapshot_service.proto` |
| Upstream project | Android Emulator (`platform/external/qemu`), Apache License 2.0 |
| Retrieved | 2026-11-10 |

Re-copy procedure after an intentional engine upgrade:

```powershell
$lib = "$env:ANDROID_HOME\emulator\lib"
Copy-Item "$lib\emulator_controller.proto","$lib\snapshot.proto","$lib\snapshot_service.proto" `
  src\EmulatorWorkspace.Controller\Protos -Force
```

Do not edit the vendored protos by hand; regenerate from the SDK on upgrade so
that the client and engine stay in lockstep. `snapshot.proto` is imported by
`snapshot_service.proto`, and `emulator_controller.proto` imports only the
protobuf well-known `empty.proto`.
