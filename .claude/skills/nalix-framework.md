# Nalix.Framework

## Triggers
- Registering or resolving services via `InstanceManager`
- Adding or modifying object/buffer pool behavior
- Working with Snowflake IDs across distributed nodes
- Adding background or recurring tasks via `TaskManager`

---

## Rules

### InstanceManager (DI)
- **Instance cache by type** — `Register<T>(instance)` stores by type key; `GetOrCreateInstance<T>()` returns cached or creates new
- Key methods: `Register<T>(T instance)`, `GetOrCreateInstance<T>([args])`, `GetExistingInstance<T>()`, `HasInstance<T>()`, `RemoveInstance(Type)`, `Clear(bool dispose)`
- `RegisterForClassOnly<T>()` skips interface registration — use when type implements multiple interfaces but only the concrete type should be resolvable
- All registrations must happen **before** `NetworkApplication.Build()`
- Do not mix with `Microsoft.Extensions.DependencyInjection` — creates a parallel container

### Object Pooling (`ObjectPoolManager`)
- Uses `PoolType<T>.Id` — a compile-time unique integer per type used as an array index on the hot path (no dictionary lookup per rent/return)
- **`IPoolable.ResetForPool()` must clear every mutable field** — `PacketSchemaGenerator` auto-generates `ResetForPool()` for `PacketBase<T>` subclasses (including `[SerializeIgnore]` transient fields, while preserving `[SkipClean]`/`[Inject]` members), and `NALIX107` warns if a manual `override void ResetForPool()` forgets to reset a mutable field or auto-property.
- Outstanding balance = Get count − Return count; negative = pool leak; logged as a warning but not thrown
- Health auto-monitored: miss rate > `ElevatedFailureThreshold` → health escalates to critical
- Trim cycles: deep trim every 6 cycles, hot pools (high hit rate) get light trim, cold pools get aggressive trim
- Safety floor: pool never shrinks below `Max(MinimumKeepObjects, capacity / 12)`

### Buffer Pooling (`BufferPoolManager`)
- Two-level: internal bucket pools → `ArrayPool<byte>.Shared` fallback when no suitable bucket
- Suitable-size result is cached (request size → actual pool size) to avoid repeated binary searches
- Adaptive expansion: triggers when usage > soft cap AND miss rate is high AND within memory budget
- Shrink is conservative: `ShrinkSafetyPolicy` enforces 25% minimum retention, 20% max shrink per cycle, absolute minimum of 1 buffer per pool
- Memory budget is cached with a TTL — budget changes (e.g., after memory pressure event) do not take effect until TTL expires

### Snowflake IDs
- Layout: `Type(8) | Timestamp(32) | Sequence(14) | MachineID(10)` — 64 bits total
- Timestamp uses Unix seconds; sequence is atomic and resets on each new second
- MachineID is 10 bits = max 1023 distinct nodes — **same MachineID on two nodes = guaranteed ID collision**

### TaskManager
- Worker priorities: `LOW` < `NORMAL` < `HIGH` < `URGENT` — use `URGENT` for health checks/critical paths, `HIGH` for dispatch, `NORMAL` for cleanup, `LOW` for background telemetry
- Workers must be async throughout — no `Thread.Sleep`, no blocking calls inside workers
- Recurring tasks configured via `IRecurringOptions` with a configurable interval

---

## Checklists

### Register a new service
1. Implement the service interface
2. Before `Build()`: `InstanceManager.Register<IMyService>(new MyService())`
3. Resolve anywhere: `InstanceManager.GetOrCreateInstance<IMyService>()` or `GetExistingInstance<IMyService>()`
4. If the service needs other services, resolve dependencies via `GetExistingInstance<T>()` in the constructor

### Add a pooled type
1. Implement `IPoolable` on the class
2. `Reset()`: clear **every** field — strings to `null`, collections to `Clear()`, primitives to `default`
3. Configure: `ObjectPoolManager.Configure<T>(options => { options.InitialCapacity = N; })`
4. Use: `ObjectPoolManager.Rent<T>()` / `ObjectPoolManager.Return(obj)` — never `new T()` on hot path
5. Verify outstanding balance stays near zero under load — negative = missing `Return()` call

### Configure distributed Snowflake
1. Assign a unique `MachineID` per node (0–1023) via environment variable or per-node config
2. Never share a MachineID between instances, even in the same datacenter
3. IDs are monotonic within a node's sequence window — cross-node ordering requires logical clocks

---

## Gotchas

- **`Reset()` failures are silent**: `ObjectPoolManager` calls `Reset()` before returning an object to pool, but does not validate that the method actually cleared state. A field left populated means the next renter sees stale data from a previous request — typically manifests as intermittent, context-dependent bugs.

- **`PoolType<T>.Id` is per-assembly, not per-AppDomain**: If the same type is pooled from two different assembly load contexts, they get different `Id` values and different pools — objects rented from one pool cannot be returned to the other.

- **`BufferPoolManager` budget TTL delay**: After a memory pressure event, the budget is not recalculated until the TTL expires. If you see unexpected allocation spikes right after pressure events, check the TTL configuration.

- **Shrink safety floor can cause churn**: If `MinimumKeepObjects` is set too high relative to actual concurrent demand, the pool never shrinks below that floor — wasting memory. If too low, the pool shrinks and then immediately expands under burst load.

- **Snowflake monotonicity is per-node only**: IDs are monotonically increasing within a single node's sequence window. Across nodes they are not ordered. If you need cross-node event ordering, use logical timestamps, not Snowflake IDs.

- **`InstanceManager.Register` after `Build()` is silently ignored or throws**: Depending on the implementation, registering after Build() may fail silently. Always complete registration during the builder phase.
