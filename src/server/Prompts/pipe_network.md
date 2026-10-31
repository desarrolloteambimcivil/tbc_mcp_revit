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
1. revit_find_mep_disconnects with domainFilter: piping → open pipe ends in scope.
2. revit_get_mep_element_connectors for the pipes involved → connector IDs, positions (mm) and diameters. Group open ends that coincide within a few millimetres.
3. For each group, check whether a structure sits at that point. Classify every open end: needs fitting (with the kind), inside a structure, or unresolved (lone end, gap, diameter or level mismatch).
4. Show the plan as a table and ask me to confirm: junction | pipes (IDs, diameters) | fitting kind | reason. List skipped and unresolved ends separately.
5. After I confirm, call revit_create_mep_fitting once per junction with the connector IDs from step 2. Set the filter value on each new fitting when the rules above require it and revit_set_element_parameter_values is exposed.
6. Run revit_find_mep_disconnects again and compare with step 1. If revit_capture_view_image is exposed, offer a capture of a changed junction.

Report
- Fittings created (ID, kind, pipes joined), junctions skipped because a structure is there, and anything unresolved with the reason. A failed fitting is reported with Revit's message, not retried with moved pipes.
- Tell me each fitting is its own Revit Undo step unless they were created in one call.

Do not
- Do not create fittings before I have confirmed the plan. In a read-only session, stop at the plan.
- Do not delete, move, resize or re-slope pipes, and do not change systems or pipe types, unless I ask for it.
- Do not use revit_send_code_to_revit when the typed tools can do the job.
