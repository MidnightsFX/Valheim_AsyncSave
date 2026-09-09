**0.5.0**
---
```
Rebuilt for the chunked world save that shipped with Deep North. The previous release does
not load against this game version at all - ZDOMan.SaveAsync and ZoneSystem.SaveASync, the
two methods it replaced, no longer exist.

The game now writes one .chunk file per region and only rewrites the regions that changed,
which removes most of what this mod used to save you. What it does not touch is
ZDOExtraData.PrepareSave, which still deep-clones all eight extra-data dictionaries for every
ZDO in the world on every save - one allocation per ZDO, dirty or not, on the main thread.
Pre-serializing straight from live data is what lets us skip it, and on a large world that is
now the single biggest freeze this mod removes.

World save:
 - The snapshot is partitioned by chunk and walks only the chunks ZDOMan marked dirty, so the
   main-thread cost now scales with what changed rather than with world size.
 - The ZDO record encoder was rewritten for the new on-disk layout: the sector field is gone,
   positions pack to a Vector2s where they round-trip exactly, rotations use the packed form,
   and session-only data keys are filtered out.
 - Zone data follows ZoneSystem.Save's new shape - Vector2s zones, location prefabs stored as
   stable hashes, and the whole package compressed.
 - Aborting a snapshot now rolls back ZDOMan's save-state flip. Without it, a shutdown save
   that preempted a snapshot read the wrong dirty-chunk slot and could drop the portal chunk
   and every chunk touched during the walk.

Character save:
 - Rewritten for the v46 .fch layout: 205 stats across 10 achievement-difficulty slots, each
   with its own known-worlds, known-keys, commands, five enemy-stat maps, and the new pickable,
   food-eaten and pieces-placed maps.
 - The minimap capture now packs the explored BitArrays with one CopyTo and expands them on the
   worker, so the main thread copies 1 MB instead of 8 MB.
 - The startup format check reads the version, stat count, difficulty-slot count and map
   version out of vanilla's own IL, so a future format change stands this half down instead of
   writing a character file the game cannot read.

Also:
 - The dead-ZDO mirror is cleared on ZDOMan.LoadChunks as well as ZDOMan.Load. Since the
   chunked format landed, Load only runs for legacy worlds, so the mirror was carrying the
   previous world's ids across every modern world load.
 - The Splatform reference pointed at a publicized DLL that has never existed; it now
   references the shipped assembly.
 - The csproj comment claiming the source list was a wildcard is now true. It was still 30
   hand-maintained entries, which is the exact failure the comment was written about.
```

**0.4.0**
---
```
Prune the server's dead-ZDO table instead of letting it grow for the whole session.

Vanilla records every destroyed ZDO in ZDOMan.m_deadZDOs and only clears it when a world
loads, but reads it for one purpose: rejecting a create for a ZDO that died while a client's
packet was still in flight. On a large world that is millions of retained entries answering a
question about the last few seconds - a dictionary that is rehashed as it grows and walked by
every gen-2 GC.

Entries now expire after a configurable lifetime (default 60s). Pruning is O(expired) with no
scan of the table, and is server-side only.
```

**0.3.0**
---
```
Initial release.
```
