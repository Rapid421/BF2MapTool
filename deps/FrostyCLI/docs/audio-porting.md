# Ambient audio and SFX porting

These commands target the bundled FE-1063 SDK and the `swbf1`/`swbf2` profiles.
They support native **SoundWaveAsset** payloads and destination-game sound placement.
They do not automatically translate a whole level's sound graphs, mixer/reverb setup,
or gameplay logic between engine versions.

For the graph layer, [audio-graphs.md](audio-graphs.md) documents graph inspection,
packages and explicit schema/reference migration with `audio graph-*` commands.

The native package route preserves every variation, channel, segment, loop range,
selection weight, persistent-data prefix and seek table without decoding/re-encoding.
It creates independent chunk GUIDs and a new EBX identity, retaining the destination
template's audio-system policy and stream-pool references. Import requires a destination
bundle; the new EBX and all its chunks are linked into it.

## Find the recordings and their playback logic

```
frostycli --game swbf1 audio dependencies <sound-patch> --out dependencies.json
frostycli --game swbf1 audio inventory XP2/Sound/Planets/Bespin/Clouds_04 --out inventory.json
frostycli --game swbf1 audio inspect <sound-wave> --out wave.json
```

`dependencies` follows SoundWave, SoundPatch and SoundPatchConfiguration references.
It reports external system/mixer/other references as boundaries for destination mapping;
it does not recursively copy the global audio system. Reported `fields` paths identify
references in the deep JSON dump, including `__PartitionObjects`; use `ebx dump --deep`
and object selectors to find their corresponding editable EBX paths.

`inventory` reports each matching wave and each variation's segments, codec, channels,
sample rate, duration and loop segment range. Failures are included per asset and return
a nonzero exit code. Missing language chunks, unsupported codecs and dialogue with
subtitles are reported explicitly rather than silently omitted.

Sound waves contain recordings. A playable sound patch supplies loop behavior, random
selection, attenuation and other playback logic. An imported wave still needs a compatible
BFII patch before placing it in a map. Matching loop indices alone do not turn a one-shot
patch into a looping patch.

## Transfer the original audio

```
frostycli --game swbf1 audio export <2015-sound-wave> --out work/lift-package
frostycli --game swbf2 --save-as work/audio-test.fbproject audio import work/lift-package --as Mods/Audio/Lift2015 --template <BFII-sound-wave> --bundle win32/S2/Levels/CloudCity_01/CloudCity_01
```

`audio export` requires a new output directory. It writes `audio.json` and numbered
chunk binaries. The manifest records SHA-256 hashes; it is written after the chunks.
Exported game audio is local input data, not something to commit to this repository.

`audio import` creates a new wave; it never overwrites the template. It validates hashes,
chunk sizes, audio headers and blocks, segment/loop/seek ranges and target field types,
then serializes and reads the target EBX before adding anything. Added chunks are read
back and checked. On a failed import, created assets are reverted. `--dry-run` performs
the package and target-schema preflight without adding assets.

Packages support PCM16Big, XAS1, EaLayer31 and EaLayer32Pcm. Unknown codecs and
subtitled/localized variation records are rejected. NewWave/stream-bank formats need
their own typed importer and are not treated as SoundWave files.

Choose a BFII template appropriate to the sound's playback role. Package data overwrites
its sample/variation settings while destination policy/stream-pool references remain.
The target SDK must preserve all transferred fields on a binary round trip. A successful
import proves storage and schema compatibility; playback still needs an in-game check.

## Place and trigger a BFII sound patch

Clone or edit a suitable **BFII sound patch** with the existing EBX tools and point its
wave-player references at the imported waves. Preserve node order, connected ValueIndex
values, receive nodes and patch inputs when editing graphs. Do not copy a 2015 mixer or
compiled graph blindly into BFII.

```
frostycli --project work/audio-test.fbproject --save audio place <BFII-patch> --into <layer> --pos 1,2,3 --play-on-creation false
```

