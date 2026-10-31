# Family placement and MEP behavior (v0.6.2)

These changes address issues #11–#13 and shipped in **v0.6.2**. Update both the server and the matching Revit plugin together; close Revit before installing, then restart Revit and the client's MCP server connection so the client sees the new schemas. Tool counts remain 40 by default, 229 with `--toolsets all`, and 232 with adaptive bake.

## Place a door, window, or furniture instance

`revit_create_point_based_element` accepts `typeId`, `x`, `y`, optional `z` (default 0), `level` (name), and optional `host_id`. Coordinates are model coordinates in millimeters. Resolve IDs from the active document; linked-model IDs are not local hosts.

| Family placement type | Behavior |
|---|---|
| `OneLevelBased` | Place by point and level. An unnecessary `host_id` is ignored with a warning. |
| `OneLevelBasedHosted` | Require `host_id`; hosted doors/windows require a local wall. Use the host + level overload. |
| `WorkPlaneBased` | Reject: a host element alone does not identify a face/work plane and orientation. |
| Other types | Reject as unsupported by this tool. |

There is no automatic host search. If a hosted family fails because its host is missing, resolve the intended host from the user's context; ask when that choice is unclear. Family-list tools do not currently expose `FamilyPlacementType`. An explicitly named missing level is an error; omitting the name retains the lowest-level default.

Example arguments after resolving a door type, a wall and a level in the current model (IDs below are illustrative):

```json
{"typeId":12345,"host_id":67890,"x":2500,"y":2000,"z":0,"level":"Level 1"}
```

Before committing, the handler regenerates and checks the actual host and `LocationPoint` against the request, with a 1 mm Euclidean tolerance. A mismatch rolls back. Success retains `elementId`, family/type/category and adds `placement_type`, actual `host_id`, `location_mm`, and `warning`. Some constrained families cannot honor a requested elevation; those placements fail rather than reporting the wrong location as success. Lighting and air-terminal tools retain their own contracts.

## Extend a pipe from an open connector

With `systemTypeId` omitted, `revit_create_pipe` searches for open physical piping End connectors within 1 mm of its start point:

- **One match:** use Revit's connector-based overload. The new pipe inherits system type and diameter and is connected during creation. A separate connect call is unnecessary.
- **Several matches:** return `created: false`, `reason: ambiguous_start_connector`, and candidate element/connector IDs. Resubmit with `startElementId` and, if needed, `startConnectorId` (`Connector.Id`, not an ordinal).
- **No match:** retain the first available `PipingSystemType` fallback and report the selected type. An explicit connector selection that does not match fails instead of falling back.

A requested diameter differing from the connector by more than 0.01 mm fails before mutation. Omit it to inherit or model an explicit transition. Providing `systemTypeId` creates an independent pipe and cannot be combined with start-connector selection. Successful responses identify the actual type ID/name, diameter, `system_type_source` (`connector`, `explicit`, or `default`), and connection information.

These are public MCP argument names. The internal plugin command uses `system_type_id`, `start_element_id`, and `start_connector_id`; do not send those names to `revit_create_pipe`. Response fields remain snake_case.

`revit_connect_mep_elements` still uses `ConnectTo`. It rejects different assigned piping/HVAC system type IDs with `connected: false`, `reason: system_type_mismatch`, and both types. This applies to the selected ports, including explicit selections. Separate systems with the same type and unassigned equipment ports are allowed; unreadable system metadata fails explicitly. Physical connector domains must match. A successful connection does not promise that Revit merges system instances or fills a geometric gap with new pipe/duct geometry.

Connection verification accepts a direct connection or two distinct physical ports on one shared pipe/duct fitting. Repeating either connection is a no-op (`already_connected: true`), including with explicit connector selection. It does not infer connectivity through arbitrary equipment or a longer network path.

## Network continuity

A modelled pipe network should be continuous, so Revit reads it as one system and analysis, schedules and disconnect checks are meaningful. Agents apply this by default when they create or rebuild pipes; the `revit_pipe_network` prompt runs the check on an existing network.

