# AsyncSave

AsyncSave moves Valheim's world and character saves off the main thread, so a save no longer
freezes the game. Nothing about the save *format* changes — the bytes it writes are the bytes
vanilla would have written.

The two halves are independent and neither syncs anything over the network, so the same DLL runs
unmodified on a dedicated server, a client, or a host, any mix is supported.

## How it works

### World save (server or host)

Vanilla's `ZNet.SaveWorld` clones every ZDO in the world into a fresh list, on one frame, and only
then hands it to the writer thread. AsyncSave replaces that with a coroutine that walks
`ZDOMan`'s sectors across many frames, serializing each ZDO straight into a reusable buffer, while
the writer thread waits on it.

### Character save (anywhere there is a local player)

Vanilla's minimap save writes 8.4 million individual booleans through a `BinaryWriter`, then
gzips the result and writes the `.fch` synchronously. AsyncSave bulk-copies the explored arrays
(two `memcpy`s producing the identical byte sequence), then hands compression, hashing and the
file write to a background worker. The parts that touch Unity-owned or gameplay-mutated state stay
on the main thread.

The character save made on quit, logout and shutdown still runs synchronously and in full before the
scene goes away, so it is never in flight; only its minimap step uses the bulk copy.

### Automatic stand-downs

If the mod cannot safely run — it fails to patch `ZNet.SaveWorld`, the `.fch` layout is not the one
this build was written against, the revision hooks are missing, or the host is big-endian — it
disables the affected half or falls back to `FullScan` **for that session only** and logs why. Your
config file is never rewritten, so the setting you chose is still there next launch.

## Requirements

BepInEx. No dependencies on other mods. Incompatible with SmoothSave (they patch the same paths);
BepInEx will refuse to load both.

---

If you enjoy my mods donations are appreciated. My mods will never be paid and donations are not required.

<a href="https://ko-fi.com/midnightsfx"><img height="36" src="https://storage.ko-fi.com/cdn/kofi2.png?v=3" alt="Support me on Ko-fi"></a>