The result includes the new SoundEntityData instance GUID. Use that GUID with the existing
`connection add` command to wire verified event ports from a trigger in the same partition.
`--play-on-creation true` enables automatic playback; the default is false. Placement
does not change the patch's loop settings or recursively bundle its dependencies.
Use the existing bundle tools to include the patch and its dependencies; `bundle add
<wave> --to <bundle> --with-resources` includes every SoundWave chunk of a wave this
project added. It cannot bundle a **shipped** wave's chunks: FrostySdk refuses to add a
chunk that is in no bundle and was not added by a mod (`Bundles.Count == 0 && !IsAdded`),
which is every vanilla superbundle-resident chunk, and BFII stores sound data that way.
Such a bundle add reports `chunkRefused` and warns; the EBX is bundled but its audio is
not, so the sound is silent in game. To reuse a shipped recording in another bundle,
`audio export` it and `audio import` it as a new asset rather than bundling the original.

## WAV replacement

`import sound` remains the editable-WAV route. It takes uncompressed 16-bit PCM WAV;
the input channel count must match the destination wave so other variations keep a
consistent channel layout. The PCM route preserves 1–8 channels and representable
sample rates. EALayer3 preserves mono/stereo at 32, 44.1 or 48 kHz; automatic mode falls
back to PCM for wider/unsupported inputs. Explicit `--ealayer3` fails instead of downmixing.

A variation with multiple segments is refused unless `--flatten` is explicitly supplied.
Flattening replaces its start/loop/stop sequence with one segment. Use the native package
route when the original loops and seek data must survive. The compressed WAV encoder
still adds encoder padding; native packages avoid that conversion and preserve exact bytes.

Shared-chunk replacement appends the new recording and updates the selected segment's
offset instead of shifting other variations' audio/seek data. It can increase chunk size.
Overlapping segment rows are refused. WAV replacement disables seeking, clears the selected
variation's persistent prefix and updates its segment duration; native packaging preserves
the original seeking arrangement.

## JSON pipelines

The commands are available as `audio-inspect`, `audio-inventory`, `audio-dependencies`,
`audio-export`, `audio-import`, and `audio-place`:

```json
{
  "game": "swbf2",
  "game-path": "F:/STAR WARS Battlefront II",
  "steps": [
    { "op": "project-new" },
    {
      "op": "audio-import",
      "file": "work/lift-package",
      "as": "Mods/Audio/Lift2015",
      "template": "<BFII-sound-wave>",
      "bundle": "win32/S2/Levels/CloudCity_01/CloudCity_01"
    },
    { "op": "project-save", "file": "work/audio-test.fbproject" }
  ]
}
```

Read/export/place steps use `ebx` for the asset name; inventory uses optional `like`.
Output reports/packages use `out`. Placement uses `into`, `pos`, and optional
`play_on_creation`. `import-sound` additionally accepts `flatten: true`.

## Verification

```
dotnet build FrostyCLI.sln -c Release -p:Platform=x64
FrostyCLI.Tests/bin/x64/Release/FrostyCLI.Tests.exe --unit
FrostyCLI.Tests/bin/x64/Release/FrostyCLI.Tests.exe --filter AudioPortIntegrationTests
./tools/test-audio-port.ps1 -Game2015 "E:/STAR WARS Battlefront" -Game2017 "F:/STAR WARS Battlefront II" -WorkDir "work/audio-roundtrip"
```

Set `FROSTYCLI_GAME_PATH_SWBF2` to enable the integration tests. The compressed-stereo
test also needs ffmpeg and the bundled encoder. The cross-profile script uses the shipped
2015 Carbonite chamber lift sound: a stereo, seekable start/loop/stop asset. It exports,
imports through the pipeline, saves, reopens in a fresh process, and compares encoded
bytes, offsets, loop metadata and independent identities. It creates a disposable
`.fbproject` only; it does not create a map mod or launch the game.
