using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("mep")]
    public class MepTools
    {
        [McpServerTool(Name = "revit_detect_system_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Walk an MEP system and page IDs per category with startElement + maxElements (hard max 5000).")]
        public static async Task<string> DetectSystemElements(long elementId, int startElement = 0, int maxElements = 500)
            => await ToolGateway.CallAsync("detect_system_elements", new { elementId, start_element = startElement, max_elements = maxElements });

        [McpServerTool(Name = "revit_create_duct", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an HVAC duct between two points (mm). ductTypeId/systemTypeId/levelId default to first available / nearest level. Provide diameter for round duct OR width+height (mm) for rectangular.")]
        public static async Task<string> CreateDuct(double startX, double startY, double startZ, double endX, double endY, double endZ, long? ductTypeId = null, long? systemTypeId = null, long? levelId = null, double? width = null, double? height = null, double? diameter = null)
            => await ToolGateway.CallAsync("create_duct", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, duct_type_id = ductTypeId, system_type_id = systemTypeId, level_id = levelId, width, height, diameter });

        [McpServerTool(Name = "revit_create_pipe", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a pipe (mm). With systemTypeId omitted, a unique open piping End connector within 1 mm of start supplies system type and diameter and is connected immediately. Multiple matches or conflicting diameter fail without creating. Use startElementId/startConnectorId (Connector.Id) to disambiguate; omit systemTypeId with these selectors. No match falls back to first system type. Explicit systemTypeId creates an independent pipe. Result reports actual type/diameter and system_type_source=connector|explicit|default. pipeTypeId defaults to first available; levelId to nearest level. End consecutive segments at the same point and join pipes that meet outside a structure with revit_connect_pipe_network.")]
        public static async Task<string> CreatePipe(double startX, double startY, double startZ, double endX, double endY, double endZ, long? pipeTypeId = null, long? systemTypeId = null, long? levelId = null, double? diameter = null, long? startElementId = null, int? startConnectorId = null)
            => await ToolGateway.CallAsync("create_pipe", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, pipe_type_id = pipeTypeId, system_type_id = systemTypeId, level_id = levelId, diameter, start_element_id = startElementId, start_connector_id = startConnectorId });

        [McpServerTool(Name = "revit_create_cable_tray", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an electrical cable tray between two points (mm). cableTrayTypeId/levelId default to first available / nearest level. Optional width+height (mm).")]
        public static async Task<string> CreateCableTray(double startX, double startY, double startZ, double endX, double endY, double endZ, long? cableTrayTypeId = null, long? levelId = null, double? width = null, double? height = null)
            => await ToolGateway.CallAsync("create_cable_tray", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, cable_tray_type_id = cableTrayTypeId, level_id = levelId, width, height });

        [McpServerTool(Name = "revit_create_conduit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an electrical conduit between two points (mm). conduitTypeId/levelId default to first available / nearest level. Optional diameter (mm).")]
        public static async Task<string> CreateConduit(double startX, double startY, double startZ, double endX, double endY, double endZ, long? conduitTypeId = null, long? levelId = null, double? diameter = null)
            => await ToolGateway.CallAsync("create_conduit", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, conduit_type_id = conduitTypeId, level_id = levelId, diameter });

        [McpServerTool(Name = "revit_create_air_terminal", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place an air terminal (diffuser/grille) family instance at a point (mm). typeId must be an Air Terminal FamilySymbol. Optional hostId for hosted placement on a duct/ceiling.")]
        public static async Task<string> CreateAirTerminal(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
            => await ToolGateway.CallAsync("create_air_terminal", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });

        [McpServerTool(Name = "revit_create_lighting_fixture", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place a lighting fixture family instance at a point (mm). typeId must be a Lighting Fixture FamilySymbol. Optional hostId for hosted placement on a ceiling.")]
        public static async Task<string> CreateLightingFixture(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
            => await ToolGateway.CallAsync("create_lighting_fixture", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });

        [McpServerTool(Name = "revit_list_mep_systems", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all MEP systems (mechanical/HVAC, piping/plumbing, electrical). domainFilter: all|mechanical|piping|electrical. element_count is pipes and fittings, ducts and fittings, or electrical circuit members. terminal_count excludes base equipment. Membership read failures return an error, never zero counts. Returns id, name, domain, system type, both counts, and connectivity status.")]
        public static async Task<string> ListMepSystems(string domainFilter = "all", int limit = 1000)
            => await ToolGateway.CallAsync("list_mep_systems", new { domain_filter = domainFilter, limit });

        [McpServerTool(Name = "revit_get_system_inventory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Return the full element inventory of one MEP system: network members, terminals, and base equipment, deduplicated by element ID, with category/type plus a category breakdown. Membership read failures return an error. Identify by systemId or systemName.")]
        public static async Task<string> GetSystemInventory(long? systemId = null, string systemName = "", bool includeParameters = false, int limit = 2000)
            => await ToolGateway.CallAsync("get_system_inventory", new { system_id = systemId, system_name = systemName, include_parameters = includeParameters, limit });

        [McpServerTool(Name = "revit_get_mep_element_connectors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Inspect all connectors on an MEP element (duct/pipe/fitting/equipment/terminal): domain, shape, position (mm), connection status, flow, direction.")]
        public static async Task<string> GetMepElementConnectors(long elementId)
            => await ToolGateway.CallAsync("get_mep_element_connectors", new { element_id = elementId });

        [McpServerTool(Name = "revit_connect_mep_elements", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change MEP connectivity; restore connections with Revit Undo. Connect physical connectors of two MEP elements with matching domains. Different assigned piping/HVAC system type IDs are rejected with connected=false and both types; unassigned equipment ports are allowed. A connection does not promise system merging. Optionally pin connectorIndex1/connectorIndex2 using Connector.Id, not ordinals. An existing direct connection or connection through one shared pipe/duct fitting is returned as already_connected=true without mutation.")]
        public static async Task<string> ConnectMepElements(long elementId1, long elementId2, long? connectorIndex1 = null, long? connectorIndex2 = null)
            => await ToolGateway.CallAsync("connect_mep_elements", new { element_id_1 = elementId1, element_id_2 = elementId2, connector_index_1 = connectorIndex1, connector_index_2 = connectorIndex2 });

        [McpServerTool(Name = "revit_create_mep_fitting", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Insert an MEP fitting at connectors of existing MEP elements. fittingKind: elbow|tee|union|cross|transition. connectors is a JSON array of {element_id, connector_index} where connector_index is the connector_id from get_mep_element_connectors: elbow/union/transition need 2, tee 3, cross 4.")]
        public static async Task<string> CreateMepFitting(string fittingKind, string connectors)
            => await ToolGateway.CallWithParamsAsync("create_mep_fitting", () =>
            {
                var parsedConnectors = JArray.Parse(connectors);
                return new { fitting_kind = fittingKind, connectors = parsedConnectors };
            });

        [McpServerTool(Name = "revit_connect_pipe_network", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change MEP connectivity; restore it with Revit Undo. Fittings trim the pipes they join. Join pipes that meet outside a structure. Open pipe End connectors within toleranceMm are grouped: 2 → elbow, or union/transition when straight; 3 → tee; 4 → cross; a lone end on another pipe's axis → that pipe is split and joined with a tee. Ends inside the extent of a generic model or plumbing/mechanical equipment (manhole, chamber, gully, tank; + structureMarginMm) stay open; a structure whose Comments differ from the pipe's belongs to another network and is ignored. Pipes are never moved; refused fittings are listed in failed. Scope with systemName (system or system-type name prefix) or elementIds. dryRun=true (default) returns the plan only. New fittings copy the pipes' Comments when they all agree.")]
        public static async Task<string> ConnectPipeNetwork(string systemName = null, long[] elementIds = null, double toleranceMm = 5, double structureMarginMm = 100, bool copyComments = true, bool dryRun = true, int limit = 200)
            => await ToolGateway.CallAsync("connect_pipe_network", new { system_name = systemName, element_ids = elementIds, tolerance_mm = toleranceMm, structure_margin_mm = structureMarginMm, copy_comments = copyComments, dry_run = dryRun, limit });

        [McpServerTool(Name = "revit_find_pipe_clashes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find clashes between straight pipes and conduits from centrelines and outer diameters (exact for round sections), closer than clearanceMm. Pairs joined through a fitting, ends meeting at a point, and crossings inside a structure of their own network (same Comments) are not clashes. Scope with systemName (system or system-type prefix) or elementIds; against=all compares with every pipe and conduit, against=scope only within the scope. Each clash reports the crossing point, overlap, both centre levels at the crossing, each curve's ends (z, joined) and the vertical move at the crossing that would clear it (raise/lower for a and b).")]
        public static async Task<string> FindPipeClashes(string systemName = null, long[] elementIds = null, string against = "all", double clearanceMm = 0, double structureMarginMm = 100, int limit = 200)
            => await ToolGateway.CallAsync("find_pipe_clashes", new { system_name = systemName, element_ids = elementIds, against, clearance_mm = clearanceMm, structure_margin_mm = structureMarginMm, limit });

        [McpServerTool(Name = "revit_resolve_pipe_clash", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change a pipe's slope; restore it with Revit Undo. Clears one clash by moving the free end of elementId vertically while fixedEnd (start|end of its curve) stays, so it passes side=above|below otherId with clearanceMm. Refused when the moved end is joined to a fitting, the end move exceeds maxEndMoveMm, the crossing is next to the fixed end, or the fall would reverse (keepFallDirection). Choose the curve and end whose level is assumed or free: levels from drawings stay. dryRun=true (default) reports inverts and slope before/after and any clash the new position would create.")]
        public static async Task<string> ResolvePipeClash(long elementId, long otherId, string fixedEnd, string side, double clearanceMm = 50, double maxEndMoveMm = 1000, bool keepFallDirection = true, bool dryRun = true)
            => await ToolGateway.CallAsync("resolve_pipe_clash", new { element_id = elementId, other_id = otherId, fixed_end = fixedEnd, side, clearance_mm = clearanceMm, max_end_move_mm = maxEndMoveMm, keep_fall_direction = keepFallDirection, dry_run = dryRun });

        [McpServerTool(Name = "revit_set_system_classification", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change MEP system membership; restore it with Revit Undo. Add MEP elements to an existing duct/piping system. If systemId omitted, only reports current system membership (read-only). elementIds is an array of MEP element ids.")]
        public static async Task<string> SetSystemClassification(long[] elementIds, long? systemId = null)
            => await ToolGateway.CallAsync("set_system_classification", new { element_ids = elementIds, system_id = systemId });

        [McpServerTool(Name = "revit_get_panel_schedule", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read panel metadata and a bounded circuit page. Use startCircuit + maxCircuits (hard max 1000).")]
        public static async Task<string> GetPanelSchedule(long? panelId = null, string panelName = "", int startCircuit = 0, int maxCircuits = 100)
            => await ToolGateway.CallAsync("get_panel_schedule", new { panel_id = panelId, panel_name = panelName, start_circuit = startCircuit, max_circuits = maxCircuits });

        [McpServerTool(Name = "revit_find_mep_disconnects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find MEP elements with open/unconnected End connectors (potential gaps in ductwork, piping, conduit). domainFilter: all|mechanical|piping|electrical. viewOnly restricts to the active view.")]
        public static async Task<string> FindMepDisconnects(string domainFilter = "all", bool viewOnly = false, int limit = 2000)
            => await ToolGateway.CallAsync("find_mep_disconnects", new { domain_filter = domainFilter, view_only = viewOnly, limit });

        [McpServerTool(Name = "revit_analyze_mep_network", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Analyze one MEP system. For piping and HVAC, element_count, category_breakdown, and open_connector_count come from the pipe or duct network; terminal_count excludes base equipment. Empty-system advice requires both counts zero and no base equipment. Membership read failures return an error. Electrical element_count is the circuit members. Identify by systemId or systemName.")]
        public static async Task<string> AnalyzeMepNetwork(long? systemId = null, string systemName = "")
            => await ToolGateway.CallAsync("analyze_mep_network", new { system_id = systemId, system_name = systemName });
    }
}
