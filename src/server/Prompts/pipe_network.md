# Pipe network connections   (argument: request = the network or area to check)

Goal: make a piping network continuous. Wherever pipes meet each other without a structure, the junction gets a fitting, so Revit reads the network as one system. Nothing is written until I confirm the concrete plan.
Network or area: {request}
Session: {mode}

Rules
- A structure is the junction. Pipe ends inside a manhole, chamber, gully, channel, tank, separator or similar family stay open; do not add a fitting there and do not report those ends as defects. If it is unclear which families count as structures in this model, ask me.
- Pipes that meet without a structure need a fitting: 2 pipes → elbow (union when they are collinear), 3 → tee, 4 → cross. Different diameters need a transition. Short connection pipes from gutters, gullies or downpipes count as pipes.
- When you create pipes yourself, end consecutive segments of one drawn line at exactly the same point (plan position and level), so the fitting can be inserted without moving them. Then add the fittings in the same task, without being asked.
- Levels and positions are evidence from the drawings. Do not move or trim pipes to make a fitting fit; report the junction instead. Very small deflections (a few degrees) may refuse an elbow: report them rather than forcing one.
- View filters may select network elements by an instance value such as Comments. If revit_list_view_filters is exposed, check the filter rules and give each new fitting the same filter value as the pipes it joins, so it does not drop out of filtered views.

Before you start
- Call revit_get_current_target. If no target is pinned or more than one Revit is open, call revit_list_available_targets and ask me which instance. In writable sessions, pin it with revit_switch_target. If no document is open, stop and ask.
- Resolve the scope with me: system name, area or element IDs. Never treat an empty request as every system in the model.

Steps
1. revit_connect_pipe_network with the scope (systemName or elementIds) and dryRun: true → planned junctions with their fitting kind, ends inside structures, and unresolved ends with a reason. The tool treats generic models and plumbing/mechanical equipment as structures; if the model uses other families for manholes or chambers, tell me.
2. Show the plan as a table and ask me to confirm: junction | pipes (IDs, diameters) | fitting kind | deflection. List ends inside structures and unresolved ends separately.
3. After I confirm, repeat the same call with dryRun: false. It copies the pipes' Comments to each fitting when they agree; set any other filter value with revit_set_element_parameter_values if it is exposed.
4. For each failed or unresolved junction that should still be joined, read revit_get_mep_element_connectors and propose a single revit_create_mep_fitting call, or explain why the junction needs a modelling decision from me.
5. Compare open_ends_before and open_ends_after, and run revit_find_mep_disconnects for the scope. If revit_capture_view_image is exposed, offer a capture of a changed junction.

Without revit_connect_pipe_network (older add-in): find open ends with revit_find_mep_disconnects, group coincident ends from revit_get_mep_element_connectors, classify them by the rules above and create each fitting with revit_create_mep_fitting after I confirm.

Report
- Fittings created (ID, kind, pipes joined), junctions skipped because a structure is there, and anything unresolved with the reason. A failed fitting is reported with Revit's message, not retried with moved pipes.
- Tell me that one revit_connect_pipe_network call is one Revit Undo step, and each revit_create_mep_fitting call is its own.

Do not
- Do not run revit_connect_pipe_network with dryRun: false or create fittings before I have confirmed the plan. In a read-only session, stop at the plan; the write tools are hidden there.
- Do not delete, move, resize or re-slope pipes, and do not change systems or pipe types, unless I ask for it.
- Do not use revit_send_code_to_revit when the typed tools can do the job.
