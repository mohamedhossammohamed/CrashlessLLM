# Architecture Deviations from Design Docs

This document records intentional deviations between the research architecture documents and the production implementation, with rationale for each.

## macOS Available Memory Calculation

### Docs specification

The architecture documents specify a conservative policy for macOS:

> "Intentionally excludes inactive/compressed pages to avoid memory-pressure stalls."
> — uses only `vmstat.free_count * page_size`.

### Production deviation

The production code uses:

```cpp
uint64_t available_pages = vmstat.free_count
                         + vmstat.inactive_count
                         + vmstat.speculative_count;
```

Excluded: `wired_count` (kernel/unreclaimable pages only).

### Rationale

1. **False rejection rate**: On a typical macOS system with 16-32 GB RAM, `free_count` alone is often only 1-3 GB because the kernel aggressively caches files in `inactive` memory. A 1.2 GB Q8_0 model with 4096-token context (predicted ~2.3 GB with margin) would be rejected even when 8-12 GB of reclaimable memory exists.

2. `inactive_count` is memory the kernel can reclaim immediately under pressure (file caches, unused allocations).

3. `speculative_count` is memory the kernel speculatively freed and can reclaim.

4. `wired_count` is truly unreclaimable (kernel structures, device mappings) and is correctly excluded.

5. The 30% safety margin still provides headroom for the OS reclamation overhead.

### Risk

If macOS cannot reclaim `inactive` pages quickly enough during model loading, the process could experience memory-pressure stalls (swap/thrashing) before the gate triggers. However, since llama.cpp on macOS uses mmap for weights when possible, and the gate still computes a conservative heuristic, this risk is bounded.

### Reversal criteria

If field reports show `inactive` reclamation stalls during load, revert to `free_count + speculative_count` only, or add a configuration knob for `ConservativeMacOSMemory`.
