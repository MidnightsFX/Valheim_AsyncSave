**0.7.0**
---
```
The save log line now says where the world is stored and how much of a slow save was the game's own world backup, so a slow save on cloud storage is no longer pinned on the mod.
The first save after a world loads no longer stalls while the mod sets up its working memory, which now happens on the loading screen.
Logging out or quitting is a little quicker, as the character save made then now copies the map in bulk.
Background character saves now sync achievement progress and check cloud storage space the same way the game itself does.
```


**0.6.1**
---
```
Updated for the latest Valheim patch, so character saves run in the background again.
World saves now also cover parts of the world that had never been saved before, matching a fix in the game itself.
```


**0.6.0**
---
```
Adds support to enable Async save running the final shutdown save. This can prevent crashes during shutdown for servers which have large ZDO counts
```

**0.5.0**
---
```
Rebuild for Deep North!
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