- **Structures are junctions.** A pipe end inside a manhole, chamber, gully, channel, tank or separator stays open. Do not add a fitting there and do not report it as a defect.
- **Pipes that meet without a structure get a fitting**: 2 pipes → elbow (union when collinear), 3 → tee, 4 → cross, and a transition between diameters. Short connections from gutters, gullies or downpipes count as pipes. A pipe that ends on the side of another pipe (a gutter or gully connection on a collector) gets a tee: the collector is split at that point. `revit_connect_pipe_network` does this for a whole system or a list of pipes. Run it with `dryRun=true` (the default) to see the plan, then with `dryRun=false`. `revit_create_mep_fitting` handles a single junction by connector IDs.
- **Create segments that meet.** Consecutive segments of one drawn line end at the same point, plan position and level included, so the fitting needs no pipe movement. Add the fittings in the same task.
- **Drawings are evidence.** Do not move, trim or re-slope pipes to make a fitting fit. Small deflections of a few degrees may refuse an elbow; report them.
- **Keep filters working.** When view filters select a network by an instance value such as Comments (`revit_list_view_filters`), give new fittings the same value as the pipes they join.
- **Check the result.** `revit_connect_pipe_network` reports open ends before and after; `revit_find_mep_disconnects` lists them for any domain. The remaining ones should all be inside structures or listed as unresolved.

`revit_connect_pipe_network` treats as structures the family instances in Generic Models, Plumbing Equipment (Revit 2024+), Plumbing Fixtures, Mechanical Equipment and Specialty Equipment. A group of ends counts as inside a structure when its centre falls in the structure's bounding box grown by `structureMarginMm`. The bounding box of a rotated family is axis-aligned and can be larger than the family. When the pipe and the structure both have a Comments value and they differ, the structure belongs to another network (for example an electrical chamber beside a drain) and is ignored. Unresolved reasons:

| Reason | Meaning |
|---|---|
| `open_end` | A lone end, not inside a structure. |
| `diameter_change_at_bend` | Two ends of different diameters that also change direction. |
| `system_type_mismatch` | Ends from different piping system types. |
| `no_straight_run_for_tee` / `no_straight_runs_for_cross` | No pair of ends continues within 5°. |
| `too_many_ends` | More than four ends meet. |
| `branch_along_pipe` | An end lies on another pipe's axis but points along it, so a tee would not fit. |

## Pipe clashes

`revit_find_pipe_clashes` checks straight pipes and conduits as cylinders: two curves clash when the distance between their centrelines is less than the sum of their outer radii plus `clearanceMm`. It is exact for round sections and fast enough for a whole site network. Rectangular ducts and cable trays are not checked; use `revit_clash_detection` for categories and solids.

Not reported as clashes:
- curves joined directly or through one fitting;
- ends that meet at a point (a junction for `revit_connect_pipe_network`);
- crossings inside a structure of their own network: same rules as network continuity, so a structure whose Comments differ from both curves does not excuse the crossing.

Each clash has a `kind`:
- `crossing`: curves at an angle;
- `parallel`: axes within 5°, for example tubes of a bank laid closer than their diameter;
- `duplicate`: two curves on the same axis, where one should be deleted.

A joint left without a fitting, where one curve carries on where the other ends (a polyline vertex), is not a clash. It is listed in `junctions_without_fitting` so `revit_connect_pipe_network` can join it.

For each clash, the levels are taken on the same vertical at the plan crossing. `raise_*_mm` and `lower_*_mm` say how far each curve would have to move there to clear the other by the clearance.

`revit_resolve_pipe_clash` moves one end of a curve vertically and keeps the other end, so the change at the crossing is the end move times the crossing's share of the length. Decide which curve and which end may move before calling it:
- **Gravity networks** (drainage, stormwater) keep the levels read from drawings, and cannot dip under a crossing because they would run uphill. `keepFallDirection` refuses a move that reverses the fall. Move the end whose level was assumed, usually to pass over the other pipe.
- **Pressure pipes and conduits** may move freely within their cover. A local offset with elbows is not done by this tool yet; moving a free end is.
- **Joined ends** are refused: moving them would break the fitting. Move or disconnect the joined element first, or choose the other end.

Run with `dryRun=true` first and check `clashes_after`; the moved curve can meet another curve in its new position.

## Interpret MEP membership and connector counts

`revit_list_mep_systems`, `revit_analyze_mep_network`, and `revit_get_system_inventory` share membership reads:

- Piping uses `PipingNetwork`; HVAC uses `DuctNetwork`; electrical uses `Elements`.
- `element_count` reflects network membership. Terminals and base equipment are also inspected; inventory deduplicates their union by element ID. Do not add the counts blindly: network collections can already include equipment/terminals.
- An empty/delete recommendation requires no network members, no terminals, and no base equipment. Failed membership reads are errors, not empty systems.
- Open-connector analysis counts physical End connectors in the system's domain. Electrical ports on HVAC equipment do not inflate the HVAC count.

## Evidence and limits

Revit 2022/2024/2027 builds passed at the v0.6.2 acceptance checkpoint; live acceptance was on Revit 2027. The reporters' Revit 2025/2026 environments were not tested. Build coverage does not establish runtime acceptance on those versions. See the [automated test guide](../tests/RvtMcp.Tests/README.md) for reproducible unit coverage.
