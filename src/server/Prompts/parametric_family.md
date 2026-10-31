# Parametric family   (argument: request = the family to create or improve)

Goal: create or improve a loadable family so that it stays correct for every type and every instance value, then load it without disturbing the instances already placed. Nothing is written until I confirm the design.
Family: {request}
Session: {mode}

Before you start
- Call revit_get_current_target. If no target is pinned or more than one Revit is open, call revit_list_available_targets and ask me which instance. In writable sessions, pin it with revit_switch_target. If no document is open, stop and ask.
- For a new family with simple geometry, revit_create_family builds it from a JSON spec. Editing an existing family, nested families and constraints need revit_send_code_to_revit.

Read first
1. revit_list_loaded_families and revit_list_family_types_in_family → types, and how many instances use each type. Read the family itself (EditFamily in a read-only send_code call): type and instance parameters, formulas, reference planes, labelled dimensions, forms and their materials. Close it without saving.
2. Note which parameters are instance parameters (a height or depth set per instance) and which reference plane each one moves.

Agree the design
3. Describe in a short paragraph what will change, which parameters drive it, and which types and instance values you will test. Wait for my confirmation.

Build rules
- Save a copy of the current family to a new file before changing it.
- Tie every new form to the reference planes that move it. Anything that must follow an instance parameter (for example a grate under a variable top) is sketched on, or hosted on, that parameter's reference plane. A form at a fixed height is only right for one value.
- Keep detailed patterns (grates, slots, textures) in a nested family. A family with labelled dimensions and a sketch of many segments raises Revit's performance warning, and the transaction rolls back.
- A void in a family cannot cut a nested family instance through the API. To cover a variable length, repeat a nested module by visibility parameters with formulas and close the remainder with end pieces locked to reference planes.
- Formulas: there is no >= or <=; write not(a < b). Units go inside the formula (250 mm). rounddown gives integer counts; parity is not(n = 2 * rounddown(n / 2)).
- Regenerate after creating reference planes, before dimensioning them.
- Materials as type parameters, so they can be changed without editing the family.

Test, load and verify (writable sessions, after I confirm)
4. Flex in the family document, inside a transaction that you roll back: every type, and at least the smallest, typical and largest value of each instance parameter. Check the extents numerically (bounding boxes of the new forms against the reference planes), not by eye.
5. Save to a new file. Save As over an existing file needs my approval in Revit, so use a new folder instead of overwriting.
6. Load it with overwrite of the family and keep the instances' parameter values (IFamilyLoadOptions with overwriteParameterValues = false). Compare every instance before and after: type, position, bounding box, Comments.
7. Check placed instances numerically, then show an image with revit_capture_view_image (element_ids, isolate).

Report
- What changed in the family, the parameters added, the flex results per type and instance value, the instance check, and where the backup and the new file are.
- That the load is one Revit Undo step and the model has not been saved.

Do not
- Do not write before I confirm the design. In a read-only session stop after step 3.
- Do not change a family's origin, its existing parameters or its category, because placed instances depend on them.
- Do not dismiss Revit warnings to make a load pass.
