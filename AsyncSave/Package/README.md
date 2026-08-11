# AsyncSave

AsyncSave moves Valheim's world and character saves off the main thread, so a save no longer
freezes the game. Nothing about the save *format* changes — the bytes it writes are the bytes
vanilla would have written.

The two halves are independent and neither syncs anything over the network, so the same DLL runs
unmodified on a dedicated server, a client, or a host, in any mix of installed and not.

## How it works

### World save (server or host)

Vanilla's `ZNet.SaveWorld` clones every ZDO in the world into a fresh list, on one frame, and only
then hands it to the writer thread. AsyncSave replaces that with a coroutine that walks
`ZDOMan`'s sectors across many frames, serializing each ZDO straight into a reusable buffer, while
the writer thread waits on it.

Because the walk now spans frames, the world can change underneath it. Three things keep the
result consistent:

- **Sector scratch copies.** Each sector's live list is copied atomically before iterating, so a
  removal cannot shift indices out from under the walk.
- **`AddToSector` tracking.** A ZDO that is created, or that moves into a sector the walk has
  already passed, is queued and serialized in a catch-up pass at the end. Without this, anything
  that moved backwards across a sector boundary mid-save would silently vanish from the file.
- **Revision reconciliation.** A ZDO whose data changed after it was serialized is re-encoded in
  place, so the save is not one revision stale.

The buffer and the dedup table are split into 64 MB segments rather than living in single arrays,
because a .NET array cannot exceed 2 GB and a large world needs more than that.

### Character save (anywhere there is a local player)

Vanilla's minimap save writes 8.4 million individual booleans through a `BinaryWriter`, then
gzips the result and writes the `.fch` synchronously. AsyncSave bulk-copies the explored arrays
(two `memcpy`s producing the identical byte sequence), then hands compression, hashing and the
file write to a background worker. The parts that touch Unity-owned or gameplay-mutated state stay
on the main thread.

Quitting, logging out and shutdown always use the untouched vanilla path, so the last save before
the scene goes away is never in flight.

## Configuration

`BepInEx/config/MidnightsFX.AsyncSave.cfg`. All values apply immediately — no restart.

### 1 - General

| Setting | Default | What it does |
|---|---|---|
| Save interval (seconds) | `1800` | World autosave period. Left at the vanilla 1800 this is not applied at all, so another mod setting the interval still wins. Below 60 disables vanilla's pre-save warning; the save itself still runs. |
| Logging | `Simple` | `Off`, `Simple` (one line per save), `Detailed` (counts and buffer figures), `Debug` (adds per-phase timings). |

### 2 - World Save

Server-side only; a pure client ignores this whole section.

| Setting | Default | What it does |
|---|---|---|
| Enabled | `true` | Off restores the vanilla save path completely. |
| Async manual saves | `true` | Whether the in-game menu's Save button returns immediately or blocks until the world is on disk. The `/save` console command is already async in vanilla and is unaffected. |
| Slice budget (ms) | `2` | Ceiling on main-thread time per slice. Lower = smoother frames, longer save. |
| Adaptive budget | `true` | Scale each slice to the frame time the game is not already using, so a save costs little while the game is struggling. Off gives a fixed budget and the shortest save. |
| Frame headroom (%) | `25` | How much longer a frame may take while saving, as a percentage of what it costs without us. 25 means a 4 ms frame may become 5 ms. Ignored when the adaptive budget is off. |
| Reconcile mutations | `Dirty` | `Dirty` re-encodes only what the revision hooks flagged and costs nothing on an idle world. `FullScan` re-checks every ZDO — correct regardless of hook coverage, but it walks the whole world every save. |

### 3 - Character Save

| Setting | Default | What it does |
|---|---|---|
| Enabled | `true` | Off restores the vanilla character save. |

### Automatic stand-downs

If the mod cannot safely run — it fails to patch `ZNet.SaveWorld`, the `.fch` layout is not the one
this build was written against, the revision hooks are missing, or the host is big-endian — it
disables the affected half or falls back to `FullScan` **for that session only** and logs why. Your
config file is never rewritten, so the setting you chose is still there next launch.

## Requirements

BepInEx. No dependencies on other mods. Incompatible with SmoothSave (they patch the same paths);
BepInEx will refuse to load both.
