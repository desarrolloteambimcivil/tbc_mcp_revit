# As-built network from a drawing   (argument: request = the network and the drawing to use)

Goal: bring one piped network in the model into line with an as-built drawing (DWG): compare first, agree the rules with me, then rebuild or correct, connect and verify. Nothing is written until I confirm the plan.
Network and drawing: {request}
Session: {mode}

Before you start
- Call revit_get_current_target. If no target is pinned or more than one Revit is open, call revit_list_available_targets and ask me which instance. In writable sessions, pin it with revit_switch_target. If no document is open, stop and ask.
- Find the drawing: revit_list_linked_cad for a linked DWG. If an AutoCAD/Civil 3D MCP server (tbc-acad-mcp, dwg_* tools) is connected, read the drawing there; otherwise ask me for the data.
- Ask how the model separates networks. If revit_list_view_filters is exposed, read the filter rules: a network is often selected by an instance value such as Comments. New and edited elements must carry exactly that value; keep notes and traceability out of it.

Read the drawing
1. Take images zone by zone (dwg_capture_view_image, or revit_capture_view_image on a view with the link) and read every zone before trusting any automatic pairing. The images show what text-to-element distance cannot: which structure a level label belongs to, where leaders point, and which way the flow arrows run.
2. Read pipes by layer and texts as plain text. Layer names often encode the diameter (a layer ending 031 can mean Ø315, 016 Ø160). A leader label belongs to the element its arrow touches, not to the nearest pipe. When layer, label and drawn line type disagree, list the conflict.
3. Assign levels with care. Structure labels (rim, pipe or invert level) usually sit at a fixed side of their structure, the same for the whole drawing; check the pattern on the images. Entry, exit and connection labels belong to the pipe end at that structure, along the pipe's direction. A pipe that would enter below the outlet of its structure, or a level 1 m off its neighbours, is a probable typo: flag it and propose the obvious correction instead of applying it silently.
4. Map drawing coordinates into the model with revit_map_cad_link_points, calibrated against several drawn curves; report the fit.

Compare, then agree
5. Compare drawing and model before changing anything: pipes in the same position, displaced, only matching another network, missing in the model, and model pipes that the drawing does not show. Compare levels where both exist. Report this as a table.
6. Agree the rules with me before writing, in particular:
   - missing levels: the structure's pipe level, otherwise a slope such as 1 % from the known end;
   - which drawing wins when labels conflict;
   - structure sizes and top levels the drawing does not give;
   - whether to correct in place or rebuild the network.
   Every assumption is written into the report, not into the model.

Write and verify (writable sessions, after I confirm)
7. Make the changes in a few named transactions, one per stage, so each is a single Revit Undo step. Pipe centrelines sit at invert + inner radius. Consecutive segments end at exactly the same point. Structures get their bottom and top from the agreed levels; check each family's level and offset parameters by reading one instance back, because a top constraint may be measured from another level.
8. Join the network with revit_connect_pipe_network: dryRun first, then dryRun: false after I confirm.
9. Verify against the drawing: pipe invert differences, coverage in plan, open ends, the view filter counts, and an image of the result. Report numbers, not impressions.

Report
- Tables: pipes (drawing handle, model ID, diameter, inverts and their source), structures (bottom, top, source), and everything not modelled with the reason.
- The assumptions in bold, the probable drawing errors, and the open questions for me.
- That nothing has been saved, and which Undo steps revert each stage.

Do not
- Do not write before I confirm the plan. In a read-only session stop after step 6.
- Do not write notes into a parameter that view filters use. Do not delete elements of another network.
- Do not save, synchronise or close the model.
