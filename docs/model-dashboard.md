# Model snapshots and dashboard

A **snapshot** is a JSON file with the figures of the model at one moment, stamped with the date and time: counts and sums, not an image. A **dashboard** is a self-contained HTML page drawn from one model's snapshots. Taking a snapshot at the end of each working session shows how the model progresses: warnings that go down, networks that get completed, data that gets filled in.

The tools are in the `lint` toolset (included in `--toolsets all`, which the installer sets).

| Tool | Writes | What it does |
|---|---|---|
| `revit_get_model_snapshot` | nothing | Reads the snapshot figures of the active model without saving them. Available in `--read-only`. |
| `revit_create_model_snapshot` | a new local JSON file | Reads the same figures, stamps them and saves them. Returns the key figures and their change since the previous snapshot of the same model. |
| `revit_list_model_snapshots` | nothing | Lists the models with snapshots, or one model's snapshots with their key figures. Needs no Revit. |
| `revit_create_model_dashboard` | a new local HTML file | Draws one model's snapshots. Needs no Revit and works offline. |

## What a snapshot contains

| Section | Audience | Content |
|---|---|---|
| `composition` | client | Model elements by category, level and (in workshared models) workset. |
| `health` | internal | Warnings grouped by description; families and in-place families; imported and linked CAD; linked Revit models; printable views and those not on a sheet; sheets; schedules; file size. |
| `networks` | client | Pipes, conduits, fittings, accessories and structures grouped by the value of `networkParameter` (default `Comments`). For each network: length by nominal diameter, open ends (an end inside a structure of the same network is not open), clashes with every other network and joints without a fitting. |
| `data_quality` | internal | How many pipes, conduits, fittings and accessories have `networkParameter` filled, with a sample of those that do not. |

- Lengths are in metres and diameters in millimetres (nominal).
- Element ids are Revit ids.
- Lists of ids are cut to `sampleLimit` (default 50).
- Clashes use the same check as `revit_find_pipe_clashes`, at `clearanceMm` (default 0). The check is skipped above 5000 pipes and conduits and is then reported as such.
- Structures count in a network only when they carry its value, because generic models are also used for unrelated objects.
- The add-in only reads the model: no transaction is opened and nothing is written by Revit.

## The dashboard

The dashboard shows:
- key figures with their change since the previous snapshot;
- each network's length by diameter, open ends and clashes;
- a matrix of clashes between networks;
- warnings by type, composition and data quality;
- with two or more snapshots, the progress over time;
- with the internal audience, the pending element ids, which can be copied to ask the agent to select them.

`audience=client` keeps only the client sections and leaves out element ids and the model's path hash. `findings` adds the agent's commentary in a separate box, marked as commentary rather than data. It switches between Spanish and English and prints cleanly.

## Where the files are

```
%LOCALAPPDATA%\NourRouas\tbc-revit-mcp\
  snapshots\<model key>\<yyyyMMdd-HHmmss>.json
  dashboards\<model key>-<yyyyMMdd-HHmmss>.html
```

- **Model key:** the model title plus 8 characters of a hash of its path, so two copies with the same title stay apart. The path itself is not stored.
- **Retention:** snapshots are the history and are never deleted automatically. Spill retention does not apply to them.
- **Privacy:** snapshots hold project data (network names, figures, element ids). They stay on the machine; the dashboard loads no scripts or fonts from the internet.
