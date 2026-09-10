# Copilot code-review instructions

This repo is a BepInEx 6 (IL2CPP) Harmony mod for *The Last Stand: Aftermath*. Plugins derive from `BasePlugin` and call the game through Il2CppInterop proxy assemblies. Review with these traps in mind; a general C# review misses most of them.

## IL2CPP Harmony traps

- **Getter/setter patches often never fire.** il2cpp inlines trivial accessors, so a `MethodType.Getter`/`.Setter` patch silently does nothing and the caller reads the backing field directly. Flag a new getter patch used as the only mechanism. The reliable change is mutating the backing field at a load hook, or reimplementing the method that reads the value.
- **No `is`/`as` across the interop boundary.** Flag `is`, `as`, or a direct cast on a game type. The correct form is `x.TryCast<T>()` then a null check.
- **Patch the type that defines the member.** A game subtype does not override an inherited member, so a patch on the subtype never binds. Expect the patch on the base type, narrowed inside with `TryCast`.
- **No `foreach` over game collections.** IL2CPP collections lack the enumerator pattern. Flag `foreach` on a game collection; expect a count plus an indexer.
- **An injected `MonoBehaviour` needs the full trio:** `ClassInjector.RegisterTypeInIl2Cpp<T>()`, an `(IntPtr)` constructor, and public Unity message methods (`LateUpdate`, `OnGUI`). Flag a new injected type missing any one.
- **Guard game lookups.** `GetComponent`, `ObjectRoot`, `View`, and the like return null often. Flag an unchecked dereference inside a patch.

## Structure and tests

- **Pure logic is separated and tested.** Decision logic that does not need the running game (filters, rules, stack/state math) lives in its own file with no BepInEx or Il2Cpp references, unit-tested under `tests/`. Flag new pure logic buried in the patch file, and new pure logic added with no test.
- **Patches** prefer a postfix, and tie the `Harmony` instance to the plugin GUID.

## Release and config hygiene

- **Verbose ships off.** A `Verbose` or diagnostic config binds with default `false`. Flag a diff that makes the shipped default `true`. Diagnostic tracing goes on `LogDebug`; keep `LogInfo` quiet.
- **The plugin GUID never changes.** It is `com.ivmakk.tlsa.<mod>`, the BepInEx identity and the config file name. Flag any edit to it.
- **No committed build output.** Flag `bin/`, `obj/`, `dist/`, or a game DLL in the diff. Game `<Reference>` entries keep `<Private>false</Private>`.
- **Changelog matches the change.** A player-visible change adds an `[Unreleased]` entry to `CHANGELOG.md` in player-facing wording. An internal-only refactor gets none.
