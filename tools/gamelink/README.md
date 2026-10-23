# FrostyGameLink

The in-process side of `frostycli mod test` and `frostycli game`: a DLL injected into a running
Star Wars Battlefront II (2017) that answers text commands on `127.0.0.1:31500`.

- **Build:** `build.bat` (any Visual Studio 2019+ with the C++ workload) -> `bin\FrostyGameLink.dll`.
  `tools/gametest/autotest.ps1` picks it up from there.
- **Inject:** `frostycli mod test <mod>|--vanilla --keep` launches the game through Frosty Mod Manager
  and injects it (see the CLI README for the one-time task setup).
- **Use:** `frostycli game ...` from the CLI, or `python gl.py <command>` here. Any gamelink command
  can be sent raw with `frostycli game send <command>`.

The logic and property feeds and the in-game overlay are described in [TRACE.md](TRACE.md).
The module also drives a free camera, picking/selection for the Frosty GameView plugin, screenshots
(`grabframe`), and reflection reads/writes (`fields`, `get`, `set`, `entities`, ...).

It is bound to one exact game build (module size 0xE45C000) and refuses to run against anything
else; every code patch also refuses bytes it doesn't expect.

## Files

| file | what |
|---|---|
| `src/main.cpp` | the module |
| `src/imgui/` | Dear ImGui 1.78 (MIT, `LICENSE.txt`) for the overlay |
| `field_hashes.csv` | event/field names Frosty knows (from EBX dumps) |
| `event_hashes_extra.csv` | more names: exact ones, and `~` guesses (see TRACE.md, Names) |
| `harvest_hashes.py`, `harvest_source.py`, `harvest_combo.py` | grow `event_hashes_extra.csv` |
| `gl.py` | Python client (`gl.py follow [--prop] [filter]` streams a feed) |
