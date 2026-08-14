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
