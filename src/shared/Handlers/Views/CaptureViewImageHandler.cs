using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CaptureViewImageHandler : IRevitCommand
    {
        public string Name => "capture_view_image";
        public string Description => "Export a view to a raster image (png/jpeg). output_path is optional; if provided it must be absolute and inside %TEMP% or %LOCALAPPDATA%\\NourRouas\\tbc-revit-mcp\\captures\\. With element_ids, captures a temporary 3D view boxed around those elements that is discarded afterwards. Returns saved path + pixel size.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""output_path"":{""type"":""string"",""description"":""Optional absolute path inside %TEMP% or %LOCALAPPDATA%\\NourRouas\\tbc-revit-mcp\\captures\\. Defaults to a generated name under captures.""},""pixel_size"":{""type"":""integer"",""default"":1600},""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""],""default"":""png""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Frame a temporary 3D view's section box around these elements""},""padding_mm"":{""type"":""number"",""default"":1000},""isolate"":{""type"":""boolean"",""default"":false}},""required"":[]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var rawOutputPath = req.Value<string>("output_path");
            var pixelSize = req.Value<int?>("pixel_size") ?? 1600;
            var imageFormat = (req.Value<string>("image_format") ?? "png").ToLowerInvariant();

            if (imageFormat != "png" && imageFormat != "jpeg")
                return CommandResult.Fail("image_format must be 'png' or 'jpeg'.");

            var outputPath = CaptureOutputPath.NormalizeOrDefault(rawOutputPath, imageFormat);
            var pathError = CaptureOutputPath.Validate(outputPath);
            if (pathError != null) return CommandResult.Fail(pathError);

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot export a view template.");

            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory)) return CommandResult.Fail("output_path must include a directory.");
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var focusToken = req["element_ids"];
            if (focusToken != null && focusToken.Type != JTokenType.Null)
            {
                if (focusToken.Type != JTokenType.Array || focusToken.Any(t => t.Type != JTokenType.Integer))
                    return CommandResult.Fail("element_ids must be an array of integer element IDs.");
                var focusIds = focusToken.Select(t => t.Value<long>()).Distinct().ToList();
                if (focusIds.Count > 0)
                    return CaptureFocused(doc, view, focusIds, req.Value<double?>("padding_mm") ?? 1000.0,
                        req.Value<bool?>("isolate") ?? false, outputPath, pixelSize, imageFormat);
            }

            var exportError = Export(doc, view, outputPath, pixelSize, imageFormat, out var actualPath);
            if (exportError != null) return CommandResult.Fail(exportError);

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                saved_path = actualPath,
                pixel_size = pixelSize,
                image_format = imageFormat
            });
        }

        private static string Export(Document doc, View view, string outputPath, int pixelSize, string imageFormat, out string actualPath)
        {
            var options = new ImageExportOptions
            {
                FilePath = outputPath,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixelSize,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = imageFormat == "jpeg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ShadowViewsFileType = imageFormat == "jpeg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ExportRange = ExportRange.SetOfViews
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            string exportError = null;
            try
            {
                doc.ExportImage(options);
            }
            catch (Exception ex)
            {
                exportError = ex.Message;
            }

            actualPath = CaptureOutputResolver.FindActualOutput(outputPath, imageFormat);
            if (actualPath != null) return null;
            return !string.IsNullOrEmpty(exportError)
                ? $"ExportImage failed: {exportError}"
                : "ExportImage completed but output file was not found.";
        }

        /// <summary>
        /// Captures a temporary isometric view whose section box frames the given elements. The view is
        /// created inside a TransactionGroup that is always rolled back, so the model keeps no new view.
        /// Orientation, detail level and display style follow the source view when it is a 3D view.
        /// </summary>
        private static CommandResult CaptureFocused(Document doc, View sourceView, List<long> elementIds, double paddingMm,
            bool isolate, string outputPath, int pixelSize, string imageFormat)
        {
            if (paddingMm < 0) return CommandResult.Fail("padding_mm cannot be negative.");
            var ids = new List<ElementId>();
            var missing = new List<long>();
            BoundingBoxXYZ box = null;
            foreach (var rawId in elementIds)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(rawId));
                var elementBox = element?.get_BoundingBox(null);
                if (elementBox == null) { missing.Add(rawId); continue; }
                ids.Add(element.Id);
                var min = elementBox.Transform.OfPoint(elementBox.Min);
                var max = elementBox.Transform.OfPoint(elementBox.Max);
                box = box == null
                    ? new BoundingBoxXYZ { Min = Min(min, max), Max = Max(min, max) }
                    : new BoundingBoxXYZ { Min = Min(box.Min, Min(min, max)), Max = Max(box.Max, Max(min, max)) };
            }
            if (box == null) return CommandResult.Fail("None of element_ids exists with model geometry: " + string.Join(", ", missing) + ".");

            var pad = UnitUtils.ConvertToInternalUnits(paddingMm, UnitTypeId.Millimeters);
            box.Min -= new XYZ(pad, pad, pad);
            box.Max += new XYZ(pad, pad, pad);

            var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (viewType == null) return CommandResult.Fail("The model has no 3D view type to create a temporary capture view.");

            using (var group = new TransactionGroup(doc, "MCP: focused capture"))
            {
                group.Start();
                try
                {
                    View3D temporary;
                    using (var tx = new Transaction(doc, "MCP: temporary capture view"))
                    {
                        tx.Start();
                        temporary = View3D.CreateIsometric(doc, viewType.Id);
                        if (sourceView is View3D source3D && !source3D.IsPerspective)
                            temporary.SetOrientation(source3D.GetOrientation());
                        if (sourceView.ViewType != ViewType.DrawingSheet && sourceView.ViewType != ViewType.Schedule)
                        {
                            // A default view template on the 3D view type may lock these; keep its values then.
                            try { temporary.DetailLevel = sourceView.DetailLevel; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                            try { temporary.DisplayStyle = sourceView.DisplayStyle; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                        }
                        temporary.IsSectionBoxActive = true;
                        temporary.SetSectionBox(box);
                        if (isolate)
                        {
                            temporary.IsolateElementsTemporary(ids);
                            temporary.ConvertTemporaryHideIsolateToPermanent();
                        }
                        if (tx.Commit() != TransactionStatus.Committed)
                            return CommandResult.Fail("Could not create the temporary capture view.");
                    }

                    var exportError = Export(doc, temporary, outputPath, pixelSize, imageFormat, out var actualPath);
                    if (exportError != null) return CommandResult.Fail(exportError);
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(sourceView.Id),
                        saved_path = actualPath,
                        pixel_size = pixelSize,
                        image_format = imageFormat,
                        focus = new
                        {
                            element_ids = ids.Select(RevitCompat.GetId).ToList(),
                            missing_element_ids = missing,
                            padding_mm = paddingMm,
                            isolate,
                            temporary_view_discarded = true
                        }
                    });
                }
                finally
                {
                    if (group.HasStarted()) group.RollBack();
                }
            }
        }

        private static XYZ Min(XYZ a, XYZ b) => new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        private static XYZ Max(XYZ a, XYZ b) => new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    }
}
